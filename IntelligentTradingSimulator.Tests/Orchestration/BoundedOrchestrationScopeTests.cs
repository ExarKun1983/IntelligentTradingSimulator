using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class BoundedOrchestrationScopeTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-11-09T22:08:00Z");
    private static readonly DateTimeOffset Observed = Created.AddMinutes(2);
    private static readonly DateTimeOffset Resolved = Observed.AddMinutes(1);
    private const string TaskId = "task:227:1:1:1";
    private const string ExternalTaskId = "e4743d38-6b27-49cd-a272-83b34465a808";
    private const string Reason =
        "Retire the stopped single-pass mapping attempt before separately approved retest #237.";

    [Fact]
    public void RetestScopeAcceptsOnlyTheDurablyResolvedPriorAttempt()
    {
        var state = RetiredState();
        state.Issues[237] = new() { Number = 237, MaxRepairTasks = 1, SinglePassTest = true };

        Scope().Validate(state);

        state.AdmittedIssue = 237;
        Scope().Validate(state);
    }

    [Fact]
    public void ReconcileRetestDoesNotRepairRetiredProjectionsAndAdmitsReadyIssue()
    {
        var state = RetiredState();
        state.Issues[227].ProjectionDigest = null;
        state.Issues[227].ObservedProjectionDigest = null;
        state.Issues[237] = new()
        {
            Number = 237, Generation = 1, Ready = true, ReadyAt = Created,
            Phase = Phase.READY, MaxRepairTasks = 1, SinglePassTest = true
        };
        Scope().Validate(state);
        var observation = new LiveObservation
        {
            ObservedAt = Observed,
            Issues = new()
            {
                [227] = new() { Ready = false, Labels = ["agent:ready"] },
                [237] = new() { Ready = true, Labels = ["agent:ready"] }
            }
        };

        var result = new OrchestrationReducer(excludedIssueNumber: 227).Reconcile(state, observation);

        Assert.Equal(237, result.State.AdmittedIssue);
        Assert.Contains(result.State.Outbox.Values, effect =>
            effect.IssueNumber == 237 && effect.Kind == EffectKind.LaunchTask);
        Assert.DoesNotContain(result.State.Outbox.Values, effect =>
            effect.IssueNumber == 227 && effect.Kind is EffectKind.ProjectLabels or EffectKind.ProjectStatus);
        Assert.Null(result.State.Issues[227].ProjectionDigest);
        Assert.Null(result.State.Issues[227].ObservedProjectionDigest);
    }

    private static void RetestScopeRejectsAnUnresolvedOrAlteredPriorAttempt(Action<RepositoryState> mutate)
    {
        var state = RetiredState();
        mutate(state);

        Assert.Throws<InvalidDataException>(() => Scope().Validate(state));
    }

    public static IEnumerable<object[]> InvalidRetirementMutations()
    {
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].Tasks[TaskId].Status = TaskStatus.Running)];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].Tasks[TaskId].OperatorResolution = null)];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].Tasks[TaskId].TerminalEvidence = null)];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].Tasks[TaskId].ExternalId = "different-task")];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].ActiveAuthorized = true)];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].ReactivationRequired = false)];
        yield return [(
            Action<RepositoryState>)(state => state.AdmittedIssue = 227)];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].Ci = new())];
        yield return [(
            Action<RepositoryState>)(state => state.Outbox["second-launch"] = new()
        {
            Id = "second-launch", IssueNumber = 227, Generation = 1, Round = 1,
            Kind = EffectKind.LaunchTask, Status = DeliveryStatus.Succeeded, TaskId = TaskId,
            ExternalId = ExternalTaskId
        })];
        yield return [(
            Action<RepositoryState>)(state => state.Outbox["uncertain"] = new()
        {
            Id = "uncertain", IssueNumber = 227, Generation = 1, Kind = EffectKind.ProjectStatus,
            Status = DeliveryStatus.Unknown
        })];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[228] = new() { Number = 228 })];
        yield return [(
            Action<RepositoryState>)(state => state.Issues.Remove(227))];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].Tasks[TaskId].TerminalEvidence =
                state.Issues[227].Tasks[TaskId].TerminalEvidence! with { EventId = "not-in-inbox" })];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].Tasks[TaskId].OperatorResolution =
                state.Issues[227].Tasks[TaskId].OperatorResolution! with { Reason = "changed" })];
        yield return [(
            Action<RepositoryState>)(state => state.Issues[227].Tasks[TaskId].OperatorResolution =
                state.Issues[227].Tasks[TaskId].OperatorResolution! with { EvidenceEventId = "not-in-inbox" })];
    }

    [Theory]
    [MemberData(nameof(InvalidRetirementMutations))]
    public void RetestScopeRejectsInvalidRetirementEvidence(Action<RepositoryState> mutate) =>
        RetestScopeRejectsAnUnresolvedOrAlteredPriorAttempt(mutate);

    [Theory]
    [InlineData(228, 227)]
    [InlineData(237, 228)]
    [InlineData(227, 227)]
    public void RetestScopeRejectsAnyOtherHistoricalIssuePair(int activeIssue, int retiredIssue)
    {
        var state = RetiredState();
        Assert.Throws<InvalidDataException>(() =>
            new BoundedOrchestrationScope(activeIssue, 1, retiredIssue).Validate(state));
    }

    private static BoundedOrchestrationScope Scope() => new(237, 1, 227);

    private static RepositoryState RetiredState()
    {
        var state = RepositoryState.Create("owner/repo");
        var launch = new EffectIntent
        {
            Id = "LaunchTask:227:1:task:227:1:1:1:1",
            Digest = "launch-digest",
            IssueNumber = 227,
            Generation = 1,
            Kind = EffectKind.LaunchTask,
            Status = DeliveryStatus.Succeeded,
            TaskId = TaskId,
            Round = 1,
            ExternalId = ExternalTaskId,
            CreatedAt = Created,
            StartedAt = Created,
            CompletedAt = Created.AddSeconds(1)
        };
        state.Outbox.Add(launch.Id, launch);

        var evidence = new TaskTerminalEvidence(launch.Id, ExternalTaskId, state.Repository, 1, 55,
            "completed", 1, [new("session", ExternalTaskId, 1, "completed", Observed)],
            Observed, Observed, new string('a', 64));
        const string terminalEventId = "terminal-for:resolve-task-227";
        const string resolutionEventId = "resolve-task-227";
        var taskActor = new Actor("github-task-api", ActorKind.TaskService);
        var human = new Actor("maintainer", ActorKind.Human, 55, Type: "User");
        var terminalEvent = new OrchestrationEvent(terminalEventId, state.Repository, 227, 1, 1,
            taskActor, Observed, new TaskTerminalObserved(TaskId, evidence));
        var resolutionEvent = new OrchestrationEvent(resolutionEventId, state.Repository, 227, 1, 1,
            human, Resolved, new ResolveTaskTerminal(TaskId, terminalEventId, Reason));
        state.Inbox.Add(terminalEventId, Inbox(terminalEvent, Observed));
        state.Inbox.Add(resolutionEventId, Inbox(resolutionEvent, Resolved));
        state.Issues.Add(227, new()
        {
            Number = 227,
            Generation = 1,
            Phase = Phase.BLOCKED,
            ReactivationRequired = true,
            HoldReason = "Task PR mapping is unsupported or contradictory; admission retained.",
            Round = 1,
            MaxRepairTasks = 1,
            SinglePassTest = true,
            ActiveTaskId = TaskId,
            Tasks = new()
            {
                [TaskId] = new()
                {
                    Id = TaskId,
                    Kind = TaskKind.Coding,
                    Status = TaskStatus.Failed,
                    Round = 1,
                    LaunchSource = TaskLaunchSource.UnmappedIssue,
                    LaunchConfirmed = true,
                    ExternalId = ExternalTaskId,
                    TerminalEvidence = new(terminalEventId, evidence),
                    OperatorResolution = new(resolutionEventId, terminalEventId, human, Reason, Resolved)
                }
            }
        });
        return state;
    }

    private static InboxEntry Inbox(OrchestrationEvent message, DateTimeOffset at) =>
        new(message.Id, message.Digest, DeliveryStatus.Succeeded, at, at,
            JsonSerializer.Serialize(message));
}
