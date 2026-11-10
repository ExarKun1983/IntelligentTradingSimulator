using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public static class GitHubIsolatedStatusTestCommand
{
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) =>
        RunAsync(args, output, error, Environment.GetEnvironmentVariable, CreateClient);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string, string?> read, Func<string, HttpClient> createClient)
    {
        HttpClient? client = null;
        HttpClient? userClient = null;
        IStateStore? diagnosticStore = null;
        var diagnosticIssueNumber = 0;
        string? diagnosticOperation = null;
        string? diagnosticManifestDigest = null;
        try
        {
            if (!args.Contains("--execute", StringComparer.Ordinal) || args.Contains("--dry-run", StringComparer.Ordinal) ||
                args.Contains("--replay", StringComparer.Ordinal) || args.Contains("--context", StringComparer.Ordinal))
                throw new InvalidDataException("Isolated status-test mode requires a trusted Actions invocation.");
            var path = read("GITHUB_EVENT_PATH");
            if (string.IsNullOrWhiteSpace(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Missing or oversized Actions notification.");
            using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = payload.RootElement;
            var eventName = read("GITHUB_EVENT_NAME") ??
                throw new InvalidDataException("Missing Actions event.");
            var runId = read("GITHUB_RUN_ID") ??
                throw new InvalidDataException("Missing Actions run identity.");
            var settings = GitHubIsolatedStatusTestSettings.Read(read, DateTimeOffset.UtcNow);
            if (eventName != "workflow_dispatch" ||
                root.GetProperty("inputs").GetProperty("issue").GetString() !=
                    settings.IssueNumber.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Isolated status test accepts only its exact manual Issue dispatch.");
            var expectedWorkflowRef = $"{settings.Repository}/{GitHubIsolatedStatusTestSettings.DriverWorkflowPath}@{GitHubIsolatedStatusTestSettings.DriverReference}";
            if (read("GITHUB_REF") != GitHubIsolatedStatusTestSettings.DriverReference ||
                read("GITHUB_WORKFLOW_REF") != expectedWorkflowRef)
                throw new InvalidDataException("Isolated driver was not run from its immutable reviewed tag.");
            var operation = root.GetProperty("inputs").GetProperty("operation").GetString();
            diagnosticIssueNumber = settings.IssueNumber;
            diagnosticOperation = operation;
            diagnosticManifestDigest = settings.ManifestDigest;
            if (operation is not ("status-test-start" or "pause" or "status-test-bootstrap"))
                throw new InvalidDataException("Unsupported isolated status-test stage.");
            if (operation == "status-test-start" && !settings.Ci.AllowsCi)
                throw new InvalidOperationException("Isolated managed-CI approval is unverified.");
            if (operation == "status-test-bootstrap" && !settings.BootstrapApproved)
                throw new InvalidOperationException("One-time isolated state bootstrap was not separately approved.");

            var userToken = read("ORCHESTRATION_STATUS_TEST_USER_TOKEN");
            var workflowToken = read("GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(userToken) || string.IsNullOrWhiteSpace(workflowToken) ||
                string.Equals(userToken, workflowToken, StringComparison.Ordinal))
                throw new InvalidOperationException("Separate isolated-test credentials are unavailable.");
            client = createClient(workflowToken);
            userClient = createClient(userToken);
            var api = new GitHubRepositoryApi(client, settings.Repository, settings.RepositoryId);
            var taskApi = new GitHubRepositoryApi(userClient, settings.Repository, settings.RepositoryId);
            var publisher = await GitHubLiveCredentials.VerifyAsync(api, taskApi, settings.ActorId);
            var workflowRoute = new GitHubWakeRoute(settings.DriverWorkflowId, settings.DefinitionSha,
                GitHubIsolatedStatusTestSettings.DriverWorkflowPath);
            var occurredAt = await GitHubLiveCommand.VerifyCommandDefinitionAsync(api, workflowRoute, runId, eventName);
            if (operation == "status-test-bootstrap")
            {
                var ingress = await api.ReadIngressEvidenceAsync(root, eventName, runId, [publisher.ActorId], default,
                    GitHubIsolatedStatusTestSettings.DriverWorkflowPath);
                if (ingress.ActorId == publisher.ActorId || ingress.ActorType != "User" ||
                    ingress.Permission is not ("admin" or "maintain" or "write"))
                    throw new UnauthorizedAccessException("Isolated bootstrap requires an authorized human actor.");
                var bootstrapTransport = new GitHubGitTransport(client, settings.Repository, allowMutations: true,
                    stateReference: GitHubIsolatedStatusTestSettings.StateReference, allowIsolatedInitialization: true);
                var bootstrapStore = new GitStateStore(bootstrapTransport, settings.Repository,
                    GitHubIsolatedStatusTestSettings.StateReference, allowInitialization: true);
                diagnosticStore = bootstrapStore;
                var initial = await bootstrapStore.LoadAsync(default);
                if (initial.Revision is not null)
                    throw new InvalidOperationException("Isolated state already exists; bootstrap cannot be repeated.");
                initial.State.IsolatedTestManifestDigest = settings.ManifestDigest;
                if (!await bootstrapStore.TrySaveAsync(initial, initial.State, default))
                    throw new InvalidOperationException("Isolated state bootstrap conflicted; inspect the ref before retrying.");
                var saved = await bootstrapStore.LoadAsync(default);
                if (saved.State.IsolatedTestManifestDigest != settings.ManifestDigest)
                    throw new InvalidDataException("Isolated state bootstrap receipt does not match the reviewed manifest.");
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Mode = "isolated-status-test-bootstrap",
                    StateRef = GitHubIsolatedStatusTestSettings.StateReference,
                    StateVersion = saved.State.Version,
                    ManifestDigest = settings.ManifestDigest
                }, StateJson.Options));
                return 0;
            }
            var transport = new GitHubGitTransport(client, settings.Repository, allowMutations: true,
                stateReference: GitHubIsolatedStatusTestSettings.StateReference);
            IStateStore store = new GitStateStore(transport, settings.Repository,
                GitHubIsolatedStatusTestSettings.StateReference);
            diagnosticStore = store;
            var snapshot = await store.LoadAsync(default);
            if (snapshot.State.IsolatedTestManifestDigest is not null &&
                snapshot.State.IsolatedTestManifestDigest != settings.ManifestDigest)
                throw new InvalidDataException("Isolated test manifest differs from its initialized state.");
            if (snapshot.State.IsolatedTestManifestDigest != settings.ManifestDigest)
                throw new InvalidDataException("Isolated state has not been bootstrapped for this manifest.");
            store = new IsolatedStatusTestStateStore(store, settings.Repository, settings.IssueNumber,
                settings.ManifestDigest);
            var repositoryGeneration = long.Parse(root.GetProperty("inputs").GetProperty("repository_generation")
                .GetString()!, CultureInfo.InvariantCulture);
            var issueGeneration = long.Parse(root.GetProperty("inputs").GetProperty("issue_generation")
                .GetString()!, CultureInfo.InvariantCulture);
            var currentIssueGeneration = snapshot.State.Issues.GetValueOrDefault(settings.IssueNumber)?.Generation ?? 1;
            if (repositoryGeneration != snapshot.State.Generation || issueGeneration != currentIssueGeneration)
                throw new InvalidDataException("Isolated stage generations do not match canonical state.");

            var host = new GitHubOrchestrationHost(api, store, taskApi: taskApi);
            var result = await host.ReconcileAsync(root, eventName, runId, repositoryGeneration,
                issueGeneration, occurredAt, [publisher.ActorId], publisher.ActorId,
                GitHubIsolatedStatusTestSettings.BaseBranch, settings.ActorId, settings.Tasks,
                ciRoute: settings.CiRoute, ciApproval: settings.Ci, statusPublisher: publisher,
                statusApproval: settings.Status, commandOnly: operation == "pause",
                isolatedStatusTest: true,
                trustedWorkflowPath: GitHubIsolatedStatusTestSettings.DriverWorkflowPath,
                isolatedManifestDigest: settings.ManifestDigest);
            var final = await store.LoadAsync(default);
            if (!final.State.Issues.TryGetValue(settings.IssueNumber, out var issue))
                throw new InvalidDataException("Isolated stage did not persist its Issue.");
            var currentStatus = issue.RequiredStatusIntentId is { } statusId
                ? final.State.Outbox.GetValueOrDefault(statusId) : null;
            if (operation == "status-test-start" &&
                (issue.Tasks.Count != 1 || issue.Tasks.Values.Single().Status != TaskStatus.Completed ||
                 issue.Ci?.Green != true || currentStatus?.Status != DeliveryStatus.Succeeded ||
                 currentStatus.RequiredStatus?.State != "success" || issue.Review is not null))
                throw new InvalidOperationException("Isolated start stopped before exact-head CI and required success were durably confirmed.");
            if (operation == "pause" &&
                (!issue.Paused || currentStatus?.Status != DeliveryStatus.Succeeded ||
                 currentStatus.RequiredStatus?.State != "error"))
                throw new InvalidOperationException("Isolated pause did not confirm the corrective exact-head error status.");
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = "isolated-status-test",
                Issue = settings.IssueNumber,
                Operation = operation,
                StateRevision = final.Revision,
                StateVersion = final.State.Version,
                ManifestDigest = settings.ManifestDigest,
                HeadSha = issue.HeadSha,
                TaskHistory = issue.Tasks.Values.OrderBy(task => task.Id, StringComparer.Ordinal)
                    .Select(task => new { task.Id, task.Kind, task.Status, task.ExternalId, task.SourceSha, task.FinalSha })
                    .ToArray(),
                Ci = issue.Ci is { } ci ? new
                {
                    ci.IntentId, ci.RunId, ci.TaskId, ci.Round, ci.Sha, ci.Green, ci.TerminalStopReason,
                    ci.DispatchBoundary
                } : null,
                StatusHistory = final.State.Outbox.Values.Where(effect =>
                        effect.IssueNumber == settings.IssueNumber && effect.Kind == EffectKind.PublishRequiredStatus)
                    .OrderBy(effect => effect.Id, StringComparer.Ordinal)
                    .Select(effect => new { effect.Id, effect.Status, effect.Sha, Payload = effect.RequiredStatus,
                        effect.ExternalId, effect.CreatedAt, effect.CompletedAt,
                        Boundary = effect.RequiredStatusBoundary }).ToArray(),
                StopReason = result.Reason.ToString(),
                ExecutedEffects = result.LastReconciliation?.ExecutedEffect?.Kind.ToString()
            }, StateJson.Options));
            return result.Reason == WorkerStopReason.Blocked ? 1 : 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException or
            HttpRequestException or OperationCanceledException or FormatException or OverflowException)
        {
            try
            {
                if (diagnosticStore is not null && diagnosticIssueNumber > 0)
                {
                    var snapshot = await diagnosticStore.LoadAsync(default);
                    var issue = snapshot.State.Issues.GetValueOrDefault(diagnosticIssueNumber);
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        Mode = "isolated-status-test-failure",
                        ErrorType = exception.GetType().Name,
                        Issue = diagnosticIssueNumber,
                        Operation = diagnosticOperation,
                        StateRevision = snapshot.Revision,
                        StateVersion = snapshot.State.Version,
                        ManifestDigest = diagnosticManifestDigest,
                        HeadSha = issue?.HeadSha,
                        TaskHistory = issue?.Tasks.Values.OrderBy(task => task.Id, StringComparer.Ordinal)
                            .Select(task => new { task.Id, task.Kind, task.Status, task.ExternalId, task.SourceSha, task.FinalSha })
                            .ToArray(),
                        Ci = issue?.Ci is { } ci ? new
                        {
                            ci.IntentId, ci.RunId, ci.TaskId, ci.Round, ci.Sha, ci.Green, ci.TerminalStopReason,
                            ci.DispatchBoundary
                        } : null,
                        StatusHistory = snapshot.State.Outbox.Values.Where(effect =>
                                effect.IssueNumber == diagnosticIssueNumber && effect.Kind == EffectKind.PublishRequiredStatus)
                            .OrderBy(effect => effect.Id, StringComparer.Ordinal)
                            .Select(effect => new { effect.Id, effect.Status, effect.Sha, Payload = effect.RequiredStatus,
                                effect.ExternalId, effect.CreatedAt, effect.CompletedAt,
                                Boundary = effect.RequiredStatusBoundary }).ToArray()
                    }, StateJson.Options));
                }
                else
                {
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        Mode = "isolated-status-test-failure",
                        ErrorType = exception.GetType().Name,
                        StateUnavailable = true
                    }, StateJson.Options));
                }
            }
            catch (Exception diagnosticException) when (diagnosticException is IOException or InvalidDataException or
                JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or
                KeyNotFoundException or HttpRequestException or FormatException or OverflowException or
                OperationCanceledException)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Mode = "isolated-status-test-failure",
                    ErrorType = exception.GetType().Name,
                    StateUnavailable = true
                }, StateJson.Options));
            }
            error.WriteLine($"Isolated status test stopped ({exception.GetType().Name}); reconcile saved test state before continuing.");
            return 1;
        }
        finally
        {
            userClient?.Dispose();
            client?.Dispose();
        }
    }

    private static HttpClient CreateClient(string token)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
