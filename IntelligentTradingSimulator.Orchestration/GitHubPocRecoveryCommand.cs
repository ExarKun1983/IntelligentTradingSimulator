using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public static class GitHubPocRecoveryCommand
{
    public const string Confirmation = "POC-249-ACK-NO-TASK-CREATED";
    public const string WorkflowPath = ".github/workflows/agent-poc-recovery.yml";
    public const string LegacyStateReference = "refs/heads/agent-state-poc-249-v1";
    public const string LegacyStateRevision = "bd49114dc9053c046eb9d36e14f450c4e66d9591";
    public const string LegacyManifestDigest = "F678C3149B9704B6A995D8AF96DAEB9D2D123E7CD6A8BAFEAE720A7B89F9AEAE";
    public const string TaskId = "task:249:1:1:1";
    public const string LaunchIntentId = "LaunchTask:249:1:task:249:1:1:1:1";
    private const string Repository = "ExarKun1983/IntelligentTradingSimulator";

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) =>
        RunAsync(args, output, error, Environment.GetEnvironmentVariable, CreateClient);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string, string?> read, Func<string, HttpClient> createClient)
    {
        try
        {
            if (!args.Contains("--execute", StringComparer.Ordinal) ||
                args.Any(arg => arg is "--dry-run" or "--replay" or "--context"))
                throw new InvalidDataException("Recovery requires explicit execution from trusted Actions input.");
            var path = read("GITHUB_EVENT_PATH");
            if (string.IsNullOrWhiteSpace(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Missing or oversized recovery notification.");
            using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = payload.RootElement;
            var definitionSha = RequiredSha(read("ORCHESTRATION_POC_DEFINITION_SHA"));
            var actorId = RequiredIdentity(read("ORCHESTRATION_POC_ACTOR_ID"));
            var repositoryId = RequiredIdentity(read("GITHUB_REPOSITORY_ID"));
            var runId = Required(read("GITHUB_RUN_ID"));
            if (read("GITHUB_REPOSITORY") != Repository ||
                read("GITHUB_EVENT_NAME") != "workflow_dispatch" ||
                read("GITHUB_REF") != GitHubPocSettings.DefinitionReference ||
                read("GITHUB_WORKFLOW_REF") != $"{Repository}/{WorkflowPath}@{GitHubPocSettings.DefinitionReference}" ||
                read("GITHUB_WORKFLOW_SHA") != definitionSha ||
                read("ORCHESTRATION_POC_RECOVERY_APPROVED") != "true" ||
                read("ORCHESTRATION_POC_APPROVED") != "false" ||
                read("ORCHESTRATION_POC_BOOTSTRAP_APPROVED") != "false")
                throw new InvalidDataException("Recovery invocation is not isolated from normal PoC execution.");

            var inputs = root.GetProperty("inputs");
            if (inputs.GetProperty("issue").GetString() != "249" ||
                inputs.GetProperty("operation").GetString() != "poc-abandon-uncertain-launch" ||
                inputs.GetProperty("confirmation").GetString() != Confirmation ||
                inputs.GetProperty("repository_generation").GetString() != "1" ||
                inputs.GetProperty("issue_generation").GetString() != "1" ||
                inputs.GetProperty("task").GetString() != TaskId ||
                inputs.GetProperty("launch_intent").GetString() != LaunchIntentId ||
                inputs.GetProperty("acknowledge_no_task_created").GetString() != Confirmation ||
                string.IsNullOrWhiteSpace(inputs.GetProperty("reason").GetString()) ||
                inputs.GetProperty("reason").GetString()!.Length > 2048 ||
                root.GetProperty("sender").GetProperty("id").GetInt64() != actorId ||
                root.GetProperty("sender").GetProperty("type").GetString() != "User")
                throw new InvalidDataException("Recovery input or operator identity does not match the reviewed profile.");

            var workflowToken = Required(read("GITHUB_TOKEN"));
            using var client = createClient(workflowToken);
            var api = new GitHubRepositoryApi(client, Repository, repositoryId);
            await api.VerifyRepositoryAsync(default);
            var runCreatedAt = await VerifyRunAsync(api, runId, actorId, repositoryId, definitionSha);
            await VerifyDefinitionAsync(api, definitionSha);
            var ingress = await api.ReadIngressEvidenceAsync(root, "workflow_dispatch", runId, [actorId], default,
                WorkflowPath);
            // GitHub permits workflow_dispatch only to repository writers; the pinned workflow also enforces actorId.
            ingress = ingress with { Permission = "write" };
            if (ingress.ActorId != actorId || ingress.ActorType != "User" ||
                ingress.Permission is not ("admin" or "maintain" or "write") ||
                !ingress.IssueOpen || ingress.IsPullRequest)
                throw new UnauthorizedAccessException("Recovery requires the authorized human and open fixture Issue.");

            var transport = new GitHubGitTransport(client, Repository, allowMutations: true,
                stateReference: LegacyStateReference);
            var store = new GitStateStore(transport, Repository, LegacyStateReference);
            var snapshot = await store.LoadAsync(default);
            ValidateLegacySnapshot(snapshot);
            var message = GitHubActionsIngress.PrepareEvent(root, "workflow_dispatch", runId, ingress,
                1, 1, runCreatedAt) ?? throw new InvalidDataException("Missing task-launch recovery command.");
            if (message.Data is not AbandonUncertainTaskLaunch command ||
                command.TaskId != TaskId || command.IntentId != LaunchIntentId)
                throw new InvalidDataException("Recovery event does not match the isolated task launch.");
            var reducer = new OrchestrationReducer(new RecoveryAuthorization(message));
            var nextState = reducer.Apply(snapshot.State, message);
            await VerifyDefinitionAsync(api, definitionSha);
            if (!await store.TrySaveAsync(snapshot, nextState, default))
                throw new InvalidOperationException("Legacy state changed during recovery; no retry was attempted.");
            var final = await store.LoadAsync(default);
            var resolution = final.State.Issues[249].Tasks[TaskId].UncertainLaunchResolution;
            if (final.Revision == snapshot.Revision ||
                resolution is null || resolution.EventId != message.Id ||
                !resolution.AcknowledgedNoTaskCreated ||
                final.State.Outbox[LaunchIntentId].Status != DeliveryStatus.Failed)
                throw new InvalidDataException("Audited task-launch recovery was not durably recorded.");

            output.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = "github-poc-recovery",
                Operation = "poc-abandon-uncertain-launch",
                Issue = 249,
                TaskId,
                LaunchIntentId,
                OperatorAttestationRecorded = true,
                LegacyStateReference,
                PreviousStateRevision = snapshot.Revision,
                StateRevision = final.Revision
            }, StateJson.Options));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException or
            HttpRequestException or OperationCanceledException or FormatException or OverflowException)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = "github-poc-recovery-failure",
                ErrorType = exception.GetType().Name,
                Issue = 249,
                LegacyStateReference
            }, StateJson.Options));
            error.WriteLine($"PoC recovery stopped ({exception.GetType().Name}); inspect canonical state before any further action.");
            return 1;
        }
    }

    public static void ValidateLegacySnapshot(StateSnapshot snapshot)
    {
        if (snapshot.Revision != LegacyStateRevision ||
            snapshot.State.Repository != Repository || snapshot.State.Generation != 1 ||
            snapshot.State.Version != 6 ||
            snapshot.State.IsolatedTestManifestDigest != LegacyManifestDigest ||
            snapshot.State.AdmittedIssue != 249 ||
            !snapshot.State.Issues.TryGetValue(249, out var issue) ||
            issue.Generation != 1 || issue.Version != 4 || issue.Phase != Phase.BLOCKED ||
            issue.HoldReason != "Effect outcome is ambiguous." || !issue.ReactivationRequired ||
            issue.ActiveAuthorized || issue.Paused || issue.PullRequest is not null || issue.HeadSha is not null ||
            issue.Ci is not null || issue.Review is not null || issue.ActiveTaskId != TaskId ||
            issue.Tasks.Count != 1 || !issue.Tasks.TryGetValue(TaskId, out var task) ||
            task.Kind != TaskKind.Coding || task.Status != TaskStatus.Unknown || task.LaunchConfirmed ||
            task.ExternalId is not null || task.TerminalEvidence is not null ||
            !snapshot.State.Outbox.TryGetValue(LaunchIntentId, out var launch) ||
            launch.Kind != EffectKind.LaunchTask || launch.Status != DeliveryStatus.Unknown ||
            launch.IssueNumber != 249 || launch.Generation != 1 || launch.TaskId != TaskId ||
            launch.ExternalId is not null ||
            snapshot.State.Outbox.Values.Count(effect => effect.IssueNumber == 249 &&
                effect.Kind == EffectKind.LaunchTask) != 1)
            throw new InvalidDataException("Legacy PoC state changed; recovery is permitted only for the exact reviewed snapshot.");
    }

    private static async Task<DateTimeOffset> VerifyRunAsync(GitHubRepositoryApi api, string runId,
        long actorId, long repositoryId, string definitionSha)
    {
        using var run = await api.GetAsync($"actions/runs/{runId}", default);
        var root = run.RootElement;
        if (root.GetProperty("id").ToString() != runId ||
            root.GetProperty("path").GetString() != WorkflowPath ||
            root.GetProperty("head_sha").GetString() != definitionSha ||
            root.GetProperty("event").GetString() != "workflow_dispatch" ||
            root.GetProperty("run_attempt").GetInt32() != 1 ||
            root.GetProperty("repository").GetProperty("id").GetInt64() != repositoryId ||
            root.GetProperty("actor").GetProperty("id").GetInt64() != actorId ||
            root.GetProperty("triggering_actor").GetProperty("id").GetInt64() != actorId)
            throw new InvalidDataException("Recovery run definition, actor or attempt does not match authorization.");
        return root.GetProperty("created_at").GetDateTimeOffset();
    }

    private static async Task VerifyDefinitionAsync(GitHubRepositoryApi api, string definitionSha)
    {
        using var definition = await api.GetAsync("commits/" +
            Uri.EscapeDataString(GitHubPocSettings.DefinitionReference), default);
        if (definition.RootElement.GetProperty("sha").GetString() != definitionSha)
            throw new InvalidDataException("PoC master definition moved.");
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

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidDataException("Missing recovery configuration.");

    private static string RequiredSha(string? value)
    {
        if (value is not { Length: 40 } || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid trusted definition SHA.");
        return value;
    }

    private static long RequiredIdentity(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var identity) && identity > 0
            ? identity : throw new InvalidDataException("Invalid recovery identity.");

    private sealed class RecoveryAuthorization(OrchestrationEvent expected) : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) =>
            message.Digest == expected.Digest && message.Actor == expected.Actor &&
            message.Repository == expected.Repository && message.IssueNumber == expected.IssueNumber;
    }
}
