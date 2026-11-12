using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public static class GitHubPocCommand
{
    public const string Confirmation = "POC-249-ONE-REPAIR-NO-MERGE";

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) =>
        RunAsync(args, output, error, Environment.GetEnvironmentVariable, CreateClient);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string, string?> read, Func<string, HttpClient> createClient)
    {
        try
        {
            if (!args.Contains("--execute", StringComparer.Ordinal) ||
                args.Any(arg => arg is "--dry-run" or "--replay" or "--context"))
                throw new InvalidDataException("PoC requires explicit execution from trusted Actions input.");
            var path = read("GITHUB_EVENT_PATH");
            if (string.IsNullOrWhiteSpace(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Missing or oversized PoC notification.");
            using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = payload.RootElement;
            var settings = GitHubPocSettings.Read(read, DateTimeOffset.UtcNow);
            var inputs = root.GetProperty("inputs");
            var operation = inputs.GetProperty("operation").GetString();
            if (read("GITHUB_EVENT_NAME") != "workflow_dispatch" ||
                read("GITHUB_REF") != GitHubPocSettings.DefinitionReference ||
                read("GITHUB_WORKFLOW_REF") !=
                    $"{settings.Repository}/{GitHubPocSettings.DriverWorkflowPath}@{GitHubPocSettings.DefinitionReference}" ||
                inputs.GetProperty("issue").GetString() != GitHubPocSettings.IssueNumber.ToString(CultureInfo.InvariantCulture) ||
                inputs.GetProperty("confirmation").GetString() != Confirmation ||
                root.GetProperty("sender").GetProperty("id").GetInt64() != settings.ActorId ||
                root.GetProperty("sender").GetProperty("type").GetString() != "User" ||
                operation is not ("poc-bootstrap" or "poc-start" or "resume" or "classify-review" or "pause"))
                throw new InvalidDataException("PoC invocation does not match the authorized manual profile.");
            if (operation == "poc-bootstrap" && !settings.BootstrapApproved)
                throw new InvalidOperationException("PoC bootstrap needs its separate one-time approval.");

            var workflowToken = read("GITHUB_TOKEN");
            var userToken = read("ORCHESTRATION_USER_TOKEN");
            if (string.IsNullOrWhiteSpace(workflowToken) || string.IsNullOrWhiteSpace(userToken) ||
                workflowToken == userToken)
                throw new InvalidOperationException("Separate PoC credentials are unavailable.");
            using var client = createClient(workflowToken);
            using var userClient = createClient(userToken);
            var api = new GitHubRepositoryApi(client, settings.Repository, settings.RepositoryId);
            var taskApi = new GitHubRepositoryApi(userClient, settings.Repository, settings.RepositoryId);
            var remaining = settings.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("PoC authorization expired.");
            using var deadline = new CancellationTokenSource(remaining);
            var token = deadline.Token;
            var publisher = await GitHubLiveCredentials.VerifyAsync(api, taskApi, settings.ActorId, token);
            var runId = read("GITHUB_RUN_ID") ?? throw new InvalidDataException("Missing PoC run ID.");
            var occurredAt = await VerifyRunAsync(api, settings, runId, token);
            await VerifyDefinitionAsync(api, settings.DefinitionSha, token);

            var transport = new GitHubGitTransport(client, settings.Repository, allowMutations: true,
                stateReference: GitHubPocSettings.StateReference, allowIsolatedInitialization: operation == "poc-bootstrap");
            IStateStore store = new GitStateStore(transport, settings.Repository, GitHubPocSettings.StateReference,
                allowInitialization: operation == "poc-bootstrap");
            var snapshot = await store.LoadAsync(token);
            if (operation == "poc-bootstrap")
            {
                var ingress = await api.ReadIngressEvidenceAsync(root, "workflow_dispatch", runId,
                    [publisher.ActorId], token, GitHubPocSettings.DriverWorkflowPath);
                if (ingress.ActorId != settings.ActorId || ingress.ActorType != "User" ||
                    ingress.Permission is not ("admin" or "maintain" or "write") || !ingress.IssueOpen || ingress.IsPullRequest)
                    throw new UnauthorizedAccessException("PoC bootstrap requires its authorized human and open fixture Issue.");
                if (snapshot.Revision is not null)
                    throw new InvalidOperationException("PoC state already exists; bootstrap cannot be repeated.");
                snapshot.State.IsolatedTestManifestDigest = settings.ManifestDigest;
                if (!await store.TrySaveAsync(snapshot, snapshot.State, token))
                    throw new InvalidOperationException("PoC bootstrap conflicted; inspect the state before continuing.");
                store = new PocStateStore(store, settings.Repository, GitHubPocSettings.IssueNumber, settings.ManifestDigest);
                await VerifyDefinitionAsync(api, settings.DefinitionSha, token);
                await WriteEvidenceAsync(output, store, operation, null, null, token);
                return 0;
            }

            store = new PocStateStore(store, settings.Repository, GitHubPocSettings.IssueNumber, settings.ManifestDigest);
            snapshot = await store.LoadAsync(token);
            var repositoryGeneration = long.Parse(inputs.GetProperty("repository_generation").GetString()!,
                CultureInfo.InvariantCulture);
            var issueGeneration = long.Parse(inputs.GetProperty("issue_generation").GetString()!,
                CultureInfo.InvariantCulture);
            if (repositoryGeneration != snapshot.State.Generation ||
                issueGeneration != (snapshot.State.Issues.GetValueOrDefault(GitHubPocSettings.IssueNumber)?.Generation ?? 1))
                throw new InvalidDataException("PoC command generations differ from canonical state.");
            if (operation == "poc-start" && !snapshot.State.Inbox.ContainsKey($"actions:{settings.RepositoryId}:{runId}") &&
                (snapshot.State.Issues.Count != 0 || snapshot.State.Outbox.Count != 0 || snapshot.State.Inbox.Count != 0))
                throw new InvalidOperationException("PoC start is allowed once on fresh state; use resume for observation.");
            if (operation != "poc-start" && !snapshot.State.Issues.ContainsKey(GitHubPocSettings.IssueNumber))
                throw new InvalidOperationException("PoC has not been started.");

            var host = new GitHubOrchestrationHost(api, store, taskApi: taskApi);
            var result = await host.ReconcileAsync(root, "workflow_dispatch", runId, repositoryGeneration,
                issueGeneration, occurredAt, [publisher.ActorId], publisher.ActorId, GitHubPocSettings.BaseBranch,
                settings.ActorId, settings.Tasks, token: token, reviewApproval: settings.Review,
                ciRoute: settings.CiRoute, ciApproval: settings.Ci,
                boundedScope: new(GitHubPocSettings.IssueNumber, 1, SinglePassTest: false),
                commandOnly: operation == "pause", trustedWorkflowPath: GitHubPocSettings.DriverWorkflowPath,
                requireDraftPullRequest: true,
                beforeEffect: effectToken => VerifyDefinitionAsync(api, settings.DefinitionSha, effectToken));
            var candidate = new GitHubTaskAdapter(api, store, GitHubPocSettings.BaseBranch, settings.ActorId,
                settings.Tasks, taskApi: taskApi, requireDraftPullRequest: true);
            await VerifyDefinitionAsync(api, settings.DefinitionSha, token);
            await WriteEvidenceAsync(output, store, operation!, result, candidate, token);
            var final = (await store.LoadAsync(token)).State.Issues[GitHubPocSettings.IssueNumber];
            return result.Reason == WorkerStopReason.Blocked &&
                final.HoldReason != "Review requires human classification." ? 1 : 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException or
            HttpRequestException or OperationCanceledException or FormatException or OverflowException)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = "github-poc-failure", ErrorType = exception.GetType().Name,
                Issue = GitHubPocSettings.IssueNumber, StateRef = GitHubPocSettings.StateReference
            }, StateJson.Options));
            error.WriteLine($"PoC stopped ({exception.GetType().Name}); inspect canonical state before continuing. Do not retry an uncertain write.");
            return 1;
        }
    }

    private static async Task<DateTimeOffset> VerifyRunAsync(GitHubRepositoryApi api, GitHubPocSettings settings,
        string runId, CancellationToken token)
    {
        using var run = await api.GetAsync($"actions/runs/{runId}", token);
        var root = run.RootElement;
        if (root.GetProperty("id").ToString() != runId ||
            root.GetProperty("workflow_id").GetInt64() != settings.DriverWorkflowId ||
            root.GetProperty("path").GetString() != GitHubPocSettings.DriverWorkflowPath ||
            root.GetProperty("head_sha").GetString() != settings.DefinitionSha ||
            root.GetProperty("event").GetString() != "workflow_dispatch" ||
            root.GetProperty("repository").GetProperty("id").GetInt64() != settings.RepositoryId ||
            root.GetProperty("run_attempt").GetInt32() != 1 ||
            root.GetProperty("actor").GetProperty("id").GetInt64() != settings.ActorId ||
            root.GetProperty("triggering_actor").GetProperty("id").GetInt64() != settings.ActorId)
            throw new InvalidDataException("PoC run definition, actor or attempt does not match authorization.");
        return root.GetProperty("created_at").GetDateTimeOffset();
    }

    internal static async Task VerifyDefinitionAsync(GitHubRepositoryApi api, string definitionSha,
        CancellationToken token)
    {
        using var definition = await api.GetAsync(
            "commits/" + Uri.EscapeDataString(GitHubPocSettings.DefinitionReference), token);
        if (definition.RootElement.GetProperty("sha").GetString() != definitionSha)
            throw new InvalidDataException("PoC master definition moved.");
    }

    internal static async Task WriteEvidenceAsync(TextWriter output, IStateStore store, string operation,
        WorkerResult? result, GitHubTaskAdapter? candidate, CancellationToken token)
    {
        var snapshot = await store.LoadAsync(token);
        var issue = snapshot.State.Issues.GetValueOrDefault(GitHubPocSettings.IssueNumber);
        var completed = await IsCompleteAsync(snapshot.State, issue, candidate, token);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Mode = "github-poc", Operation = operation, Issue = GitHubPocSettings.IssueNumber,
            StateRef = GitHubPocSettings.StateReference, StateRevision = snapshot.Revision,
            ManifestDigest = snapshot.State.IsolatedTestManifestDigest, snapshot.State.Version,
            Phase = issue?.Phase.ToString(), issue?.PullRequest, issue?.HeadSha, issue?.HoldReason,
            issue?.CurrentHandoffId, issue?.Round, StopReason = result?.Reason.ToString(),
            Tasks = issue?.Tasks.Values.Select(task => new
                { task.Id, task.Kind, task.Status, task.ExternalId, task.SourceSha, task.FinalSha }).ToArray(),
            Ci = issue?.Ci,
            Review = issue?.Review is { } review ? new
                { review.IntentId, review.ReviewId, review.Sha, review.Classification, review.RequestBoundary } : null,
            Effects = snapshot.State.Outbox.Values.Where(effect =>
                effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview)
                .Select(effect => new { effect.Id, effect.Kind, effect.Status, effect.TaskId, effect.Sha,
                    effect.ExternalId, effect.CreatedAt, effect.CompletedAt }).ToArray(),
            Completed = completed,
            MergeEnabled = false, RequiredStatusPublicationEnabled = false
        }, StateJson.Options));
    }

    internal static async Task<bool> IsCompleteAsync(RepositoryState state, IssueState? issue,
        GitHubTaskAdapter? candidate, CancellationToken token)
    {
        if (issue?.Phase != Phase.HUMAN_REVIEW ||
            issue.Review?.Classification != ReviewClassification.Accepted ||
            issue.Ci?.Green != true || issue.Ci.Sha != issue.HeadSha || issue.Review.Sha != issue.HeadSha)
            return false;
        if (candidate is null)
            throw new InvalidDataException("PoC completion requires live candidate validation.");
        await candidate.ValidateCandidateShaAsync(state, issue, token);
        return true;
    }

    private static HttpClient CreateClient(string token)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
