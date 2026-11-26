using System.Text.Json;
using System.Net;
using System.Text;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class RestartPreparationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:02:00Z");
    private static readonly string Sha = new('a', 40);
    private static readonly OrchestrationReducer Reducer = new(new Allow());

    [Fact]
    public void AcknowledgedEarlierHeadRiskIsArchivedWithoutResolvingOrReplayingTheUnknownIntent()
    {
        var original = StatusRiskState();
        var unknown = JsonSerializer.Serialize(original.Outbox["uncertain"], StateJson.Options);
        var command = Command(new PrepareRestart(17, Sha, "Accept possible late pending write to discarded earlier head.")
        {
            AcknowledgedUncertainStatusIntentIds = ["uncertain"],
            StatusRiskVerification = new([new("uncertain", "corrected-earlier-head", new string('b', 40), 101)], "revoked", 102)
        });

        var prepared = Reducer.Apply(original, command);
        var audit = Assert.Single(prepared.Issues[7].RestartPreparations.Values);
        var risk = Assert.Single(audit.StatusRiskVerification!.Acknowledgements);
        Assert.Equal("uncertain", risk.UncertainIntentId);
        Assert.Equal("corrected-earlier-head", risk.CorrectiveIntentId);
        Assert.Equal(101, risk.CorrectiveStatusId);
        Assert.Equal(unknown, JsonSerializer.Serialize(prepared.Outbox["uncertain"], StateJson.Options));
        Assert.Equal(StateJson.Serialize(prepared), StateJson.Serialize(Reducer.Apply(prepared, command)));
        Assert.Equal(risk, Assert.Single(StateJson.Deserialize(StateJson.Serialize(prepared))
            .Issues[7].RestartPreparations.Values.Single().StatusRiskVerification!.Acknowledgements));
        Assert.False(prepared.Issues[7].Ready);
        Assert.Null(prepared.Issues[7].PullRequest);

        var ready = Command(new Ready(), "new-ready", 2) with { OccurredAt = Now.AddMinutes(1) };
        var restarted = Reducer.Reconcile(Reducer.Apply(prepared, ready), Live(true)).State;
        var launch = Assert.Single(restarted.Outbox.Values, effect =>
            effect.Generation == 2 && effect.Kind == EffectKind.LaunchTask);
        Assert.Null(launch.Sha);
        Assert.Empty(restarted.Issues[7].Tasks[launch.TaskId!].ReviewBlockers);
        Assert.Equal(unknown, JsonSerializer.Serialize(restarted.Outbox["uncertain"], StateJson.Options));
    }

    [Theory]
    [InlineData("missing-ack")]
    [InlineData("extra-ack")]
    [InlineData("duplicate-ack")]
    [InlineData("success")]
    [InlineData("prepared")]
    [InlineData("external-receipt")]
    [InlineData("wrong-generation")]
    [InlineData("current-sha")]
    [InlineData("untracked-sha")]
    [InlineData("wrong-pr-evidence")]
    [InlineData("missing-launch")]
    [InlineData("missing-correction")]
    [InlineData("stale-correction")]
    [InlineData("unconfirmed-correction")]
    public void AcknowledgementCannotBypassOtherUncertaintyOrMissingRevocation(string problem)
    {
        var state = StatusRiskState();
        var ids = new List<string> { "uncertain" };
        var uncertain = state.Outbox["uncertain"];
        switch (problem)
        {
            case "missing-ack": ids.Clear(); break;
            case "extra-ack": ids.Add("not-canonical"); break;
            case "duplicate-ack": ids.Add("uncertain"); break;
            case "success": uncertain.RequiredStatus = uncertain.RequiredStatus! with { State = "success" }; break;
            case "prepared": uncertain.RequiredStatusBoundary = new(9, "trusted[bot]", "BOT_9", "Bot", Now, []); break;
            case "external-receipt": uncertain.ExternalId = "123"; break;
            case "wrong-generation": uncertain.Generation = 2; break;
            case "current-sha": uncertain.Sha = Sha; break;
            case "untracked-sha": uncertain.Sha = new string('c', 40); break;
            case "wrong-pr-evidence":
                var task = state.Issues[7].Tasks["earlier-task"];
                task.CandidateShaEvidence = task.CandidateShaEvidence! with { PullRequest = 18 };
                break;
            case "missing-launch": state.Outbox.Remove("earlier-launch"); break;
            case "missing-correction": state.Outbox.Remove("corrected-earlier-head"); break;
            case "stale-correction":
                var correction = state.Outbox["corrected-earlier-head"];
                correction.RequiredStatusBoundary = correction.RequiredStatusBoundary! with { PreparedAt = Now.AddHours(-1) };
                break;
            case "unconfirmed-correction": state.Outbox["corrected-earlier-head"].Status = DeliveryStatus.Unknown; break;
        }
        var before = StateJson.Serialize(state);
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state, Command(new PrepareRestart(17, Sha, "Audit")
        {
            AcknowledgedUncertainStatusIntentIds = ids
        })));
        Assert.Equal(before, StateJson.Serialize(state));
    }

    [Fact]
    public async Task ProductionAcknowledgementVerifiesLiveCorrectionsAndOnlyProjectsFreshWaitingState()
    {
        var state = StatusRiskState();
        using var fixture = Fixture();
        using var statuses = new StatusRiskApi(fixture, state);
        using var client = new HttpClient(statuses, false) { BaseAddress = new("https://api.github.com/") };
        var store = new GitHubTaskIntegrationTests.Store(state);
        var unknown = JsonSerializer.Serialize(state.Outbox["uncertain"], StateJson.Options);
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());

        await PrepareAcknowledged(host);
        await PrepareAcknowledged(host);

        Assert.Equal(2, store.State.Issues[7].Generation);
        Assert.False(store.State.Issues[7].Ready);
        Assert.Single(store.State.Issues[7].RestartPreparations);
        Assert.Equal(unknown, JsonSerializer.Serialize(store.State.Outbox["uncertain"], StateJson.Options));
        Assert.Equal(2, statuses.StatusReads);
        Assert.All(fixture.Writes, write => Assert.StartsWith("/repos/owner/repo/issues/7/", write.Path));
        Assert.DoesNotContain(store.State.Outbox.Values, effect => effect.Generation == 2 &&
            effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview or EffectKind.PublishRequiredStatus);
    }

    [Theory]
    [InlineData("missing-status")]
    [InlineData("later-status")]
    [InlineData("wrong-receipt")]
    [InlineData("publisher")]
    [InlineData("current-head")]
    [InlineData("no-approval")]
    public async Task ProductionAcknowledgementRejectsUnverifiedLiveStatusBeforeSaving(string problem)
    {
        var state = StatusRiskState();
        using var fixture = Fixture();
        using var statuses = new StatusRiskApi(fixture, state, problem);
        using var client = new HttpClient(statuses, false) { BaseAddress = new("https://api.github.com/") };
        var store = new GitHubTaskIntegrationTests.Store(state);
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());

        await Assert.ThrowsAsync<InvalidDataException>(() => PrepareAcknowledged(host, problem != "no-approval"));

        Assert.Equal(1, store.State.Issues[7].Generation);
        Assert.Empty(store.State.Issues[7].RestartPreparations);
        Assert.Empty(fixture.Writes);
        Assert.Equal(DeliveryStatus.Unknown, store.State.Outbox["uncertain"].Status);
    }

    [Theory]
    [InlineData("corrected-earlier-head")]
    [InlineData("revoked")]
    public async Task SaveConflictCannotReplaceTheVerifiedStatusReceipts(string changedIntent)
    {
        var state = StatusRiskState();
        using var fixture = Fixture();
        using var statuses = new StatusRiskApi(fixture, state);
        using var client = new HttpClient(statuses, false) { BaseAddress = new("https://api.github.com/") };
        var store = new GitHubTaskIntegrationTests.Store(state);
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1),
            new ReceiptRaceStore(store, changedIntent), new Clock());

        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAcknowledged(host));

        Assert.Equal(1, store.State.Issues[7].Generation);
        Assert.Empty(store.State.Issues[7].RestartPreparations);
        Assert.Empty(fixture.Writes);
        Assert.Equal(DeliveryStatus.Unknown, store.State.Outbox["uncertain"].Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[\"uncertain\",\"uncertain\"]")]
    [InlineData("not-json")]
    public void RestartIngressRejectsMalformedRiskAcknowledgement(string ids)
    {
        var payload = Payload(ids);
        var evidence = new IngressEvidence("owner/repo", 1, 55, "User", "write", 70, 7, true, false, false, []);
        Assert.Throws<InvalidDataException>(() =>
            GitHubActionsIngress.PrepareEvent(payload, "workflow_dispatch", "44", evidence, 1, 1, Now));
    }

    [Fact]
    public void PreparationArchivesOldObligationsAndWaitsForFreshReadiness()
    {
        var original = ClosedState();
        var command = Command();
        var prepared = Reducer.Apply(original, command);
        var issue = prepared.Issues[7];

        Assert.Equal(2, issue.Generation);
        Assert.Equal(Phase.READY, issue.Phase);
        Assert.False(issue.Ready);
        Assert.False(issue.ActiveAuthorized);
        Assert.False(issue.ReactivationRequired);
        Assert.Null(issue.PullRequest);
        Assert.Null(issue.HeadSha);
        Assert.Null(issue.ActiveTaskId);
        Assert.Null(issue.Review);
        Assert.Null(issue.Ci);
        var audit = Assert.Single(issue.RestartPreparations.Values);
        Assert.Equal(17, audit.PullRequest);
        Assert.Equal("old-task", audit.TaskId);
        Assert.Equal("blocker", Assert.Single(audit.Review!.Findings).Id);
        Assert.NotNull(audit.Ci);
        Assert.Single(issue.Tasks);
        Assert.Single(issue.TerminalHistory);
        Assert.Single(prepared.Outbox);
        Assert.Equal(StateJson.Serialize(prepared), StateJson.Serialize(Reducer.Apply(prepared, command)));
        Assert.Contains("waiting for the maintainer", StatusProjection.Render(issue));

        var live = Live(ready: false);
        var waiting = Reducer.Reconcile(prepared, live).State;
        Assert.Single(waiting.Issues[7].Tasks);
        Assert.DoesNotContain(waiting.Outbox.Values, effect => effect.Generation == 2 &&
            effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview);
        var staleReady = Command(new Ready(), "delayed-ready", 2) with { OccurredAt = Now.AddSeconds(-1) };
        waiting = Reducer.Apply(waiting, staleReady);
        Assert.False(waiting.Issues[7].Ready);

        var ready = Command(new Ready(), "new-ready", 2) with { OccurredAt = Now.AddMinutes(1) };
        var restarted = Reducer.Reconcile(Reducer.Apply(waiting, ready), Live(ready: true)).State;
        var task = restarted.Issues[7].Tasks[restarted.Issues[7].ActiveTaskId!];
        Assert.Equal(TaskKind.Coding, task.Kind);
        Assert.Equal(TaskLaunchSource.UnmappedIssue, task.LaunchSource);
        Assert.Null(task.ParentTaskId);
        Assert.Null(task.SourceSha);
        Assert.Empty(task.ReviewBlockers);
        Assert.Equal(2, restarted.Issues[7].Round);
        Assert.Single(restarted.Outbox.Values, effect => effect.Generation == 2 && effect.Kind == EffectKind.LaunchTask);
        Assert.Equal(1, original.Issues[7].Generation);
        Assert.Contains("review feedback does not expand", restarted.Outbox.Values
            .Single(effect => effect.Generation == 2 && effect.Kind == EffectKind.LaunchTask).TaskPrompt);
    }

    [Theory]
    [InlineData("merged")]
    [InlineData("open-pr")]
    [InlineData("wrong-head")]
    [InlineData("running-task")]
    [InlineData("unknown-effect")]
    [InlineData("pending-effect")]
    [InlineData("other-uncertain")]
    [InlineData("other-executing-status")]
    [InlineData("own-uncertain-status")]
    [InlineData("status-not-revoked")]
    [InlineData("single-pass")]
    [InlineData("stale-generation")]
    [InlineData("wrong-pr")]
    [InlineData("missing-reason")]
    public void PreparationCannotEraseAnUnsafeOrMismatchedAttempt(string problem)
    {
        var state = ClosedState();
        var issue = state.Issues[7];
        var command = Command();
        switch (problem)
        {
            case "merged": issue.TerminalOutcome = TerminalOutcome.Merged; break;
            case "open-pr": issue.Phase = Phase.BLOCKED; break;
            case "wrong-head": issue.HeadSha = new string('b', 40); break;
            case "running-task": issue.Tasks["old-task"].Status = TaskStatus.Running; break;
            case "unknown-effect": state.Outbox["launch"].Status = DeliveryStatus.Unknown; break;
            case "pending-effect": state.Outbox["launch"].Status = DeliveryStatus.Pending; break;
            case "other-uncertain":
            case "other-executing-status":
                state.Issues[8] = new() { Number = 8 };
                state.Outbox["other"] = new()
                {
                    Id = "other", IssueNumber = 8,
                    Kind = problem == "other-uncertain" ? EffectKind.ProjectLabels : EffectKind.PublishRequiredStatus,
                    Status = problem == "other-uncertain" ? DeliveryStatus.Unknown : DeliveryStatus.Executing
                };
                break;
            case "own-uncertain-status":
                state.Outbox["status"] = new()
                {
                    Id = "status", IssueNumber = 7, Kind = EffectKind.PublishRequiredStatus, Status = DeliveryStatus.Unknown
                };
                issue.RequiredStatusIntentId = "revoked";
                state.Outbox["revoked"] = new()
                {
                    Id = "revoked", IssueNumber = 7, Generation = 1, Kind = EffectKind.PublishRequiredStatus,
                    TaskId = issue.ActiveTaskId, Round = issue.Round, Sha = Sha, Status = DeliveryStatus.Succeeded,
                    RequiredStatus = RequiredStatusPolicy.ForIntent(state, issue, "revoked")
                };
                break;
            case "status-not-revoked":
                state.Outbox["status"] = new EffectIntent
                {
                    Id = "status", IssueNumber = 7, Generation = 1, Kind = EffectKind.PublishRequiredStatus,
                    Status = DeliveryStatus.Succeeded, RequiredStatus = new("Agent managed CI", "success", "", "", null, null)
                };
                break;
            case "single-pass": issue.SinglePassTest = true; break;
            case "stale-generation": command = command with { IssueGeneration = 2 }; break;
            case "wrong-pr": command = Command(new PrepareRestart(18, Sha, "Wrong PR")); break;
            case "missing-reason": command = Command(new PrepareRestart(17, Sha, "")); break;
        }
        var before = StateJson.Serialize(state);
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state, command));
        Assert.Equal(before, StateJson.Serialize(state));
    }

    [Fact]
    public void FreshAttemptGetsTheSameRepairCapWithoutDeletingOldTaskHistory()
    {
        var state = ClosedState();
        state.Issues[7].MaxRepairTasks = 1;
        state.Issues[7].Tasks["old-task"].Kind = TaskKind.ReviewRepair;
        state.Issues[7].ConsecutiveCiFailures = 2;
        state.Issues[7].ConsecutiveReviewFailures = 4;
        state = Reducer.Apply(state, Command());
        var audit = Assert.Single(state.Issues[7].RestartPreparations.Values);
        Assert.Equal(2, audit.ConsecutiveCiFailures);
        Assert.Equal(4, audit.ConsecutiveReviewFailures);
        Assert.Equal(0, state.Issues[7].ConsecutiveCiFailures);
        Assert.Equal(0, state.Issues[7].ConsecutiveReviewFailures);
        state = Reducer.Reconcile(Reducer.Apply(state,
            Command(new Ready(), "ready", 2) with { OccurredAt = Now.AddMinutes(1) }), Live(true)).State;

        void CompleteActiveTask()
        {
            var issue = state.Issues[7];
            issue.Tasks[issue.ActiveTaskId!].Status = TaskStatus.Completed;
            var launch = state.Outbox.Values.Single(effect => effect.TaskId == issue.ActiveTaskId &&
                effect.Kind == EffectKind.LaunchTask);
            launch.Status = DeliveryStatus.Succeeded;
            issue.PullRequest = 18;
            issue.HeadSha = Sha;
            issue.Phase = Phase.FIX_PENDING;
        }

        CompleteActiveTask();
        var mapped = Live(true);
        mapped.Issues[7].PullRequest = 18;
        mapped.Issues[7].HeadSha = Sha;
        state = Reducer.Reconcile(state, mapped).State;
        Assert.Equal(TaskKind.ReviewRepair, state.Issues[7].Tasks[state.Issues[7].ActiveTaskId!].Kind);
        Assert.Equal(3, state.Issues[7].Tasks.Count);

        CompleteActiveTask();
        state = Reducer.Reconcile(state, mapped).State;
        Assert.Equal("Repair task budget exhausted.", state.Issues[7].HoldReason);
        Assert.Equal(3, state.Issues[7].Tasks.Count);
    }

    [Fact]
    public async Task PreparationRetainsConfirmedOldHeadRevocationAndDoesNotRepublishIt()
    {
        var state = ClosedState();
        var issue = state.Issues[7];
        issue.RequiredStatusIntentId = "revoked";
        state.Outbox["revoked"] = new()
        {
            Id = "revoked", IssueNumber = 7, Generation = 1, Kind = EffectKind.PublishRequiredStatus,
            TaskId = issue.ActiveTaskId, Round = issue.Round, Sha = Sha, Status = DeliveryStatus.Succeeded,
            RequiredStatus = RequiredStatusPolicy.ForIntent(state, issue, "revoked")
        };
        Assert.Equal("error", state.Outbox["revoked"].RequiredStatus!.State);
        using var fixture = Fixture();
        using var client = fixture.Client();
        var store = new GitHubTaskIntegrationTests.Store(state);

        await Prepare(new(new(client, "owner/repo", 1), store, new Clock()), Payload());

        Assert.Null(store.State.Issues[7].RequiredStatusIntentId);
        Assert.Equal(DeliveryStatus.Succeeded, store.State.Outbox["revoked"].Status);
        Assert.DoesNotContain(fixture.Writes, write => write.Path.Contains("/statuses/", StringComparison.Ordinal));
        Assert.Equal(2, store.State.Issues[7].Generation);
    }

    [Theory]
    [InlineData("missing-pr")]
    [InlineData("invalid-sha")]
    [InlineData("missing-reason")]
    [InlineData("bot")]
    [InlineData("read-only")]
    public void RestartIngressRequiresExactInputsAndAuthorizedHuman(string problem)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, type = problem == "bot" ? "Bot" : "User" },
            inputs = new
            {
                issue = "7", operation = "prepare-restart", pull = problem == "missing-pr" ? "" : "17",
                sha = problem == "invalid-sha" ? new string('z', 40) : Sha,
                reason = problem == "missing-reason" ? "" : "Discard old attempt"
            }
        });
        var evidence = new IngressEvidence("owner/repo", 1, 55, problem == "bot" ? "Bot" : "User",
            problem == "read-only" ? "read" : "write", 70, 7, true, false, false, []);

        Assert.Throws<InvalidDataException>(() =>
            GitHubActionsIngress.PrepareEvent(payload, "workflow_dispatch", "44", evidence, 1, 1, Now));
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending, EffectKind.ProjectLabels)]
    [InlineData(DeliveryStatus.Unknown, EffectKind.PublishRequiredStatus)]
    public async Task ProductionPreparationProjectsOnlyAndReplayCannotLaunchOrIncrementAgain(
        DeliveryStatus otherStatus, EffectKind otherKind)
    {
        using var fixture = Fixture();
        var store = new GitHubTaskIntegrationTests.Store(ClosedState());
        store.State.Issues[8] = new() { Number = 8, Generation = 2, Ready = true };
        store.State.Outbox["other"] = new()
        {
            Id = "other", IssueNumber = 8, Generation = 1, Kind = otherKind, Status = otherStatus,
            Sha = new string('b', 40),
            Labels = otherKind == EffectKind.ProjectLabels ? ["agent:working"] : [],
            RequiredStatus = otherKind == EffectKind.PublishRequiredStatus
                ? new(RequiredStatusPolicy.Context, "pending", "Legacy unresolved delivery",
                    "https://github.com/owner/repo/pull/18", null, null) : null
        };
        var otherIssue = JsonSerializer.Serialize(store.State.Issues[8], StateJson.Options);
        var otherEffect = JsonSerializer.Serialize(store.State.Outbox["other"], StateJson.Options);
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        var payload = Payload();

        await Prepare(host, payload);
        await Prepare(host, payload);

        var issue = store.State.Issues[7];
        Assert.Equal(2, issue.Generation);
        Assert.Single(issue.RestartPreparations);
        Assert.Null(issue.PullRequest);
        Assert.False(issue.Ready);
        Assert.Single(issue.Tasks);
        Assert.DoesNotContain(fixture.Labels, OrchestrationReducer.IsProjectionLabel);
        Assert.All(fixture.Writes, write => Assert.StartsWith("/repos/owner/repo/issues/", write.Path));
        Assert.Contains(fixture.Writes, write => write.Path.EndsWith("/issues/7/comments", StringComparison.Ordinal) &&
            write.Body!.Contains("Restart prepared; waiting for the maintainer", StringComparison.Ordinal));
        Assert.Equal(otherIssue, JsonSerializer.Serialize(store.State.Issues[8], StateJson.Options));
        Assert.Equal(otherEffect, JsonSerializer.Serialize(store.State.Outbox["other"], StateJson.Options));
        Assert.DoesNotContain(fixture.Requests, request => request.Contains("/issues/8", StringComparison.Ordinal));
        Assert.Contains(fixture.Requests, request => request == "GET /agents/repos/owner/repo/tasks/remote-task");
        Assert.DoesNotContain(store.State.Outbox.Values, effect => effect.Generation == 2 &&
            effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview);
    }

    [Theory]
    [InlineData("open-pr")]
    [InlineData("merged-pr")]
    [InlineData("closed-issue")]
    [InlineData("task-running")]
    [InlineData("session")]
    [InlineData("creator")]
    [InlineData("later-push")]
    [InlineData("base")]
    [InlineData("ready")]
    public async Task ProductionPreparationRejectsContradictoryLiveEvidenceBeforeSaving(string problem)
    {
        using var fixture = Fixture();
        if (problem == "open-pr") fixture.PullClosed = false;
        else if (problem == "merged-pr") fixture.PullMerged = true;
        else if (problem == "closed-issue") fixture.IssueClosed = true;
        else if (problem == "task-running") fixture.RemoteState = "in_progress";
        else if (problem == "ready") fixture.Labels.Add("agent:ready");
        else fixture.Corruption = problem;
        var store = new GitHubTaskIntegrationTests.Store(ClosedState());
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());

        await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(host, Payload()));

        Assert.Equal(1, store.State.Issues[7].Generation);
        Assert.Empty(store.State.Issues[7].RestartPreparations);
        Assert.Empty(fixture.Writes);
    }

    private static Task<WorkerResult> Prepare(GitHubOrchestrationHost host, JsonElement payload) =>
        host.ReconcileAsync(payload, "workflow_dispatch", "44", 1, 1, Now, [], 99, "master", 55,
            new(true, true, true, true), commandOnly: true);

    private static Task<WorkerResult> PrepareAcknowledged(GitHubOrchestrationHost host, bool approved = true) =>
        host.ReconcileAsync(Payload("[\"uncertain\"]"), "workflow_dispatch", "44", 1, 1, Now, [], 99, "master", 55,
            new(true, true, true, true), commandOnly: true, statusPublisher: new(9, "trusted[bot]", "BOT_9", "Bot"),
            statusApproval: new(approved, true, true, true, true));

    private static GitHubTaskIntegrationTests.ApiFixture Fixture()
    {
        var fixture = new GitHubTaskIntegrationTests.ApiFixture
        {
            RunEvent = "workflow_dispatch", RemoteState = "completed", PullClosed = true
        };
        fixture.Labels.Clear();
        fixture.Labels.Add("agent:blocked");
        return fixture;
    }

    private static JsonElement Payload(string? acknowledgedIds = null) => JsonSerializer.SerializeToElement(new
    {
        repository = new { id = 1, full_name = "owner/repo" },
        sender = new { id = 55, login = "maintainer", type = "User" },
        inputs = new { issue = "7", operation = "prepare-restart", pull = "17", sha = Sha,
            reason = "Discard superseded attempt.", acknowledge_uncertain_status_intents = acknowledgedIds }
    });

    private static OrchestrationEvent Command(EventData? data = null, string id = "restart", long generation = 1) =>
        new(id, "owner/repo", 7, 1, generation, new("maintainer", ActorKind.Human, 55, Type: "User"),
            Now, data ?? new PrepareRestart(17, Sha, "Discard superseded attempt."));

    private static RepositoryState ClosedState()
    {
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new IssueState
        {
            Number = 7, Phase = Phase.CLOSED, ReactivationRequired = true, PullRequest = 17, HeadSha = Sha,
            Round = 1, ActiveTaskId = "old-task", TerminalOutcome = TerminalOutcome.Unmerged,
            TerminalHistory = [new(1, TerminalOutcome.Unmerged, 17, Sha, Now.AddMinutes(-1))],
            Ci = new() { TaskId = "old-task", Green = true, Sha = Sha },
            Review = new()
            {
                TaskId = "old-task", Sha = Sha, Classification = ReviewClassification.NeedsHuman,
                LifecycleState = ReviewLifecycleState.Completed, Findings = [new("blocker", "Old obligation", "Old review")]
            },
            Tasks = new()
            {
                ["old-task"] = new()
                {
                    Id = "old-task", Kind = TaskKind.Coding, Round = 1, Status = TaskStatus.Completed,
                    LaunchConfirmed = true, ExternalId = "remote-task", FinalSha = Sha,
                    CandidateShaEvidence = new(1, TaskTerminalRecovery.CandidateShaPolicy, "old-task", "remote-task",
                        ["session"], 17, Sha)
                }
            }
        };
        state.Outbox["launch"] = new()
        {
            Id = "launch", Digest = "saved-launch", IssueNumber = 7, Generation = 1, TaskId = "old-task",
            Kind = EffectKind.LaunchTask, Status = DeliveryStatus.Succeeded, ExternalId = "remote-task", Round = 1
        };
        return state;
    }

    private static RepositoryState StatusRiskState()
    {
        var state = ClosedState();
        var issue = state.Issues[7];
        var earlierSha = new string('b', 40);
        issue.Tasks["earlier-task"] = new()
        {
            Id = "earlier-task", Kind = TaskKind.Coding, Round = 1, Status = TaskStatus.Completed,
            LaunchConfirmed = true, ExternalId = "earlier-remote", FinalSha = earlierSha,
            CandidateShaEvidence = new(1, TaskTerminalRecovery.CandidateShaPolicy, "earlier-task", "earlier-remote",
                ["session"], 17, earlierSha)
        };
        state.Outbox["earlier-launch"] = new()
        {
            Id = "earlier-launch", IssueNumber = 7, Generation = 1, TaskId = "earlier-task",
            Kind = EffectKind.LaunchTask, Status = DeliveryStatus.Succeeded, ExternalId = "earlier-remote", Round = 1
        };
        state.Outbox["uncertain"] = new()
        {
            Id = "uncertain", IssueNumber = 7, Generation = 1, Kind = EffectKind.PublishRequiredStatus,
            Status = DeliveryStatus.Unknown, TaskId = "old-task", Round = 1, Sha = earlierSha,
            CompletedAt = Now.AddMinutes(-1),
            RequiredStatus = new(RequiredStatusPolicy.Context, "pending", "Old pending status", "old-target", null, null)
        };
        issue.RequiredStatusIntentId = "revoked";
        foreach (var (id, sha, statusId) in new[] { ("corrected-earlier-head", earlierSha, 101), ("revoked", Sha, 102) })
            state.Outbox[id] = new()
            {
                Id = id, IssueNumber = 7, Generation = 1, Kind = EffectKind.PublishRequiredStatus,
                TaskId = issue.ActiveTaskId, Round = issue.Round, Sha = sha, Status = DeliveryStatus.Succeeded,
                ExternalId = statusId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RequiredStatus = RequiredStatusPolicy.ForIntent(state, issue, id),
                RequiredStatusBoundary = new(9, "trusted[bot]", "BOT_9", "Bot", Now.AddSeconds(-30), [], statusId)
            };
        return state;
    }

    private sealed class StatusRiskApi(GitHubTaskIntegrationTests.ApiFixture inner, RepositoryState state,
        string? corruption = null) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker forwarding = new(inner, false);
        public int StatusReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            object response;
            if (path == "/users/trusted[bot]")
                response = new { id = 9, login = "trusted[bot]", node_id = "BOT_9", type = "Bot" };
            else if (path.EndsWith("/status", StringComparison.Ordinal))
                response = new { sha = path.Split('/')[5], repository = new { id = 1, full_name = "owner/repo" } };
            else if (path.EndsWith("/statuses", StringComparison.Ordinal))
            {
                StatusReads++;
                var sha = path.Split('/')[5];
                var correction = state.Outbox.Values.Single(effect =>
                    effect.Sha == sha && effect.Status == DeliveryStatus.Succeeded && effect.Kind == EffectKind.PublishRequiredStatus);
                var payload = correction.RequiredStatus!;
                var boundary = correction.RequiredStatusBoundary!;
                var invalid = corruption == "later-status" || corruption == "current-head" && sha == Sha;
                response = corruption == "missing-status" ? Array.Empty<object>() : new object[]
                {
                    new
                    {
                        id = boundary.StatusId + (invalid || corruption == "wrong-receipt" ? 1 : 0),
                        url = $"https://api.github.com/repos/owner/repo/statuses/{sha}",
                        creator = new { id = corruption == "publisher" ? 10 : 9, login = "trusted[bot]", node_id = "BOT_9", type = "Bot" },
                        context = payload.Context, state = invalid ? "pending" : payload.State,
                        target_url = payload.TargetUrl, description = payload.Description,
                        created_at = invalid ? Now : boundary.PreparedAt
                    }
                };
            }
            else return forwarding.SendAsync(request, token);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) forwarding.Dispose();
            base.Dispose(disposing);
        }
    }

    private static LiveObservation Live(bool ready) => new()
    {
        ObservedAt = Now.AddMinutes(1),
        Issues = { [7] = new() { Ready = ready } }
    };

    private sealed class ReceiptRaceStore(GitHubTaskIntegrationTests.Store inner, string changedIntent) : IStateStore
    {
        private bool changed;
        public Task<StateSnapshot> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken token)
        {
            if (!changed && next.Issues[7].Generation == 2)
            {
                changed = true;
                var effect = inner.State.Outbox[changedIntent];
                var id = effect.RequiredStatusBoundary!.StatusId!.Value + 100;
                effect.RequiredStatusBoundary = effect.RequiredStatusBoundary with { StatusId = id };
                effect.ExternalId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                inner.State.Version++;
            }
            return inner.TrySaveAsync(expected, next, token);
        }
    }

    private sealed class Allow : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => true;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
