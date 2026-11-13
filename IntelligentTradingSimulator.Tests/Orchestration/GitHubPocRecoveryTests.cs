using IntelligentTradingSimulator.Orchestration;
using System.Text.Json;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubPocRecoveryTests
{
    [Fact]
    public void RecoveryAcceptsOnlyThePinnedLegacySnapshot()
    {
        var snapshot = LegacySnapshot();
        GitHubPocRecoveryCommand.ValidateLegacySnapshot(snapshot);

        Assert.Throws<InvalidDataException>(() =>
            GitHubPocRecoveryCommand.ValidateLegacySnapshot(snapshot with { Revision = new string('a', 40) }));

        var changed = StateJson.Clone(snapshot.State);
        changed.Issues[249].Tasks[GitHubPocRecoveryCommand.TaskId].ExternalId = "unexpected";
        Assert.Throws<InvalidDataException>(() =>
            GitHubPocRecoveryCommand.ValidateLegacySnapshot(snapshot with { State = changed }));
    }

    [Fact]
    public void OperatorAttestationClosesUnknownLaunchWithoutCreatingAnotherTask()
    {
        var snapshot = LegacySnapshot();
        var actor = new Actor("operator", ActorKind.Human, 123, Type: "User");
        var message = new OrchestrationEvent("actions:12345:67890", snapshot.State.Repository, 249, 1, 1,
            actor, DateTimeOffset.Parse("2026-11-13T18:00:00Z"),
            new AbandonUncertainTaskLaunch(GitHubPocRecoveryCommand.TaskId,
                GitHubPocRecoveryCommand.LaunchIntentId, "Operator verified no task was created.", true));
        var state = new OrchestrationReducer(new ExactAuthorization(message)).Apply(snapshot.State, message);
        var issue = state.Issues[249];

        Assert.Equal(Phase.BLOCKED, issue.Phase);
        Assert.True(issue.ReactivationRequired);
        Assert.False(issue.ActiveAuthorized);
        Assert.Null(state.AdmittedIssue);
        Assert.Single(issue.Tasks);
        Assert.Equal(TaskStatus.Failed, issue.Tasks[GitHubPocRecoveryCommand.TaskId].Status);
        Assert.Null(issue.Tasks[GitHubPocRecoveryCommand.TaskId].ExternalId);
        Assert.Equal(message.Id, issue.Tasks[GitHubPocRecoveryCommand.TaskId].UncertainLaunchResolution!.EventId);
        Assert.Equal(DeliveryStatus.Failed, state.Outbox[GitHubPocRecoveryCommand.LaunchIntentId].Status);
        Assert.Equal(EffectFailureCode.OperatorAbandonedUnknownTaskLaunch,
            state.Outbox[GitHubPocRecoveryCommand.LaunchIntentId].FailureCode);

        var replay = new OrchestrationReducer(new ExactAuthorization(message)).Apply(state, message);
        Assert.Equal(StateJson.Serialize(state), StateJson.Serialize(replay));
    }

    [Fact]
    public void UncertainLaunchCannotBeAbandonedWithoutExplicitAttestation()
    {
        var snapshot = LegacySnapshot();
        var message = new OrchestrationEvent("actions:12345:67891", snapshot.State.Repository, 249, 1, 1,
            new Actor("operator", ActorKind.Human, 123, Type: "User"),
            DateTimeOffset.Parse("2026-11-13T18:00:00Z"),
            new AbandonUncertainTaskLaunch(GitHubPocRecoveryCommand.TaskId,
                GitHubPocRecoveryCommand.LaunchIntentId, "No task exists.", false));

        Assert.Throws<InvalidOperationException>(() =>
            new OrchestrationReducer(new ExactAuthorization(message)).Apply(snapshot.State, message));
    }

    [Fact]
    public void RecoveryIngressRequiresExactOperatorAttestationAndTaskIdentity()
    {
        var inputs = new
        {
            issue = "249",
            operation = "poc-abandon-uncertain-launch",
            confirmation = GitHubPocRecoveryCommand.Confirmation,
            acknowledge_no_task_created = GitHubPocRecoveryCommand.Confirmation,
            task = GitHubPocRecoveryCommand.TaskId,
            launch_intent = GitHubPocRecoveryCommand.LaunchIntentId,
            repository_generation = "1",
            issue_generation = "1",
            reason = "Operator confirms no task was created."
        };
        var payload = RecoveryPayload(inputs);
        var evidence = new IngressEvidence("ExarKun1983/IntelligentTradingSimulator", 12345, 123,
            "User", "write", 54321, 249, true, false, false, []);

        var message = GitHubActionsIngress.PrepareEvent(payload, "workflow_dispatch", "67890",
            evidence, 1, 1, DateTimeOffset.Parse("2026-11-13T18:00:00Z"));
        Assert.Equal(inputs.task, Assert.IsType<AbandonUncertainTaskLaunch>(message!.Data).TaskId);
        Assert.True(Assert.IsType<AbandonUncertainTaskLaunch>(message.Data).AcknowledgeNoTaskCreated);

        var invalid = inputs with { acknowledge_no_task_created = "true" };
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.PrepareEvent(
            RecoveryPayload(invalid), "workflow_dispatch", "67890", evidence,
            1, 1, DateTimeOffset.Parse("2026-11-13T18:00:00Z")));
    }

    private static StateSnapshot LegacySnapshot()
    {
        var state = RepositoryState.Create("ExarKun1983/IntelligentTradingSimulator");
        state.Generation = 1;
        state.Version = 6;
        state.IsolatedTestManifestDigest = GitHubPocRecoveryCommand.LegacyManifestDigest;
        state.AdmittedIssue = 249;
        var issue = new IssueState
        {
            Number = 249,
            Generation = 1,
            Version = 4,
            Phase = Phase.BLOCKED,
            ReactivationRequired = true,
            HoldReason = "Effect outcome is ambiguous.",
            Round = 1,
            MaxRepairTasks = 1,
            ActiveTaskId = GitHubPocRecoveryCommand.TaskId
        };
        issue.Tasks.Add(GitHubPocRecoveryCommand.TaskId, new TaskIdentity
        {
            Id = GitHubPocRecoveryCommand.TaskId,
            Kind = TaskKind.Coding,
            Status = TaskStatus.Unknown,
            Round = 1,
            LaunchSource = TaskLaunchSource.UnmappedIssue
        });
        state.Issues.Add(249, issue);
        state.Outbox.Add(GitHubPocRecoveryCommand.LaunchIntentId, new EffectIntent
        {
            Id = GitHubPocRecoveryCommand.LaunchIntentId,
            Digest = new string('b', 64),
            IssueNumber = 249,
            Generation = 1,
            Kind = EffectKind.LaunchTask,
            Status = DeliveryStatus.Unknown,
            TaskId = GitHubPocRecoveryCommand.TaskId,
            Round = 1,
            FailureCode = EffectFailureCode.ExecutionException
        });
        return new(GitHubPocRecoveryCommand.LegacyStateRevision, state);
    }

    private static JsonElement RecoveryPayload(object inputs) =>
        JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 12345, full_name = "ExarKun1983/IntelligentTradingSimulator" },
            sender = new { id = 123, login = "operator", type = "User" },
            inputs
        });

    private sealed class ExactAuthorization(OrchestrationEvent expected) : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => message.Digest == expected.Digest;
    }
}
