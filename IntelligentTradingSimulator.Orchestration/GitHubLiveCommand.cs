using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubRecoveryApproval(bool ApiCredentialVerified = false,
    bool TrustedDefinitionVerified = false, bool NoAdditionalChargesVerified = false)
{
    public bool AllowsRecovery => ApiCredentialVerified && TrustedDefinitionVerified && NoAdditionalChargesVerified;
}

public sealed record GitHubLiveSettings(string Repository, long RepositoryId, long ActorId, string BaseBranch,
    GitHubExecutionApproval Tasks, GitHubWakeApproval Wake, GitHubWakeRoute WakeRoute,
    GitHubReviewApproval Review, GitHubCiApproval Ci, GitHubCiRoute CiRoute, GitHubRequiredStatusApproval Status,
    int? TestIssue = null, int? TestMaxRepairTasks = null, int? RetiredTestIssue = null)
{
    public GitHubRecoveryApproval Recovery { get; init; } = new();

    public void ValidatePipelinePrerequisites()
    {
        if (!Review.AllowsReviews)
            throw new InvalidOperationException("Review prerequisites are unverified before pipeline execution.");
        if (!Ci.AllowsCi)
            throw new InvalidOperationException("Managed CI prerequisites are unverified before pipeline execution.");
    }

    public static GitHubLiveSettings Read(Func<string, string?> read, bool recoveryOnly = false)
    {
        bool Approved(string name) => read(name) == "true";
        string Required(string name) => !string.IsNullOrWhiteSpace(read(name)) ? read(name)! :
            throw new InvalidDataException("Missing live configuration.");
        long Identity(string name) => long.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture,
            out var id) && id > 0 ? id : throw new InvalidDataException("Invalid live identity.");
        int? OptionalIdentity(string name) => string.IsNullOrWhiteSpace(read(name)) ? null :
            int.TryParse(read(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value : throw new InvalidDataException("Invalid bounded-test identity.");
        var testIssue = OptionalIdentity("ORCHESTRATION_TEST_ISSUE");
        var testMaxRepairTasks = OptionalIdentity("ORCHESTRATION_TEST_MAX_REPAIR_TASKS");
        var retiredTestIssue = OptionalIdentity("ORCHESTRATION_RETIRED_TEST_ISSUE");
        if ((testIssue is null) != (testMaxRepairTasks is null) || testMaxRepairTasks is not (null or 1) ||
            (retiredTestIssue is not null && (testIssue != 237 || retiredTestIssue != 227)) ||
            (testIssue == 237 && retiredTestIssue != 227))
            throw new InvalidDataException("Bounded test mode requires one Issue and a repair-task cap of exactly one.");
        var activation = Approved("ORCHESTRATION_ACTIVATION_APPROVED");
        var credential = Approved("ORCHESTRATION_USER_TOKEN_VERIFIED") &&
            Approved("ORCHESTRATION_PROTECTED_CREDENTIAL_ENVIRONMENT_VERIFIED");
        var cost = Approved("ORCHESTRATION_NO_ADDED_CHARGES_VERIFIED");
        var tasks = new GitHubExecutionApproval(activation, credential, Approved("ORCHESTRATION_TASK_ENTITLEMENT_VERIFIED"), cost);
        var wake = new GitHubWakeApproval(activation, credential, Approved("ORCHESTRATION_TRUSTED_DEFINITION_VERIFIED"), cost);
        var recovery = new GitHubRecoveryApproval(credential,
            Approved("ORCHESTRATION_TRUSTED_DEFINITION_VERIFIED"), cost);
        if (recoveryOnly ? !recovery.AllowsRecovery : !tasks.AllowsTasks || !wake.AllowsWake)
            throw new InvalidOperationException("Live execution is disabled or its prerequisites are unverified.");
        var status = new GitHubRequiredStatusApproval(activation, credential, Approved("ORCHESTRATION_TRUSTED_STATUS_PUBLISHER_VERIFIED"),
            Approved("ORCHESTRATION_REQUIRED_CONTEXT_VERIFIED"), cost);
        if (!recoveryOnly && !status.AllowsPublication && testIssue is null)
            throw new InvalidOperationException("Required-status publication prerequisites are unverified.");
        return new(Required("GITHUB_REPOSITORY"), Identity("GITHUB_REPOSITORY_ID"), Identity("ORCHESTRATION_ACTOR_ID"),
            Required("ORCHESTRATION_BASE_BRANCH"), tasks, wake,
            new(Identity("ORCHESTRATION_WORKFLOW_ID"), Required("ORCHESTRATION_DEFINITION_SHA")),
            new(activation, Approved("ORCHESTRATION_REVIEW_POLICY_VERIFIED"), cost,
                Approved("ORCHESTRATION_AUTOMATIC_REVIEWS_DISABLED")),
            new(activation, credential, Approved("ORCHESTRATION_TRUSTED_CI_VERIFIED"), cost),
            new(Identity("ORCHESTRATION_CI_WORKFLOW_ID"), Required("ORCHESTRATION_CI_REF"),
                Required("ORCHESTRATION_CI_DEFINITION_SHA")), status, testIssue, testMaxRepairTasks, retiredTestIssue)
            { Recovery = recovery };
    }
}

/// <summary>Explicitly gated production composition. Normal/offline CLI defaults remain read-only.</summary>
public static class GitHubLiveCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Contains("--github-poc", StringComparer.Ordinal))
            return await GitHubPocCommand.RunAsync(args, output, error);
        if (args.Contains("--github-isolated-status-test", StringComparer.Ordinal))
            return await GitHubIsolatedStatusTestCommand.RunAsync(args, output, error);
        try
        {
            if (!args.Contains("--execute", StringComparer.Ordinal) || args.Contains("--dry-run", StringComparer.Ordinal) ||
                args.Contains("--replay", StringComparer.Ordinal) || args.Contains("--context", StringComparer.Ordinal))
                throw new InvalidDataException("Live mode requires explicit execution and trusted Actions input.");
            var path = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
            if (string.IsNullOrWhiteSpace(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Missing or oversized Actions notification.");
            using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") ?? throw new InvalidDataException("Missing event.");
            var recoveryOnly = IsRecoveryOnlyCommand(eventName, payload.RootElement);
            var settings = GitHubLiveSettings.Read(Environment.GetEnvironmentVariable, recoveryOnly);
            if (!recoveryOnly)
            {
                try
                {
                    settings.ValidatePipelinePrerequisites();
                }
                catch (InvalidOperationException exception)
                {
                    error.WriteLine($"Live preflight stopped: {exception.Message} No API calls or state writes were made.");
                    return 1;
                }
            }
            var credential = Environment.GetEnvironmentVariable("ORCHESTRATION_USER_TOKEN");
            if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("Verified user credential unavailable.");
            var workflowCredential = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(workflowCredential) || workflowCredential == credential)
                throw new InvalidOperationException("Separate workflow credential unavailable.");
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", workflowCredential);
            using var userClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30)
            };
            userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var api = new GitHubRepositoryApi(client, settings.Repository, settings.RepositoryId);
            var taskApi = new GitHubRepositoryApi(userClient, settings.Repository, settings.RepositoryId);
            var publisher = await GitHubLiveCredentials.VerifyAsync(api, taskApi, settings.ActorId);
            var store = new GitStateStore(new GitHubGitTransport(client, settings.Repository, allowMutations: true), settings.Repository);
            var host = new GitHubOrchestrationHost(api, store, taskApi: taskApi);
            var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? throw new InvalidDataException("Missing run ID.");
            WorkerResult? result;
            if (eventName is "issues" or "workflow_dispatch")
            {
                var state = (await store.LoadAsync(default)).State;
                var issue = eventName == "issues" ? payload.RootElement.GetProperty("issue").GetProperty("number").GetInt32() :
                    int.Parse(payload.RootElement.GetProperty("inputs").GetProperty("issue").GetString()!, CultureInfo.InvariantCulture);
                ValidateTestScope(state, settings, issue);
                // The run definition is verified independently of command authorization.
                var occurredAt = await VerifyCommandDefinitionAsync(api, settings.WakeRoute, runId, eventName);
                var repositoryGeneration = state.Generation;
                var issueGeneration = state.Issues.GetValueOrDefault(issue)?.Generation ?? 1;
                if (eventName == "workflow_dispatch")
                {
                    var inputs = payload.RootElement.GetProperty("inputs");
                    repositoryGeneration = long.Parse(inputs.GetProperty("repository_generation").GetString()!, CultureInfo.InvariantCulture);
                    issueGeneration = long.Parse(inputs.GetProperty("issue_generation").GetString()!, CultureInfo.InvariantCulture);
                }
                else if (state.Inbox.GetValueOrDefault($"actions:{settings.RepositoryId}:{runId}")?.Event is { } replay)
                {
                    repositoryGeneration = replay.RepositoryGeneration;
                    issueGeneration = replay.IssueGeneration;
                    occurredAt = replay.OccurredAt;
                }
                result = await host.ReconcileAsync(payload.RootElement, eventName, runId, repositoryGeneration,
                    issueGeneration, occurredAt,
                    [publisher.ActorId], publisher.ActorId, settings.BaseBranch, settings.ActorId,
                    settings.Tasks, ciRoute: settings.CiRoute, ciApproval: settings.Ci, reviewApproval: settings.Review,
                    statusPublisher: settings.Status.AllowsPublication ? publisher : null, statusApproval: settings.Status,
                    boundedScope: CreateBoundedScope(settings), commandOnly: recoveryOnly,
                    recoveryApproval: settings.Recovery);
            }
            else
            {
                if (settings.TestIssue is not null)
                    ValidateTestScope((await store.LoadAsync(default)).State, settings, null);
                result = await host.ReconcileWakeAsync(payload.RootElement, eventName, runId, settings.WakeRoute,
                    settings.Wake, publisher.ActorId, settings.BaseBranch, settings.ActorId, settings.Tasks,
                    ciRoute: settings.CiRoute, ciApproval: settings.Ci, reviewApproval: settings.Review,
                    statusPublisher: settings.Status.AllowsPublication ? publisher : null, statusApproval: settings.Status,
                    boundedScope: CreateBoundedScope(settings));
            }
            output.WriteLine(JsonSerializer.Serialize(new { Mode = "github-live", Result = result?.Reason.ToString() }, StateJson.Options));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or
            ArgumentException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or
            OperationCanceledException or FormatException or OverflowException)
        {
            error.WriteLine($"Live orchestration stopped ({exception.GetType().Name}); reconcile persisted state before retrying.");
            return 1;
        }
    }

    internal static bool IsRecoveryOnlyCommand(string eventName, JsonElement payload) =>
        eventName == "workflow_dispatch" &&
        payload.GetProperty("inputs").GetProperty("operation").GetString() is
            "pause" or "resolve-task" or "abandon-uncertain-review";

    private static void ValidateTestScope(RepositoryState state, GitHubLiveSettings settings, int? requestedIssue)
    {
        if (settings.TestIssue is not { } testIssue) return;
        if (requestedIssue is { } requested && requested != testIssue)
            throw new InvalidDataException("Bounded test mode rejected an Issue outside its configured scope.");
        CreateBoundedScope(settings)!.Validate(state);
    }

    private static BoundedOrchestrationScope? CreateBoundedScope(GitHubLiveSettings settings) =>
        settings.TestIssue is { } testIssue
            ? new BoundedOrchestrationScope(testIssue, settings.TestMaxRepairTasks!.Value,
                settings.RetiredTestIssue) : null;

    internal static async Task<DateTimeOffset> VerifyCommandDefinitionAsync(GitHubRepositoryApi api, GitHubWakeRoute route,
        string runId, string eventName)
    {
        using var run = await api.GetAsync($"actions/runs/{runId}", default);
        if (run.RootElement.GetProperty("id").ToString() != runId ||
            run.RootElement.GetProperty("workflow_id").GetInt64() != route.WorkflowId ||
            run.RootElement.GetProperty("path").GetString() != route.WorkflowPath ||
            run.RootElement.GetProperty("event").GetString() != eventName ||
            run.RootElement.GetProperty("head_sha").GetString() != route.DefinitionSha ||
            run.RootElement.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId)
            throw new InvalidDataException("Command definition identity mismatch.");
        return run.RootElement.GetProperty("created_at").GetDateTimeOffset();
    }
}
