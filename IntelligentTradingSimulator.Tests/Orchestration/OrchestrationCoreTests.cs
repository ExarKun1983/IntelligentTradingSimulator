using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class OrchestrationCoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static string Sha(int value) => value.ToString("x40");
    private static OrchestrationReducer Reducer() => new(new TrustedPolicy());
    private static RepositoryState Empty() => new() { Repository = "owner/repo" };
    private static OrchestrationEvent Event(EventData data, string id = "event", int issue = 7,
        long generation = 1, DateTimeOffset? at = null) =>
        new(id, "owner/repo", issue, 1, generation, data switch
        {
            TaskCompleted => new Actor("task-service", ActorKind.TaskService),
            CiResult => new Actor("ci-service", ActorKind.CiService),
            ReviewResult => new Actor("review-service", ActorKind.ReviewService),
            _ => new Actor("maintainer", ActorKind.Human)
        }, at ?? Now, data);

    private static LiveObservation Live(RepositoryState state, string? head = null, bool ready = true, bool closed = false) =>
        new()
        {
            ObservedAt = Now.AddHours(1),
            Issues = state.Issues.ToDictionary(p => p.Key, p => new LiveIssueObservation
            {
                Ready = ready, Closed = closed, PullRequest = 11, HeadSha = head ?? p.Value.HeadSha ?? Sha(1),
                CompletedTaskId = p.Value.ActiveTaskId, Labels = OrchestrationReducer.ProjectLabels(p.Value).ToList()
            })
        };

    private static RepositoryState Admitted()
    {
        var reducer = Reducer();
        var ready = reducer.Apply(Empty(), Event(new Ready(), "ready"));
        return reducer.Reconcile(ready, Live(ready)).State;
    }

    private static RepositoryState SucceedEffect(RepositoryState state, EffectKind kind, string externalId)
    {
        var reducer = Reducer();
        var effect = state.Outbox.Values.Single(e => e.Kind == kind && e.Status == DeliveryStatus.Pending);
        state = reducer.MarkExecuting(state, effect.Id, Now);
        return reducer.RecordOutcome(state, effect.Id, new(DeliveryStatus.Succeeded, externalId), Now);
    }

    private static RepositoryState CompleteTask(RepositoryState state, int head)
    {
        var reducer = Reducer();
        state = SucceedEffect(state, EffectKind.LaunchTask, $"remote-task-{head}");
        var taskId = state.Issues[7].ActiveTaskId!;
        state = reducer.Apply(state, Event(Completion(state.Issues[7], taskId, 11, Sha(head)), $"completed:{head}"));
        return reducer.Reconcile(state, Live(state, Sha(head))).State;
    }

    private static TaskCompleted Completion(IssueState issue, string taskId, int pullRequest, string sha) =>
        new(taskId, pullRequest, sha, new(TaskTerminalRecovery.CandidateShaEvidenceVersion,
            TaskTerminalRecovery.CandidateShaPolicy, taskId, issue.Tasks[taskId].ExternalId!,
            [$"session:{taskId}"], pullRequest, sha));

    private static RepositoryState FinishCi(RepositoryState state, bool green, string run)
    {
        var reducer = Reducer();
        state = SucceedEffect(state, EffectKind.RunCi, run);
        var ci = state.Issues[7].Ci!;
        return reducer.Apply(state, Event(new CiResult(run, ci.TaskId, ci.Round, 11, ci.Sha, green,
            green ? [] : [new Finding("ci-failure", "CI failed.", $"Failed run {run}.")]), $"ci:{run}"));
    }

    private static RepositoryState FinishReview(RepositoryState state, ReviewClassification classification,
        string reviewId, List<Finding>? findings = null, List<FindingResponse>? responses = null)
    {
        var reducer = Reducer();
        state = reducer.Reconcile(state, Live(state)).State;
        state = SucceedEffect(state, EffectKind.RequestReview, reviewId);
        var review = state.Issues[7].Review!;
        return reducer.Apply(state, Event(new ReviewResult(reviewId, review.TaskId, review.Round, 11, review.Sha,
            classification, findings ?? [], responses ?? []), $"review:{reviewId}"));
    }

    [Fact]
    public void ReducerClonesInputsAndDeduplicatesByFullEventDigest()
    {
        var input = Empty();
        var message = Event(new Ready());
        var result = Reducer().Apply(input, message);
        Assert.Empty(input.Issues);
        Assert.Equal(StateJson.Serialize(result), StateJson.Serialize(Reducer().Apply(result, message)));
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(result, message with { Data = new Withdraw() }));
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(result, message with { Actor = new("other", ActorKind.Human) }));
    }

    [Fact]
    public void AuthorizationIsDenyByDefaultAndVerifiesActorScopeAndRole()
    {
        var message = Event(new Ready());
        Assert.Throws<UnauthorizedAccessException>(() => new OrchestrationReducer().Apply(Empty(), message));
        Assert.Throws<UnauthorizedAccessException>(() => Reducer().Apply(Empty(), message with { Actor = new("stranger", ActorKind.Human) }));
        Assert.Throws<UnauthorizedAccessException>(() => Reducer().Apply(Empty(), message with { Actor = new("task-service", ActorKind.TaskService) }));
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(Empty(), message with { Repository = "attacker/repo" }));
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(Empty(), message with { RepositoryGeneration = 2 }));
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(Empty(), message with { IssueGeneration = 2 }));
    }

    [Fact]
    public void ReadinessWithdrawalDefeatsOldReplayAndLiveLabelsCannotReauthorize()
    {
        var reducer = Reducer();
        var state = reducer.Apply(Empty(), Event(new Ready(), "old", at: Now));
        state = reducer.Apply(state, Event(new Withdraw(), "withdraw", at: Now.AddMinutes(2)));
        state = reducer.Apply(state, Event(new Ready(), "stale-new-id", at: Now.AddMinutes(1)));
        state = reducer.Apply(state, Event(new Ready(), "old", at: Now));
        var decision = reducer.Reconcile(state, Live(state));
        Assert.False(decision.State.Issues[7].Ready);
        Assert.Null(decision.State.AdmittedIssue);
        Assert.DoesNotContain(decision.PendingEffects, e => e.Kind == EffectKind.LaunchTask);
    }

    [Fact]
    public void LiveWithdrawalCancelsAnUnlaunchedAdmissionAndReleasesSlot()
    {
        var state = Admitted();
        var decision = Reducer().Reconcile(state, Live(state, ready: false));
        Assert.Null(decision.State.AdmittedIssue);
        Assert.Equal(Phase.BLOCKED, decision.State.Issues[7].Phase);
        Assert.All(decision.State.Outbox.Values.Where(e => e.Kind == EffectKind.LaunchTask),
            e => Assert.Equal(DeliveryStatus.Cancelled, e.Status));
    }

    [Fact]
    public void QueueHasOneOwnerAndQueuedIssueIsNotBlocked()
    {
        var reducer = Reducer();
        var state = reducer.Apply(Admitted(), Event(new Ready(), "ready8", 8));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(7, state.AdmittedIssue);
        Assert.Equal(Phase.READY, state.Issues[8].Phase);
        Assert.DoesNotContain(state.Outbox.Values, e => e.IssueNumber == 8 && e.Kind == EffectKind.LaunchTask);
        state = reducer.Apply(state, Event(new Pause("stop"), "pause"));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(8, state.AdmittedIssue);
    }

    [Theory]
    [InlineData(DeliveryStatus.Executing)]
    [InlineData(DeliveryStatus.Unknown)]
    public void UncertainWorkRetainsRepositoryAdmission(DeliveryStatus status)
    {
        var reducer = Reducer();
        var state = Admitted();
        var task = state.Outbox.Values.Single(e => e.Kind == EffectKind.LaunchTask);
        state = reducer.MarkExecuting(state, task.Id, Now);
        if (status == DeliveryStatus.Unknown)
            state = reducer.RecordOutcome(state, task.Id, new(status), Now);
        state = reducer.Apply(state, Event(new Pause("stop"), "pause"));
        state = reducer.Apply(state, Event(new Ready(), "ready8", 8));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(7, state.AdmittedIssue);
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state, Event(new Reactivate("Operator reactivation."), "reactivate")));
    }

    [Fact]
    public void ResumeHeadChangesAndStageEditsCannotManufactureCompletion()
    {
        var reducer = Reducer();
        var state = Admitted();
        state = reducer.Apply(state, Event(new Resume(), "resume"));
        state = reducer.Apply(state, Event(new StageEdited(), "stage"));
        state = reducer.Apply(state, Event(new HeadChanged(11, Sha(2)), "push"));
        state = reducer.Reconcile(state, Live(state, Sha(2))).State;
        Assert.Equal(Phase.CODING, state.Issues[7].Phase);
        Assert.Null(state.Issues[7].Ci);
        Assert.Null(state.Issues[7].Review);
        Assert.DoesNotContain(state.Outbox.Values, e => e.Kind is EffectKind.RunCi or EffectKind.RequestReview);
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state,
            Event(new CiResult("early", state.Issues[7].ActiveTaskId!, 1, 11, Sha(2), true), "early")));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state,
            Event(new TaskCompleted(state.Issues[7].ActiveTaskId!, 11, Sha(2)), "unconfirmed")));
    }

    [Fact]
    public void CompletionMustBeConfirmedAtTheLiveFinalHead()
    {
        var reducer = Reducer();
        var state = SucceedEffect(Admitted(), EffectKind.LaunchTask, "task");
        var taskId = state.Issues[7].ActiveTaskId!;
        state = reducer.Apply(state, Event(Completion(state.Issues[7], taskId, 11, Sha(2)), "complete"));
        Assert.Null(state.Issues[7].Ci);
        var mismatch = reducer.Reconcile(state, Live(state, Sha(3))).State;
        Assert.Equal(Phase.BLOCKED, mismatch.Issues[7].Phase);
        Assert.Null(mismatch.Issues[7].Ci);
        var matched = reducer.Reconcile(state, Live(state, Sha(2))).State;
        Assert.Equal(Phase.CI_PENDING, matched.Issues[7].Phase);
        Assert.Equal(Sha(2), matched.Issues[7].Ci!.Sha);
        Assert.Equal(TaskStatus.Completed, matched.Issues[7].Tasks[taskId].Status);
        Assert.Equal(TaskTerminalRecovery.CandidateShaPolicy,
            matched.Issues[7].Tasks[taskId].CandidateShaEvidence!.Policy);
        Assert.Equal(StateJson.Serialize(matched),
            StateJson.Serialize(StateJson.Deserialize(StateJson.Serialize(matched))));

        var raced = reducer.Reconcile(matched, Live(matched, Sha(3))).State;
        var racedHead = raced.Issues[7];
        Assert.Equal(Phase.BLOCKED, racedHead.Phase);
        Assert.All(raced.Outbox.Values.Where(effect => effect.Kind == EffectKind.RunCi),
            effect => Assert.NotEqual(DeliveryStatus.Pending, effect.Status));
        Assert.Equal(Sha(2), racedHead.Tasks[taskId].CandidateShaEvidence!.ObservedHeadSha);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("external-task")]
    [InlineData("session")]
    [InlineData("pull-request")]
    [InlineData("sha")]
    [InlineData("policy")]
    public void CandidateShaEvidenceRejectsIdentityAndMappingMismatches(string mismatch)
    {
        var state = SucceedEffect(Admitted(), EffectKind.LaunchTask, "remote-task");
        var taskId = state.Issues[7].ActiveTaskId!;
        var candidate = new CandidateShaEvidence(TaskTerminalRecovery.CandidateShaEvidenceVersion,
            TaskTerminalRecovery.CandidateShaPolicy, taskId, "remote-task", ["session"], 11, Sha(2));
        candidate = mismatch switch
        {
            "task" => candidate with { TaskId = "other-task" },
            "external-task" => candidate with { ExternalTaskId = "other-remote-task" },
            "session" => candidate with { SessionIds = [] },
            "pull-request" => candidate with { PullRequest = 12 },
            "sha" => candidate with { ObservedHeadSha = Sha(3) },
            "policy" => candidate with { Policy = "unknown-policy" },
            _ => candidate
        };
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(state,
            Event(new TaskCompleted(taskId, 11, Sha(2), candidate), "candidate-mismatch")));
    }

    [Fact]
    public void CompletionEvidenceReplayIsIdempotentAndConflictingReplayIsRejected()
    {
        var state = SucceedEffect(Admitted(), EffectKind.LaunchTask, "remote-task");
        var taskId = state.Issues[7].ActiveTaskId!;
        var completed = Completion(state.Issues[7], taskId, 11, Sha(2));
        var message = Event(completed, "completed");
        var reducer = Reducer();
        var recorded = reducer.Apply(state, message);
        Assert.Equal(StateJson.Serialize(recorded), StateJson.Serialize(reducer.Apply(recorded, message)));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(recorded,
            message with { Data = completed with { Sha = Sha(3) } }));
    }

    [Fact]
    public void WrongPrAndMalformedShaAreRejected()
    {
        var state = SucceedEffect(Admitted(), EffectKind.LaunchTask, "task");
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(state,
            Event(new TaskCompleted(state.Issues[7].ActiveTaskId!, 12, Sha(2)))));
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(state,
            Event(new TaskCompleted(state.Issues[7].ActiveTaskId!, 11, "not-a-sha"))));
    }

    [Fact]
    public void CiRunTaskRoundAndShaMustExactlyMatchCompletedTask()
    {
        var state = SucceedEffect(CompleteTask(Admitted(), 2), EffectKind.RunCi, "run");
        var ci = state.Issues[7].Ci!;
        var valid = new CiResult("run", ci.TaskId, ci.Round, 11, ci.Sha, true);
        foreach (var invalid in new[]
        {
            valid with { RunId = "other" }, valid with { TaskId = "other" },
            valid with { Round = 99 }, valid with { Sha = Sha(3) }, valid with { PullRequest = 12 }
        })
            Assert.Throws<InvalidOperationException>(() => Reducer().Apply(state, Event(invalid)));
        Assert.Equal(Phase.REVIEW_PENDING, Reducer().Apply(state, Event(valid)).Issues[7].Phase);
    }

    [Fact]
    public void TwoCiRepairsAreChildrenInSameRoundAndOnlyEligibleGreenResetsBudget()
    {
        var reducer = Reducer();
        var state = CompleteTask(Admitted(), 2);
        var original = state.Issues[7].ActiveTaskId;
        for (var failure = 1; failure <= 2; failure++)
        {
            state = FinishCi(state, false, $"run{failure}");
            Assert.Equal(failure, state.Issues[7].ConsecutiveCiFailures);
            state = reducer.Apply(state, Event(new Resume(), $"resume{failure}"));
            Assert.Equal(failure, state.Issues[7].ConsecutiveCiFailures);
            state = reducer.Reconcile(state, Live(state)).State;
            var child = state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!];
            Assert.Equal(TaskKind.CiRepair, child.Kind);
            Assert.Equal(1, child.Round);
            Assert.NotNull(child.ParentTaskId);
            state = CompleteTask(state, failure + 2);
        }
        state = FinishCi(state, true, "green");
        Assert.Equal(0, state.Issues[7].ConsecutiveCiFailures);
        Assert.Equal(3, state.Issues[7].Tasks.Count);
        Assert.Equal(original, state.Issues[7].Tasks.Values.Single(t => t.Kind == TaskKind.Coding).Id);
    }

    [Fact]
    public void CiBudgetExhaustionSurvivesResumeStageChangesAndReady()
    {
        var reducer = Reducer();
        var state = CompleteTask(Admitted(), 2);
        state.Issues[7].CiBudget = 1;
        state = FinishCi(state, false, "bad");
        state = reducer.Apply(state, Event(new Resume(), "resume"));
        state = reducer.Apply(state, Event(new StageEdited(), "stage"));
        state = reducer.Apply(state, Event(new Ready(), "again", at: Now.AddMinutes(1)));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal(1, state.Issues[7].ConsecutiveCiFailures);
        Assert.True(state.Issues[7].ReactivationRequired);
    }

    [Fact]
    public void SinglePassTestStopsAfterFailedCiAndCannotBeReactivated()
    {
        var reducer = Reducer();
        var state = Admitted();
        state.Issues[7].SinglePassTest = true;
        state = CompleteTask(state, 2);
        state = FinishCi(state, false, "failed-single-ci");
        state = StateJson.Deserialize(StateJson.Serialize(state));
        state = reducer.Apply(state, Event(new Resume(), "resume-single"));
        state = reducer.Apply(state, Event(new Ready(), "ready-single"));
        state = reducer.Reconcile(state, Live(state)).State;

        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal("Single-pass test stopped after CI failure.", state.Issues[7].HoldReason);
        Assert.Null(state.AdmittedIssue);
        Assert.Single(state.Issues[7].Tasks);
        Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.RunCi);
        Assert.DoesNotContain(state.Outbox.Values, effect => effect.Kind == EffectKind.RequestReview);
        Assert.Throws<InvalidOperationException>(() =>
            reducer.Apply(state, Event(new Reactivate("try again"), "reactivate-single")));
    }

    [Fact]
    public void SinglePassTestAllowsOneReviewAfterGreenCiButNeverRepairsReviewFindings()
    {
        var reducer = Reducer();
        var state = Admitted();
        state.Issues[7].SinglePassTest = true;
        state = CompleteTask(state, 2);
        state = FinishCi(state, true, "single-green");
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.RequestReview);
        state = FinishReview(state, ReviewClassification.Blocking, "single-review",
            [new("f", "Fix this", "evidence")], []);
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Single(state.Issues[7].Tasks);
        Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.RunCi);
        Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.RequestReview);
    }

    [Fact]
    public void SinglePassTestRejectsSecondCiAndReviewPlanning()
    {
        var reducer = Reducer();
        var state = Admitted();
        state.Issues[7].SinglePassTest = true;
        state = CompleteTask(state, 2);
        state = FinishCi(state, true, "first-ci");
        state = reducer.Reconcile(state, Live(state)).State;
        state = SucceedEffect(state, EffectKind.RequestReview, "first-review");
        var original = StateJson.Serialize(state);

        state.Issues[7].Review = null;
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal("Single-pass test review limit reached.", state.Issues[7].HoldReason);
        Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.RequestReview);

        state = StateJson.Deserialize(original);
        state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!].Status =
            IntelligentTradingSimulator.Orchestration.TaskStatus.AwaitingHeadConfirmation;
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal("Single-pass test CI limit reached.", state.Issues[7].HoldReason);
        Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.RunCi);
    }

    [Fact]
    public void BoundedRepairTaskCapAllowsOneRepairAndBlocksTheNext()
    {
        var reducer = Reducer();
        var state = Admitted();
        state.Issues[7].MaxRepairTasks = 1;
        state = CompleteTask(state, 2);

        state = FinishCi(state, false, "first-failure");
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Single(state.Issues[7].Tasks.Values, task => task.Kind == TaskKind.CiRepair);
        state = CompleteTask(state, 3);

        state = FinishCi(state, false, "second-failure");
        state = reducer.Reconcile(state, Live(state)).State;

        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal("Repair task budget exhausted.", state.Issues[7].HoldReason);
        Assert.True(state.Issues[7].ReactivationRequired);
        Assert.Null(state.AdmittedIssue);
        Assert.Single(state.Issues[7].Tasks.Values, task => task.Kind == TaskKind.CiRepair);
        Assert.DoesNotContain(state.Outbox.Values, intent => intent.Kind == EffectKind.LaunchTask &&
            intent.Status == DeliveryStatus.Pending);
    }

    [Fact]
    public void ReadyCanSetButNeverRaiseBoundedRepairTaskCap()
    {
        var reducer = Reducer();
        var state = reducer.Apply(Empty(), Event(new Ready(1), "bounded-ready"));
        Assert.Equal(1, state.Issues[7].MaxRepairTasks);
        Assert.Throws<InvalidOperationException>(() =>
            reducer.Apply(state, Event(new Ready(2), "attempt-raise")));
        Assert.Throws<InvalidOperationException>(() =>
            reducer.Apply(Empty(), Event(new Ready(0), "invalid-cap")));
    }

    [Fact]
    public void ConfirmedRepairTaskMayPushButPushStillCannotEnterCi()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), false, "bad");
        state = reducer.Reconcile(state, Live(state)).State;
        state = SucceedEffect(state, EffectKind.LaunchTask, "repair");
        state = reducer.Apply(state, Event(new HeadChanged(11, Sha(3)), "repair-push"));
        state = reducer.Reconcile(state, Live(state, Sha(3))).State;
        Assert.Equal(Phase.FIX_PENDING, state.Issues[7].Phase);
        Assert.False(state.Issues[7].ReactivationRequired);
        Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.RunCi);
        state = reducer.Apply(state, Event(Completion(state.Issues[7], state.Issues[7].ActiveTaskId!, 11, Sha(3)), "repair-done"));
        state = reducer.Reconcile(state, Live(state, Sha(3))).State;
        Assert.Equal(Phase.CI_PENDING, state.Issues[7].Phase);
        Assert.Equal(2, state.Outbox.Values.Count(effect => effect.Kind == EffectKind.RunCi));
    }

    [Fact]
    public void CiRepairInsideReviewRoundKeepsBothCiAndReviewBlockers()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "initial");
        state = FinishReview(state, ReviewClassification.Blocking, "review",
            [new Finding("review-finding", "Defect", "Regression failure")]);
        state = reducer.Reconcile(state, Live(state)).State;
        var reviewRepair = state.Issues[7].ActiveTaskId;
        state = FinishCi(CompleteTask(state, 3), false, "repair-ci-failed");
        state = reducer.Reconcile(state, Live(state)).State;
        var ciRepair = state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!];
        Assert.Equal(TaskKind.CiRepair, ciRepair.Kind);
        Assert.Equal(reviewRepair, ciRepair.ParentTaskId);
        Assert.Equal(1, ciRepair.Round);
        Assert.Equal("review-finding", Assert.Single(ciRepair.ReviewBlockers).Id);
        Assert.Equal("ci-failure", Assert.Single(ciRepair.CiBlockers).Id);
        state = FinishCi(CompleteTask(state, 4), true, "repaired-ci");
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal("review-finding", Assert.Single(state.Issues[7].Review!.Findings).Id);
    }

    [Fact]
    public void FourReviewRepairCyclesPreserveBlockersAndDoNotTreatShaAsProgress()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "initial");
        for (var cycle = 1; cycle <= 4; cycle++)
        {
            state = FinishReview(state, ReviewClassification.Blocking, $"review{cycle}",
                [new Finding("finding", "Repair the defect.", "test still fails")]);
            Assert.Equal(cycle, state.Issues[7].ConsecutiveReviewFailures);
            state = reducer.Reconcile(state, Live(state)).State;
            Assert.Equal(TaskKind.ReviewRepair, state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!].Kind);
            Assert.Single(state.Issues[7].Review!.Findings);
            state = CompleteTask(state, cycle + 2);
            state = FinishCi(state, true, $"green{cycle}");
            Assert.Equal(cycle, state.Issues[7].ConsecutiveReviewFailures);
        }
        state = FinishReview(state, ReviewClassification.Accepted, "final",
            responses: [new FindingResponse("finding", "Regression test now passes.", true)]);
        Assert.Equal(Phase.HUMAN_REVIEW, state.Issues[7].Phase);
        Assert.Null(state.AdmittedIssue);
    }

    [Fact]
    public void FindingEvidenceCanResetReviewBudgetOnlyAfterEligibleGreen()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "initial");
        state = FinishReview(state, ReviewClassification.Blocking, "one",
            [new Finding("a", "Defect A", "failed A"), new Finding("b", "Defect B", "failed B")]);
        state = reducer.Reconcile(state, Live(state)).State;
        state = FinishCi(CompleteTask(state, 3), true, "second");
        state = FinishReview(state, ReviewClassification.Blocking, "two",
            [new Finding("b", "Defect B", "failed B")], [new FindingResponse("a", "test A now passes", true)]);
        Assert.Equal(1, state.Issues[7].ConsecutiveReviewFailures);
        state = reducer.Reconcile(state, Live(state)).State;
        state = CompleteTask(state, 4);
        Assert.Equal(1, state.Issues[7].ConsecutiveReviewFailures);
        state = FinishCi(state, true, "third");
        Assert.Equal(0, state.Issues[7].ConsecutiveReviewFailures);
        Assert.False(state.Issues[7].PendingReviewProgress);
    }

    [Fact]
    public void ReintroducedBlockerCannotUseHistoricalClearanceForAcceptance()
    {
        var state = ReintroducedBlocker();
        var oldEvidence = new FindingResponse("a", "test A now passes", true);
        Assert.Contains(oldEvidence, state.Issues[7].Review!.Responses);
        Assert.Throws<InvalidOperationException>(() =>
            FinishReview(state, ReviewClassification.Accepted, "missing-new-clearance"));
        Assert.Throws<InvalidOperationException>(() =>
            FinishReview(state, ReviewClassification.Accepted, "replayed-clearance", responses: [oldEvidence]));

        state = FinishReview(state, ReviewClassification.Accepted, "fresh-clearance",
            responses: [new("a", "Reintroduced A regression passes at the repaired head.", true)]);
        Assert.Equal(Phase.HUMAN_REVIEW, state.Issues[7].Phase);
        Assert.True(state.Issues[7].PendingReviewProgress);
        Assert.Empty(state.Issues[7].Review!.Findings);
        Assert.Equal(3, state.Issues[7].Review!.Responses.Count);
        Assert.Contains(oldEvidence, state.Issues[7].Review!.Responses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeedsHumanAndClassificationRetainReintroducedBlockerUntilFreshEvidence(bool replayOldEvidence)
    {
        var oldEvidence = new FindingResponse("a", "test A now passes", true);
        var state = FinishReview(ReintroducedBlocker(), ReviewClassification.NeedsHuman, "ambiguous-reopened",
            responses: replayOldEvidence ? [oldEvidence] : []);
        Assert.Equal("a", Assert.Single(state.Issues[7].Review!.Findings).Id);
        Assert.False(state.Issues[7].PendingReviewProgress);
        var failures = state.Issues[7].ConsecutiveReviewFailures;
        var issue = state.Issues[7];
        var classification = new HumanClassification(issue.CurrentHandoffId!, issue.Round, 11, issue.HeadSha!,
            ReviewClassification.Accepted, "Classify the current blocker.");
        Assert.Throws<InvalidOperationException>(() =>
            Reducer().Apply(state, Event(classification, "missing")));
        Assert.Throws<InvalidOperationException>(() =>
            Reducer().Apply(state, Event(classification with { Responses = [oldEvidence] }, "replayed")));

        state = Reducer().Apply(state, Event(classification with
        {
            Classification = ReviewClassification.NeedsHuman, Responses = [oldEvidence]
        }, "still-ambiguous"));
        Assert.Equal("a", Assert.Single(state.Issues[7].Review!.Findings).Id);
        Assert.False(state.Issues[7].PendingReviewProgress);
        Assert.Equal(failures, state.Issues[7].ConsecutiveReviewFailures);

        state = Reducer().Apply(state, Event(classification with
        {
            HandoffId = state.Issues[7].CurrentHandoffId!,
            Responses = [new("a", "Current-head regression confirms reopened A is repaired.", true)]
        }, "fresh-human-clearance"));
        Assert.Equal(Phase.HUMAN_REVIEW, state.Issues[7].Phase);
        Assert.True(state.Issues[7].PendingReviewProgress);
        Assert.Empty(state.Issues[7].Review!.Findings);
        Assert.Contains(oldEvidence, state.Issues[7].Review!.Responses);
    }

    private static RepositoryState ReintroducedBlocker()
    {
        var reducer = Reducer();
        var a = new Finding("a", "Defect A", "Regression A fails.");
        var b = new Finding("b", "Defect B", "Regression B fails.");
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "initial-ab");
        state = FinishReview(state, ReviewClassification.Blocking, "ab", [a, b]);
        state = reducer.Reconcile(state, Live(state)).State;
        state = FinishCi(CompleteTask(state, 3), true, "a-repaired");
        state = FinishReview(state, ReviewClassification.Blocking, "only-b", [b],
            [new("a", "test A now passes", true)]);
        state = reducer.Reconcile(state, Live(state)).State;
        state = FinishCi(CompleteTask(state, 4), true, "b-repaired");
        state = FinishReview(state, ReviewClassification.Blocking, "a-reopened", [a],
            [new("b", "test B now passes", true)]);
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal("a", Assert.Single(state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!].ReviewBlockers).Id);
        return FinishCi(CompleteTask(state, 5), true, "reopened-a-repaired");
    }

    [Fact]
    public void UnstructuredReviewNeedsHumanAndOnlySeparateClassificationCanResumeIt()
    {
        var reducer = Reducer();
        Assert.Equal(ReviewClassification.NeedsHuman, UpstreamIdentity.ClassifyUnstructuredReview("APPROVED! no bugs"));
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "green");
        state = FinishReview(state, ReviewClassification.NeedsHuman, "ambiguous");
        Assert.Null(state.AdmittedIssue);
        state = reducer.Apply(state, Event(new Resume(), "resume"));
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        var handoff = state.Issues[7].CurrentHandoffId!;
        state = reducer.Apply(state, Event(new HumanFeedback("Please investigate."), "feedback"));
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal(handoff, state.Issues[7].Feedback["feedback"].HandoffId);
        state = reducer.Apply(state, Event(new HumanClassification(handoff, 1, 11, Sha(2),
            ReviewClassification.Blocking, "Maintainer explicitly classified the review.",
            [new Finding("a", "A defect", "test A")]), "classify"));
        Assert.Equal("classify", state.Issues[7].Handoffs[handoff].ClassificationEventId);
        Assert.Equal("Maintainer explicitly classified the review.", state.Issues[7].Classifications["classify"].Reason);
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(7, state.AdmittedIssue);
        Assert.Equal(TaskKind.ReviewRepair, state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!].Kind);
        var paused = reducer.Apply(state, Event(new Pause("security hold"), "pause"));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(paused,
            Event(new HumanClassification(handoff, 1, 11, Sha(2),
                ReviewClassification.Accepted, "Cannot override pause."), "invalid-classify")));
    }

    [Fact]
    public void BlockingRequiresNonemptyTypedFindingsAndAcceptedCannotHideFindings()
    {
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "green");
        Assert.Throws<InvalidOperationException>(() => FinishReview(state, ReviewClassification.Blocking, "empty"));
        Assert.Throws<InvalidOperationException>(() => FinishReview(state, ReviewClassification.Accepted, "hidden",
            [new Finding("a", "defect", "evidence")]));
    }

    [Fact]
    public void FifthReviewWithoutFindingProgressExhaustsBudgetDespiteChangingHeads()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "initial");
        for (var cycle = 1; cycle <= 5; cycle++)
        {
            state = FinishReview(state, ReviewClassification.Blocking, $"review{cycle}",
                [new Finding("a", "Defect", "Regression still fails.")]);
            if (cycle == 5) break;
            state = reducer.Reconcile(state, Live(state)).State;
            state = FinishCi(CompleteTask(state, cycle + 2), true, $"green{cycle}");
        }
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal(5, state.Issues[7].ConsecutiveReviewFailures);
        Assert.Null(state.AdmittedIssue);
        state = reducer.Apply(state, Event(new Resume(), "resume"));
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
    }

    [Fact]
    public void BlockerCannotDisappearWithoutSpecificEvidenceOrWithShaOnlyEvidence()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "initial");
        state = FinishReview(state, ReviewClassification.Blocking, "one",
            [new Finding("a", "Defect", "Failure evidence")]);
        state = reducer.Reconcile(state, Live(state)).State;
        state = FinishCi(CompleteTask(state, 3), true, "repair");
        Assert.Throws<InvalidOperationException>(() => FinishReview(state, ReviewClassification.Accepted, "missing"));
        Assert.Throws<InvalidOperationException>(() => FinishReview(state, ReviewClassification.Accepted, "sha",
            responses: [new FindingResponse("a", Sha(3), true)]));
        Assert.Throws<InvalidOperationException>(() => FinishReview(state, ReviewClassification.Blocking, "contradictory",
            [new Finding("a", "Defect", "Failure evidence")], [new FindingResponse("a", "Supposedly fixed", true)]));
    }

    [Fact]
    public void NeedsHumanCannotEraseInheritedBlockerOrAuthorizeAcceptanceWithoutResolutionEvidence()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "initial-green");
        state = FinishReview(state, ReviewClassification.Blocking, "blocking",
            [new Finding("F", "Inherited defect", "Regression F fails.")]);
        state = reducer.Reconcile(state, Live(state)).State;
        state = FinishCi(CompleteTask(state, 3), true, "repair-green");
        state = FinishReview(state, ReviewClassification.NeedsHuman, "ambiguous");
        state = StateJson.Deserialize(StateJson.Serialize(state));
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal("F", Assert.Single(state.Issues[7].Review!.Findings).Id);
        var newGeneration = reducer.Apply(state, Event(new Reactivate("Start a new generation without discarding unresolved findings."), "new-generation"));
        newGeneration = reducer.Reconcile(newGeneration, Live(newGeneration)).State;
        var newTask = newGeneration.Issues[7].Tasks[newGeneration.Issues[7].ActiveTaskId!];
        Assert.Equal("F", Assert.Single(newTask.ReviewBlockers).Id);
        var handoff = state.Issues[7].CurrentHandoffId!;
        var accepted = new HumanClassification(handoff, 1, 11, Sha(3), ReviewClassification.Accepted,
            "Human decision must supply finding-specific evidence.");
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state, Event(accepted, "invalid-accept")));
        state = reducer.Apply(state, Event(new HumanClassification(handoff, 1, 11, Sha(3),
            ReviewClassification.NeedsHuman, "The review remains unclassified."), "still-unknown"));
        Assert.Equal("F", Assert.Single(state.Issues[7].Review!.Findings).Id);
        var nextHandoff = state.Issues[7].CurrentHandoffId!;
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state,
            Event(accepted with { HandoffId = nextHandoff }, "invalid-accept-again")));
        state = reducer.Apply(state, Event(accepted with
        {
            HandoffId = nextHandoff,
            Reason = "Verified the specific regression was repaired.",
            Responses = [new FindingResponse("F", "Regression F now passes with the repaired implementation.", true)]
        }, "evidenced-accept"));
        Assert.Equal(Phase.HUMAN_REVIEW, state.Issues[7].Phase);
        Assert.Empty(state.Issues[7].Review!.Findings);
        Assert.Equal("F", Assert.Single(state.Issues[7].Review!.Responses).FindingId);
    }

    [Fact]
    public void SixProductiveReviewRoundsAreNotCappedByNoProgressBudget()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "initial");
        state = FinishReview(state, ReviewClassification.Blocking, "initial",
            [new Finding("f0", "Initial defect", "Initial regression fails.")]);
        state.Issues[7].ReviewBudget = 1;
        for (var round = 1; round <= 6; round++)
        {
            state = reducer.Reconcile(state, Live(state)).State;
            state = FinishCi(CompleteTask(state, round + 2), true, $"green:{round}");
            state = FinishReview(state, ReviewClassification.Blocking, $"productive:{round}",
                [new Finding($"f{round}", "Additional specific defect", $"Regression {round} fails.")],
                [new FindingResponse($"f{round - 1}", $"Regression {round - 1} now passes.", true)]);
            Assert.Equal(Phase.FIX_PENDING, state.Issues[7].Phase);
            Assert.False(state.Issues[7].ReactivationRequired);
            if (round == 1) Assert.Equal(1, state.Issues[7].ConsecutiveReviewFailures);
        }
        Assert.Equal(0, state.Issues[7].ConsecutiveReviewFailures);
        Assert.Equal(6, state.Issues[7].Review!.Responses.Count);
    }

    [Fact]
    public void HumanClassificationRequiresExactHandoffRoundShaReasonAndHumanActor()
    {
        var reducer = Reducer();
        var state = FinishReview(FinishCi(CompleteTask(Admitted(), 2), true, "green"),
            ReviewClassification.NeedsHuman, "ambiguous");
        var command = new HumanClassification(state.Issues[7].CurrentHandoffId!, 1, 11, Sha(2),
            ReviewClassification.Blocking, "Explicit human adjudication.", [new Finding("a", "Defect", "Evidence")]);
        foreach (var invalid in new[]
        {
            command with { HandoffId = "other" }, command with { Round = 2 },
            command with { Sha = Sha(3) }, command with { PullRequest = 12 }, command with { Reason = "" }
        })
            Assert.Throws<InvalidOperationException>(() => reducer.Apply(state, Event(invalid)));
        Assert.Throws<UnauthorizedAccessException>(() => reducer.Apply(state, Event(command) with
        {
            Actor = new Actor("task-service", ActorKind.TaskService, 42)
        }));
        state = reducer.Apply(state, Event(new HumanFeedback("Accepted; please resume."), "prose"));
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal(ReviewClassification.NeedsHuman, state.Issues[7].Review!.Classification);
        Assert.Empty(state.Issues[7].Classifications);
    }

    [Fact]
    public void EveryFeedbackPayloadRetainsAuthorAndHandoffRelationshipAcrossReplay()
    {
        var reducer = Reducer();
        var state = FinishReview(FinishCi(CompleteTask(Admitted(), 2), true, "green"),
            ReviewClassification.NeedsHuman, "ambiguous");
        var handoff = state.Issues[7].CurrentHandoffId!;
        var first = Event(new HumanFeedback("Investigate finding A.", handoff, 1, Sha(2)), "feedback:1") with
        {
            Actor = new Actor("maintainer", ActorKind.Human, 42)
        };
        state = reducer.Apply(state, first);
        state = reducer.Apply(state, first with { Id = "feedback:2" });
        state = reducer.Apply(state, Event(new HumanFeedback("Also investigate finding B."), "feedback:3"));
        state = reducer.Apply(state, first);
        state = StateJson.Deserialize(StateJson.Serialize(state));
        Assert.Equal(3, state.Issues[7].Feedback.Count);
        Assert.Equal(42, state.Issues[7].Feedback["feedback:1"].Author.Id);
        Assert.Equal("Investigate finding A.", state.Issues[7].Feedback["feedback:1"].Text);
        Assert.Equal("Investigate finding A.", state.Issues[7].Feedback["feedback:2"].Text);
        Assert.Equal("Also investigate finding B.", state.Issues[7].Feedback["feedback:3"].Text);
        Assert.All(state.Issues[7].Feedback.Values, feedback => Assert.Equal(handoff, feedback.HandoffId));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state,
            Event(new HumanFeedback("Wrong SHA.", handoff, 1, Sha(3)), "wrong-context")));
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
    }

    [Theory]
    [InlineData(true, TerminalOutcome.Merged)]
    [InlineData(false, TerminalOutcome.Unmerged)]
    [InlineData(null, TerminalOutcome.Unknown)]
    public void TerminalOutcomeUsesLiveMergeEvidenceAndRetainsHistory(bool? merged, TerminalOutcome expected)
    {
        var reducer = Reducer();
        var state = Admitted();
        var live = Live(state, closed: true);
        live.Issues[7].PullRequestMerged = merged;
        state = reducer.Reconcile(state, live).State;
        Assert.Equal(Phase.CLOSED, state.Issues[7].Phase);
        Assert.Equal(expected, state.Issues[7].TerminalOutcome);
        Assert.Equal(expected, Assert.Single(state.Issues[7].TerminalHistory).Outcome);
        state = reducer.Reconcile(state, live).State;
        Assert.Single(state.Issues[7].TerminalHistory);
        state = reducer.Apply(state, Event(new Reopen(), "reopened"));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        state = reducer.Apply(state, Event(new Reactivate("Explicit operator reactivation."), "reactivate"));
        Assert.Equal(TerminalOutcome.None, state.Issues[7].TerminalOutcome);
        Assert.Single(state.Issues[7].TerminalHistory);
    }

    [Fact]
    public void IdenticalFeedbackTextHasDistinctIdentityAndCannotReopenClosedIssue()
    {
        var reducer = Reducer();
        var state = reducer.Apply(Empty(), Event(new Ready(), "ready"));
        state = reducer.Apply(state, Event(new HumanFeedback("Please investigate."), "comment1"));
        state = reducer.Apply(state, Event(new HumanFeedback("Please investigate."), "comment2"));
        Assert.Equal(2, state.Issues[7].FeedbackEventIds.Count);
        Assert.Equal(2, state.Issues[7].Feedback.Count);
        Assert.Equal("Please investigate.", state.Issues[7].Feedback["comment1"].Text);
        Assert.Equal("Please investigate.", state.Issues[7].Feedback["comment2"].Text);
        Assert.Equal("comment1", state.Issues[7].Feedback["comment1"].EventId);
        Assert.Equal("comment2", state.Issues[7].Feedback["comment2"].EventId);
        state = reducer.Reconcile(state, Live(state, closed: true)).State;
        state = reducer.Apply(state, Event(new Reopen(), "reopened"));
        state = reducer.Apply(state, Event(new Resume(), "resume"));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        state = reducer.Apply(state, Event(new Reactivate("Explicit operator reactivation."), "reactivate"));
        Assert.Equal(2, state.Issues[7].Generation);
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state, Event(new Resume(), "stale")));
    }

    [Fact]
    public void DelayedClosureDoesNotOverrideLiveReopen()
    {
        var reducer = Reducer();
        var state = reducer.Apply(Empty(), Event(new Ready(), "ready"));
        state = reducer.Apply(state, Event(new Closure(), "closed"));
        state = reducer.Apply(state, Event(new Reopen(), "reopened"));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.False(state.Issues[7].ClosurePending);
    }

    [Fact]
    public void ClosureNotificationFollowedByLiveOpenCannotReuseOldReadiness()
    {
        var reducer = Reducer();
        var state = reducer.Apply(Empty(), Event(new Ready(), "ready"));
        state = reducer.Apply(state, Event(new Closure(), "delayed-close"));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.False(state.Issues[7].Ready);
        Assert.Null(state.AdmittedIssue);
        Assert.DoesNotContain(state.Outbox.Values, effect => effect.Kind == EffectKind.LaunchTask);
    }

    [Fact]
    public void ProjectionLoopsDoNotDuplicateEffectsOrAdvanceHeldState()
    {
        var reducer = Reducer();
        var state = reducer.Apply(Admitted(), Event(new Pause("hold"), "pause"));
        var live = Live(state);
        live.Issues[7].Labels = ["agent:ci", "agent:ready", "unrelated"];
        state = reducer.Reconcile(state, live).State;
        var count = state.Outbox.Count;
        var repeated = reducer.Reconcile(state, live).State;
        Assert.Equal(count, repeated.Outbox.Count);
        Assert.Equal(StateJson.Serialize(state), StateJson.Serialize(repeated));
        state = reducer.Apply(state, Event(new StageEdited(), "tampered"));
        state = reducer.Reconcile(state, live).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Contains("agent:blocked", OrchestrationReducer.ProjectLabels(state.Issues[7]));
        Assert.DoesNotContain(state.Outbox.Values, e => e.Kind is EffectKind.RunCi or EffectKind.RequestReview);
    }

    [Fact]
    public void ActualApiShapesUseRestUserOrGraphQlAuthorNotProse()
    {
        using var rest = JsonDocument.Parse("""{"user":{"id":42,"login":"trusted","type":"User"},"body":"author: attacker"}""");
        using var graph = JsonDocument.Parse("""{"author":{"databaseId":42,"id":"opaque-node-id","login":"trusted","__typename":"User"},"body":"user: attacker"}""");
        Assert.Equal("trusted", UpstreamIdentity.RestUser(rest.RootElement, ActorKind.Human).Login);
        Assert.Equal("trusted", UpstreamIdentity.GraphQlAuthor(graph.RootElement, ActorKind.Human).Login);
        Assert.Equal(42, UpstreamIdentity.RestUser(rest.RootElement, ActorKind.Human).Id);
        Assert.Equal(42, UpstreamIdentity.GraphQlAuthor(graph.RootElement, ActorKind.Human).Id);
        Assert.Equal("opaque-node-id", UpstreamIdentity.GraphQlAuthor(graph.RootElement, ActorKind.Human).NodeId);
        Assert.Equal("User", UpstreamIdentity.GraphQlAuthor(graph.RootElement, ActorKind.Human).Type);
        Assert.Throws<InvalidDataException>(() => UpstreamIdentity.RestUser(graph.RootElement, ActorKind.Human));
        Assert.Throws<InvalidDataException>(() => UpstreamIdentity.GraphQlAuthor(rest.RootElement, ActorKind.Human));
        using var textIdentity = JsonDocument.Parse("""{"user":{"id":"42","login":"trusted","type":"User"}}""");
        Assert.Throws<InvalidDataException>(() => UpstreamIdentity.RestUser(textIdentity.RootElement, ActorKind.Human));
    }

    [Fact]
    public void GraphQlIdentityRequiresNodeIdAndSupportedTypenameNotLoginOrDatabaseIdAlone()
    {
        foreach (var json in new[]
        {
            """{"author":{"login":"trusted","databaseId":42}}""",
            """{"author":{"login":"trusted","id":"node","__typename":"Organization"}}""",
            """{"author":{"login":"trusted","id":42,"__typename":"User"}}""",
            """{"author":{"login":"trusted","id":"node","__typename":"Bot"}}"""
        })
        {
            using var invalid = JsonDocument.Parse(json);
            Assert.Throws<InvalidDataException>(() => UpstreamIdentity.GraphQlAuthor(invalid.RootElement, ActorKind.Human));
        }
        using var valid = JsonDocument.Parse("""{"author":{"login":"trusted","id":"user-node","__typename":"User"}}""");
        var actor = UpstreamIdentity.GraphQlAuthor(valid.RootElement, ActorKind.Human);
        Assert.Null(actor.Id);
        Assert.Equal("user-node", actor.NodeId);
        Assert.Equal("User", actor.Type);
    }

    [Fact]
    public void InitialReadinessIsVerifiedButItsProjectedRemovalCannotWithdrawActiveAuthorization()
    {
        var reducer = Reducer();
        var state = Admitted();
        Assert.True(state.Issues[7].ActiveAuthorized);
        Assert.Contains("agent:ready", OrchestrationReducer.ProjectLabels(state.Issues[7]));
        state = SucceedEffect(state, EffectKind.LaunchTask, "coding-task");
        Assert.DoesNotContain("agent:ready", OrchestrationReducer.ProjectLabels(state.Issues[7]));
        state = reducer.Reconcile(state, Live(state, ready: false)).State;
        Assert.True(state.Issues[7].ActiveAuthorized);
        Assert.Equal(Phase.CODING, state.Issues[7].Phase);
        state = reducer.Apply(state, Event(new Withdraw(), "withdraw", at: Now.AddHours(2)));
        Assert.False(state.Issues[7].ActiveAuthorized);
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal(7, state.AdmittedIssue);
    }

    [Fact]
    public void FeedbackDuringCodingDefersCiAndIsHandedToOneExactChildTask()
    {
        var reducer = Reducer();
        var state = SucceedEffect(Admitted(), EffectKind.LaunchTask, "initial");
        var initialTask = state.Issues[7].ActiveTaskId!;
        state = reducer.Apply(state, Event(new HumanFeedback("Address this regression."), "comment:1"));
        state = reducer.Apply(state, Event(new HumanFeedback("Address this regression."), "comment:2"));
        state = reducer.Apply(state, Event(Completion(state.Issues[7], initialTask, 11, Sha(2)), "initial-complete"));
        state = reducer.Reconcile(state, Live(state, Sha(2), ready: false)).State;
        Assert.DoesNotContain(state.Outbox.Values, effect => effect.Kind is EffectKind.RunCi or EffectKind.RequestReview);
        var child = state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!];
        Assert.Equal(TaskKind.FeedbackRepair, child.Kind);
        Assert.Equal(initialTask, child.ParentTaskId);
        Assert.Equal(new[] { "comment:1", "comment:2" }, child.FeedbackEventIds);
        Assert.Equal(2, state.Issues[7].PendingFeedbackEventIds.Count);
        Assert.All(state.Issues[7].Feedback.Values, feedback => Assert.Equal(child.Id, feedback.PlannedTaskId));
        state = CompleteTask(state, 3);
        Assert.Empty(state.Issues[7].PendingFeedbackEventIds);
        Assert.All(state.Issues[7].Feedback.Values, feedback =>
        {
            Assert.Equal(child.Id, feedback.HandedOffTaskId);
            Assert.NotNull(feedback.HandoffId);
            Assert.NotNull(feedback.HandedOffAt);
        });
        Assert.Equal(Phase.CI_PENDING, state.Issues[7].Phase);
        Assert.Equal(child.Id, state.Issues[7].Ci!.TaskId);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("ci-pending")]
    [InlineData("ci-confirmed")]
    [InlineData("review-pending")]
    [InlineData("review-confirmed")]
    public void SinglePassFeedbackCancelsPendingWorkAndWaitsForTrackedOperationsBeforePersistentHold(string stage)
    {
        var reducer = Reducer();
        var state = Admitted();
        state.Issues[7].SinglePassTest = true;
        EventData? settlement = null;
        string operationId;
        if (stage == "coding")
        {
            state = SucceedEffect(state, EffectKind.LaunchTask, "coding");
            var taskId = state.Issues[7].ActiveTaskId!;
            operationId = state.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask).Id;
            settlement = Completion(state.Issues[7], taskId, 11, Sha(2));
        }
        else
        {
            state = CompleteTask(state, 2);
            var kind = EffectKind.RunCi;
            if (stage.StartsWith("review", StringComparison.Ordinal))
            {
                state = FinishCi(state, true, "green");
                state = reducer.Reconcile(state, Live(state)).State;
                kind = EffectKind.RequestReview;
            }
            operationId = state.Outbox.Values.Single(effect => effect.Kind == kind).Id;
            if (stage.EndsWith("confirmed", StringComparison.Ordinal))
            {
                state = SucceedEffect(state, kind, "operation");
                var issue = state.Issues[7];
                settlement = kind == EffectKind.RunCi
                    ? new CiResult("operation", issue.Ci!.TaskId, issue.Ci.Round, 11, issue.Ci.Sha, true)
                    : new ReviewResult("operation", issue.Review!.TaskId, issue.Review.Round, 11,
                        issue.Review.Sha, ReviewClassification.Accepted, [], []);
            }
        }
        var taskIdBeforeFeedback = state.Issues[7].ActiveTaskId;
        var agentIntents = state.Outbox.Values.Where(effect =>
            effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview)
            .Select(effect => effect.Id).Order().ToArray();

        state = reducer.Apply(state, Event(new HumanFeedback("Stop and address this feedback."), "single-feedback"));
        Assert.Equal(settlement is null ? DeliveryStatus.Cancelled : DeliveryStatus.Succeeded,
            state.Outbox[operationId].Status);
        state = reducer.Reconcile(state, Live(state, ready: false)).State;
        if (settlement is not null)
        {
            Assert.Equal(Phase.FIX_PENDING, state.Issues[7].Phase);
            Assert.Equal(7, state.AdmittedIssue);
            Assert.Equal(taskIdBeforeFeedback, state.Issues[7].ActiveTaskId);
            if (stage == "coding")
                Assert.Equal(TaskStatus.Running, state.Issues[7].Tasks[taskIdBeforeFeedback!].Status);
            else if (stage == "ci-confirmed")
            {
                Assert.Equal("operation", state.Issues[7].Ci!.RunId);
                Assert.Null(state.Issues[7].Ci!.Green);
            }
            else
            {
                Assert.Equal("operation", state.Issues[7].Review!.ReviewId);
                Assert.Null(state.Issues[7].Review!.Classification);
            }
            state = StateJson.Deserialize(StateJson.Serialize(state));
            state = reducer.Apply(state, Event(settlement, "single-settlement"));
            state = reducer.Reconcile(state, Live(state, stage == "coding" ? Sha(2) : null, ready: false)).State;
            if (stage == "coding")
                Assert.Equal(TaskStatus.Completed, state.Issues[7].Tasks[taskIdBeforeFeedback!].Status);
            else if (stage == "ci-confirmed")
                Assert.True(state.Issues[7].Ci!.Green);
            else
                Assert.Equal(ReviewClassification.Accepted, state.Issues[7].Review!.Classification);
        }

        AssertHeldWithoutMoreWork(state);
        state = StateJson.Deserialize(StateJson.Serialize(state));
        state = reducer.Apply(state, Event(new Ready(), "single-ready-after-feedback"));
        state = reducer.Apply(state, Event(new Resume(), "single-resume-after-feedback"));
        state = reducer.Reconcile(state, Live(state)).State;
        AssertHeldWithoutMoreWork(state);

        void AssertHeldWithoutMoreWork(RepositoryState held)
        {
            var issue = held.Issues[7];
            Assert.True(issue.SinglePassTest);
            Assert.Equal(Phase.BLOCKED, issue.Phase);
            Assert.Equal("Single-pass test does not allow repair tasks.", issue.HoldReason);
            Assert.True(issue.ReactivationRequired);
            Assert.Null(held.AdmittedIssue);
            Assert.Equal(taskIdBeforeFeedback, issue.ActiveTaskId);
            Assert.Equal(TaskKind.Coding, Assert.Single(issue.Tasks.Values).Kind);
            Assert.Equal("single-feedback", Assert.Single(issue.PendingFeedbackEventIds));
            Assert.Null(issue.Feedback["single-feedback"].PlannedTaskId);
            Assert.Null(issue.Feedback["single-feedback"].HandedOffTaskId);
            Assert.Equal(agentIntents, held.Outbox.Values.Where(effect =>
                effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview)
                .Select(effect => effect.Id).Order().ToArray());
            Assert.DoesNotContain(held.Outbox.Values, effect => effect.Status == DeliveryStatus.Pending &&
                effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeedbackCancelsPendingCiOrReviewBeforeCreatingFeedbackRepair(bool reviewPending)
    {
        var reducer = Reducer();
        var state = CompleteTask(Admitted(), 2);
        var kind = EffectKind.RunCi;
        if (reviewPending)
        {
            state = FinishCi(state, true, "green");
            state = reducer.Reconcile(state, Live(state)).State;
            kind = EffectKind.RequestReview;
        }
        var originalIntent = state.Outbox.Values.Single(effect => effect.Kind == kind && effect.Status == DeliveryStatus.Pending).Id;
        state = reducer.Apply(state, Event(new HumanFeedback("Please address this first."), "feedback"));
        Assert.Equal(DeliveryStatus.Cancelled, state.Outbox[originalIntent].Status);
        state = reducer.Reconcile(state, Live(state, ready: false)).State;
        Assert.Equal(Phase.FIX_PENDING, state.Issues[7].Phase);
        var child = state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!];
        Assert.Equal(TaskKind.FeedbackRepair, child.Kind);
        Assert.Equal("feedback", Assert.Single(child.FeedbackEventIds));
        Assert.DoesNotContain(state.Outbox.Values, effect => effect.Status == DeliveryStatus.Pending &&
            effect.Kind is EffectKind.RunCi or EffectKind.RequestReview);
    }

    [Fact]
    public void ScopedClassificationWaitsForAdmissionAndReactivationRequiresAuditedReason()
    {
        var reducer = Reducer();
        var state = FinishReview(FinishCi(CompleteTask(Admitted(), 2), true, "green"),
            ReviewClassification.NeedsHuman, "ambiguous");
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Null(state.AdmittedIssue);
        var handoff = state.Issues[7].CurrentHandoffId!;
        state = reducer.Apply(state, Event(new Ready(), "ready8", 8));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(8, state.AdmittedIssue);
        state = reducer.Apply(state, Event(new HumanClassification(handoff, 1, 11, Sha(2),
            ReviewClassification.Blocking, "Explicit adjudication.", [new Finding("f", "Defect", "Evidence")]), "classify"));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(8, state.AdmittedIssue);
        Assert.Single(state.Issues[7].Tasks);
        state = reducer.Apply(state, Event(new Pause("Yield repository slot."), "pause8", 8));
        state = reducer.Reconcile(state, Live(state)).State;
        Assert.Equal(7, state.AdmittedIssue);
        Assert.Equal(TaskKind.ReviewRepair, state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!].Kind);
        var queued = reducer.Apply(Empty(), Event(new Ready(), "ready"));
        queued = reducer.Apply(queued, Event(new Pause("Hold."), "pause"));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(queued, Event(new Reactivate(" "), "empty-reason")));
        queued = reducer.Apply(queued, Event(new Reactivate("Operator accepted the new generation."), "reactivate"));
        var audit = queued.Issues[7].Reactivations["reactivate"];
        Assert.Equal(1, audit.PreviousGeneration);
        Assert.Equal(2, audit.Generation);
        Assert.Equal("Operator accepted the new generation.", audit.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausedConfirmedCiOrReviewRetainsAdmissionUntilResultSettlesWithoutClearingHold(bool reviewPending)
    {
        var reducer = Reducer();
        var state = CompleteTask(Admitted(), 2);
        EventData result;
        if (reviewPending)
        {
            state = FinishCi(state, true, "green");
            state = reducer.Reconcile(state, Live(state)).State;
            state = SucceedEffect(state, EffectKind.RequestReview, "review");
            var review = state.Issues[7].Review!;
            result = new ReviewResult("review", review.TaskId, review.Round, 11, review.Sha,
                ReviewClassification.Accepted, [], []);
        }
        else
        {
            state = SucceedEffect(state, EffectKind.RunCi, "run");
            var ci = state.Issues[7].Ci!;
            result = new CiResult("run", ci.TaskId, ci.Round, 11, ci.Sha, true);
        }
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state,
            Event(new Reactivate("Cannot bypass an active phase."), "active-reactivate")));
        state.Issues[7].ConsecutiveCiFailures = 2;
        state = reducer.Apply(state, Event(new Pause("Operator hold."), "pause"));
        Assert.Equal(7, state.AdmittedIssue);
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state,
            Event(new Reactivate("Cannot bypass a confirmed pending operation."), "pending-reactivate")));
        state = reducer.Apply(state, Event(result, "settled"));
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal("Operator hold.", state.Issues[7].HoldReason);
        Assert.Equal(2, state.Issues[7].ConsecutiveCiFailures);
        Assert.Null(state.AdmittedIssue);
        state = reducer.Apply(state, Event(new Reactivate("Operation settled; new authorized generation."), "reactivate"));
        Assert.Equal(2, state.Issues[7].Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeedbackWaitsForConfirmedCiOrReviewThenHandsOffExactlyOnce(bool reviewPending)
    {
        var reducer = Reducer();
        var state = CompleteTask(Admitted(), 2);
        EventData result;
        if (reviewPending)
        {
            state = FinishCi(state, true, "green");
            state = reducer.Reconcile(state, Live(state)).State;
            state = SucceedEffect(state, EffectKind.RequestReview, "review");
            var review = state.Issues[7].Review!;
            result = new ReviewResult("review", review.TaskId, review.Round, 11, review.Sha,
                ReviewClassification.Accepted, [], []);
        }
        else
        {
            state = SucceedEffect(state, EffectKind.RunCi, "run");
            var ci = state.Issues[7].Ci!;
            result = new CiResult("run", ci.TaskId, ci.Round, 11, ci.Sha, true);
        }
        state = reducer.Apply(state, Event(new HumanFeedback("Address this before proceeding."), "feedback"));
        state = reducer.Reconcile(state, Live(state, ready: false)).State;
        Assert.Single(state.Issues[7].Tasks);
        Assert.Equal(7, state.AdmittedIssue);
        state = reducer.Apply(state, Event(result, "settled"));
        state = reducer.Reconcile(state, Live(state, ready: false)).State;
        var child = state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!];
        Assert.Equal(TaskKind.FeedbackRepair, child.Kind);
        Assert.Equal("feedback", Assert.Single(child.FeedbackEventIds));
        state = CompleteTask(state, 3);
        Assert.Equal(child.Id, state.Issues[7].Feedback["feedback"].HandedOffTaskId);
        Assert.Empty(state.Issues[7].PendingFeedbackEventIds);
        var repeated = reducer.Apply(state, Event(new HumanFeedback("Address this before proceeding."), "feedback"));
        Assert.Equal(StateJson.Serialize(state), StateJson.Serialize(repeated));
    }

    [Fact]
    public void StateRoundTripIncludesTypedEventDigestAndBoundedIndexesFailClosed()
    {
        var state = Admitted();
        Assert.Equal(StateJson.Serialize(state), StateJson.Serialize(StateJson.Deserialize(StateJson.Serialize(state))));
        for (var index = state.Inbox.Count; index < OrchestrationReducer.MaximumIndexEntries; index++)
            state.Inbox[$"filler{index}"] = new($"filler{index}", "digest", DeliveryStatus.Succeeded, Now, Now);
        Assert.Throws<InvalidOperationException>(() => Reducer().Apply(state, Event(new Resume(), "full")));
        Assert.Throws<InvalidDataException>(() => Reducer().Reconcile(new RepositoryState { Repository = "owner/repo", SchemaVersion = 2 }, new()));
    }

    [Fact]
    public void OfflineFixturesAcceptLowercasePropertiesAndTypedEventDiscriminators()
    {
        var state = StateJson.Deserialize("""{"repository":"owner/repo"}""");
        var message = JsonSerializer.Deserialize<OrchestrationEvent>("""
            {
              "id":"ready:7","repository":"owner/repo","issueNumber":7,
              "repositoryGeneration":1,"issueGeneration":1,
              "actor":{"login":"maintainer","kind":"Human"},
              "occurredAt":"2026-01-01T00:00:00Z","data":{"type":"ready"}
            }
            """, StateJson.Options);
        Assert.IsType<Ready>(message!.Data);
        var result = Reducer().Apply(state, message);
        Assert.True(result.Issues[7].Ready);
        Assert.Equal(StateJson.Serialize(RepositoryState.Create("owner/repo")), StateJson.Serialize(state));
        Assert.Throws<ArgumentException>(() => RepositoryState.Create(" "));
    }

    [Fact]
    public void InboxRetainsFullReadyPauseAndReactivateCommandsAcrossStateRoundTrip()
    {
        var reducer = Reducer();
        var ready = Event(new Ready(), "audit:ready") with
        {
            Actor = new Actor("maintainer", ActorKind.Human, 42, "user-node", "User")
        };
        var pause = ready with
        {
            Id = "audit:pause", Data = new Pause("Operator requested a pause."), OccurredAt = Now.AddMinutes(1)
        };
        var reactivate = ready with
        {
            Id = "audit:reactivate", Data = new Reactivate("Operator accepted a new generation."),
            OccurredAt = Now.AddMinutes(2)
        };
        var state = reducer.Apply(Empty(), ready);
        state = reducer.Apply(state, pause);
        state = reducer.Apply(state, reactivate);
        var roundTrip = StateJson.Deserialize(StateJson.Serialize(state));
        Assert.Equal(3, roundTrip.Inbox.Count);
        foreach (var original in new[] { ready, pause, reactivate })
        {
            var entry = roundTrip.Inbox[original.Id];
            Assert.NotNull(entry.PayloadJson);
            Assert.Equal(original, entry.Event);
            Assert.Equal(original.Digest, entry.Digest);
            Assert.Equal(original.Digest, entry.Event!.Digest);
            Assert.Equal(original.Actor, entry.Event.Actor);
            Assert.Equal(original.OccurredAt, entry.Event.OccurredAt);
            Assert.Equal(1, entry.Event.IssueGeneration);
        }
        Assert.Equal(2, roundTrip.Issues[7].Generation);
        Assert.Equal("Operator requested a pause.", Assert.IsType<Pause>(roundTrip.Inbox[pause.Id].Event!.Data).Reason);
        Assert.Equal("Operator accepted a new generation.",
            Assert.IsType<Reactivate>(roundTrip.Inbox[reactivate.Id].Event!.Data).Reason);
        Assert.Equal(StateJson.Serialize(roundTrip), StateJson.Serialize(reducer.Apply(roundTrip, ready)));
    }

    [Fact]
    public void InboxPayloadIsDetachedFromMutableEventListsAndAccessorReturnsDefensiveCopies()
    {
        var reducer = Reducer();
        var state = FinishCi(CompleteTask(Admitted(), 2), true, "audit-green");
        state = reducer.Reconcile(state, Live(state)).State;
        state = SucceedEffect(state, EffectKind.RequestReview, "audit-review");
        var review = state.Issues[7].Review!;
        var findings = new List<Finding> { new("f", "Original defect", "Original evidence") };
        var responses = new List<FindingResponse>();
        var message = Event(new ReviewResult("audit-review", review.TaskId, review.Round, 11, review.Sha,
            ReviewClassification.Blocking, findings, responses), "audit:review");
        var originalDigest = message.Digest;
        state = reducer.Apply(state, message);
        var originalPayload = state.Inbox[message.Id].PayloadJson;
        findings[0] = new Finding("f", "Mutated defect", "Mutated evidence");
        findings.Add(new Finding("injected", "Injected defect", "Injected evidence"));
        responses.Add(new FindingResponse("f", "Injected resolution", true));
        var copy = Assert.IsType<ReviewResult>(state.Inbox[message.Id].Event!.Data);
        Assert.Equal("Original defect", Assert.Single(copy.Findings).Description);
        Assert.Empty(copy.Responses);
        copy.Findings.Clear();
        copy.Responses.Add(new FindingResponse("f", "Accessor mutation", true));
        var restored = StateJson.Deserialize(StateJson.Serialize(state));
        var accepted = Assert.IsType<ReviewResult>(restored.Inbox[message.Id].Event!.Data);
        Assert.Equal("Original defect", Assert.Single(accepted.Findings).Description);
        Assert.Empty(accepted.Responses);
        Assert.Equal("Original defect", Assert.Single(restored.Issues[7].Review!.Findings).Description);
        Assert.Equal(originalPayload, restored.Inbox[message.Id].PayloadJson);
        Assert.Equal(originalDigest, restored.Inbox[message.Id].Digest);
        Assert.Equal(originalDigest, restored.Inbox[message.Id].Event!.Digest);
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state, message));
    }

    private sealed class TrustedPolicy : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => message.Actor.Login is
            "maintainer" or "task-service" or "ci-service" or "review-service";
    }
}
