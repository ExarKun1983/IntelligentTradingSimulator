using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class ManagedPushRoutingTests
{
    private static readonly string Sha = new('a', 40);
    private static readonly ManagedPushRouteRequest Request = new("owner/repo", 17, Sha, Sha);

    [Fact]
    public void OrdinaryBehaviorIsIdenticalByDefaultAndUnmanagedRequestsAlwaysRun()
    {
        Assert.True(ManagedPushRouting.Decide(State(), Request).RunFullCi);
        Assert.True(ManagedPushRouting.Decide(null, Request).RunFullCi);
        Assert.True(ManagedPushRouting.Decide(RepositoryState.Create("owner/repo"), Request, true).RunFullCi);
        Assert.True(ManagedPushRouting.Decide(State(), Request with { PullRequest = 18 }, true).RunFullCi);
    }

    [Fact]
    public void WorkflowRouterUsesTrustedReadOnlyCodeDefaultsOffAndFailsClosedWhenEnabled()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IntelligentTradingSimulator.sln")))
            directory = directory.Parent;
        var workflow = File.ReadAllText(Path.Combine(directory!.FullName, ".github/workflows/ci.yml"));
        var router = workflow[workflow.IndexOf("  managed-route:", StringComparison.Ordinal)..
            workflow.IndexOf("  build-and-test:", StringComparison.Ordinal)];
        Assert.Contains("ORCHESTRATION_MANAGED_ROUTING_APPROVED == 'true'", router);
        Assert.Contains("ORCHESTRATION_ACTIVATION_APPROVED == 'true'", router);
        Assert.Contains("ORCHESTRATION_REQUIRED_CONTEXT_VERIFIED == 'true'", router);
        Assert.Contains("ORCHESTRATION_NO_ADDED_CHARGES_VERIFIED == 'true'", router);
        Assert.Contains("github.event_name == 'pull_request'", router);
        Assert.Contains("repository: ${{ github.repository }}", router);
        Assert.Contains("ref: ${{ vars.ORCHESTRATION_DEFINITION_SHA }}", router);
        Assert.Contains("persist-credentials: false", router);
        Assert.Contains("contents: read", router);
        Assert.Contains("pull-requests: read", router);
        Assert.Contains("actions: read", router);
        Assert.Contains("--github-route --dry-run", router);
        Assert.DoesNotContain("secrets.", router);
        Assert.DoesNotContain("head.sha", router);
        Assert.DoesNotContain("pull_request.head", router);
        Assert.DoesNotContain(": write", router);
        Assert.Contains("needs.managed-route.result == 'skipped'", workflow);
        Assert.Contains("needs.managed-route.result == 'success'", workflow);
        Assert.Contains("needs.managed-route.outputs.run_full_ci == 'true'", workflow);
        Assert.DoesNotContain("needs.managed-route.result != 'success'", workflow);
        Assert.Contains("Test agent orchestrator scripts", workflow);
        Assert.Contains("Run tests", workflow);
    }

    [Theory]
    [InlineData(TaskStatus.Intended)]
    [InlineData(TaskStatus.Executing)]
    [InlineData(TaskStatus.Running)]
    public void CanonicalIntermediatePushIsSuppressedOnlyWithSeparateApproval(TaskStatus status)
    {
        var state = State();
        state.Issues[7].Tasks["task"].Status = status;
        var before = StateJson.Serialize(state);
        Assert.False(ManagedPushRouting.Decide(state, Request, true).RunFullCi);
        Assert.True(ManagedPushRouting.Decide(state, Request).RunFullCi);
        Assert.Equal(before, StateJson.Serialize(state));
        Assert.Null(state.Issues[7].Tasks["task"].FinalSha);
        Assert.Null(state.Issues[7].Ci);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("fork")]
    [InlineData("closed")]
    [InlineData("event-head")]
    [InlineData("admission")]
    [InlineData("mapping")]
    [InlineData("round")]
    [InlineData("schema")]
    [InlineData("issue-schema")]
    [InlineData("task-identity")]
    public void StaleAmbiguousOrNonIntermediateContextKeepsOrdinaryCi(string corruption)
    {
        var state = State();
        var request = Request;
        switch (corruption)
        {
            case "repository": request = request with { Repository = "other/repo" }; break;
            case "fork": request = request with { SameRepository = false }; break;
            case "closed": request = request with { IsOpen = false }; break;
            case "event-head": request = request with { EventHeadSha = new('b', 40) }; break;
            case "admission": state.AdmittedIssue = null; break;
            case "mapping": state.Issues[8] = new() { Number = 8, PullRequest = 17 }; break;
            case "round": state.Issues[7].Round++; break;
            case "schema": state.SchemaVersion++; break;
            case "issue-schema": state.Issues[7].SchemaVersion++; break;
            case "task-identity": state.Issues[7].Tasks["task"].Id = "other"; break;
        }
        Assert.True(ManagedPushRouting.Decide(state, request, true).RunFullCi);
    }

    [Theory]
    [InlineData("canonical-head-lag")]
    [InlineData("ci-pending-drift")]
    [InlineData("review-pending-drift")]
    [InlineData("fix-pending")]
    [InlineData("paused")]
    [InlineData("blocked")]
    [InlineData("completed")]
    [InlineData("unknown")]
    [InlineData("authorized")]
    [InlineData("human-review-owned")]
    [InlineData("closed-owned")]
    public void OwnedPushesNeverRunOrdinaryFullCiOrInferCompletion(string situation)
    {
        var state = State();
        var issue = state.Issues[7];
        switch (situation)
        {
            case "canonical-head-lag": issue.HeadSha = new('b', 40); break;
            case "ci-pending-drift":
                issue.Phase = Phase.CI_PENDING;
                issue.Tasks["task"].Status = TaskStatus.Completed;
                issue.Tasks["task"].LaunchConfirmed = true;
                issue.Tasks["task"].FinalSha = new('b', 40);
                issue.HeadSha = new('b', 40);
                issue.Ci = new() { TaskId = "task", Round = 1, Sha = issue.HeadSha, Green = true };
                break;
            case "review-pending-drift":
                issue.Phase = Phase.REVIEW_PENDING;
                issue.HeadSha = new('b', 40);
                issue.Ci = new() { TaskId = "task", Round = 1, Sha = issue.HeadSha, Green = true };
                break;
            case "fix-pending": issue.Phase = Phase.FIX_PENDING; break;
            case "paused": issue.Paused = true; break;
            case "blocked": issue.Phase = Phase.BLOCKED; issue.ReactivationRequired = true; break;
            case "completed": issue.Tasks["task"].Status = TaskStatus.Completed; break;
            case "unknown": issue.Tasks["task"].Status = TaskStatus.Unknown; break;
            case "authorized": issue.ActiveAuthorized = false; break;
            case "human-review-owned": issue.Phase = Phase.HUMAN_REVIEW; break;
            case "closed-owned": issue.Phase = Phase.CLOSED; break;
        }
        var before = StateJson.Serialize(state);
        Assert.False(ManagedPushRouting.Decide(state, Request, true).RunFullCi);
        Assert.True(ManagedPushRouting.Decide(state, Request).RunFullCi);
        Assert.Equal(before, StateJson.Serialize(state));
    }

    private static RepositoryState State()
    {
        var state = RepositoryState.Create("owner/repo");
        state.AdmittedIssue = 7;
        state.Issues[7] = new() { Number = 7, Phase = Phase.CODING, ActiveAuthorized = true, PullRequest = 17,
            HeadSha = Sha, Round = 1, ActiveTaskId = "task",
            Tasks = new() { ["task"] = new() { Id = "task", Round = 1, Status = TaskStatus.Running } } };
        return state;
    }
}
