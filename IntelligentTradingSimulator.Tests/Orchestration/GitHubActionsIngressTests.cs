using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public class GitHubActionsIngressTests
{
    private static IngressEvidence Evidence(
        bool ready = true, bool open = true, string permission = "write",
        long actor = 42, string actorType = "User", int number = 7) =>
        new("owner/repo", 100, actor, actorType, permission, 200, number, open, ready, false,
            new HashSet<long> { 999 });

    private static JsonElement Issue(
        string action = "labeled", string label = "agent:ready",
        long actor = 42, string actorType = "User", long repository = 100, long issue = 200, int number = 7) =>
        JsonSerializer.SerializeToElement(new
        {
            action,
            repository = new { id = repository, full_name = "owner/repo" },
            sender = new { id = actor, login = "untrusted-display-name", type = actorType },
            issue = new { id = issue, number, state = "open" },
            label = new { id = 300, name = label }
        });

    [Fact]
    public void AuthorizedReadyNotificationUsesStableRunIdentity()
    {
        var first = GitHubActionsIngress.Parse(Issue(), "issues", "123", Evidence());
        var replay = GitHubActionsIngress.Parse(Issue(), "issues", "123", Evidence());
        Assert.Equal(first, replay);
        Assert.Equal(NotificationKind.Readiness, first.Kind);
        Assert.Equal("actions:100:123", first.SourceId);
        Assert.Equal(42, first.ActorId);
    }

    [Fact]
    public void ReadyCarriesOnlyConfiguredBoundedRepairTaskCap()
    {
        var message = GitHubActionsIngress.PrepareEvent(Issue(), "issues", "123", Evidence(),
            1, 1, DateTimeOffset.UnixEpoch, maxRepairTasks: 1);
        Assert.Equal(1, Assert.IsType<Ready>(message!.Data).MaxRepairTasks);
        Assert.DoesNotContain("MaxRepairTasks", JsonSerializer.Serialize(new Ready(), StateJson.Options));
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.PrepareEvent(
            Issue(), "issues", "123", Evidence(), 1, 1, DateTimeOffset.UnixEpoch, maxRepairTasks: 0));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("maintain")]
    [InlineData("admin")]
    public void PatOwnerReadyIsAcceptedAndReplayDoesNotLaunchAgain(string permission)
    {
        const long patOwner = 6195946;
        var context = Context() with
        {
            Evidence = Evidence(actor: patOwner, permission: permission) with
            {
                ProjectionActorIds = [GitHubLiveCredentials.ProjectionActorId]
            }
        };
        var payload = Issue(actor: patOwner);
        var first = OfflineNotificationReplay.Run(payload, "issues", context);
        var replay = OfflineNotificationReplay.Run(payload, "issues", context with { State = first.State });
        Assert.Equal(Phase.CODING, replay.State.Issues[7].Phase);
        Assert.Equal(patOwner, Assert.Single(replay.State.Inbox.Values).Event!.Actor.Id);
        Assert.Single(replay.State.Issues[7].Tasks);
        Assert.Single(replay.State.Outbox.Values, intent => intent.Kind == EffectKind.LaunchTask);
    }

    [Theory]
    [InlineData("agent:ready")]
    [InlineData("agent:working")]
    [InlineData("agent:review")]
    public void VerifiedActionsBotProjectionIsIgnored(string label)
    {
        var evidence = Evidence(actor: GitHubLiveCredentials.ProjectionActorId, actorType: "Bot") with
        {
            ProjectionActorIds = [GitHubLiveCredentials.ProjectionActorId]
        };
        Assert.Null(GitHubActionsIngress.PrepareEvent(
            Issue(label: label, actor: GitHubLiveCredentials.ProjectionActorId, actorType: "Bot"),
            "issues", "123", evidence, 1, 1, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DelayedReadyCannotOverrideLiveWithdrawalOrClosure(bool ready, bool open)
    {
        var result = GitHubActionsIngress.Parse(Issue(), "issues", "123", Evidence(ready, open));
        Assert.Equal(NotificationKind.WithdrawReadiness, result.Kind);
    }

    [Fact]
    public void WithdrawalCannotTurnLaterRelabelIntoNewCommand()
    {
        var result = GitHubActionsIngress.Parse(Issue("unlabeled"), "issues", "123", Evidence());
        Assert.Equal(NotificationKind.Ignore, result.Kind);
    }

    [Theory]
    [InlineData("agent:working")]
    [InlineData("agent:ci")]
    [InlineData("agent:ai-review")]
    [InlineData("agent:review")]
    [InlineData("agent:blocked")]
    public void HumanStageEditsAreProjectionNotificationsNotTransitions(string label)
    {
        Assert.Equal(NotificationKind.Projection,
            GitHubActionsIngress.Parse(Issue(label: label), "issues", "123", Evidence()).Kind);
    }

    [Theory]
    [InlineData("agent:ready")]
    [InlineData("agent:review")]
    [InlineData("agent:blocked")]
    public void TrustedProjectionActorDoesNotCreateCommandLoop(string label)
    {
        Assert.Equal(NotificationKind.Ignore,
            GitHubActionsIngress.Parse(Issue(label: label, actor: 999, actorType: "Bot"),
                "issues", "123", Evidence(actor: 999, actorType: "Bot")).Kind);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("triage")]
    [InlineData("")]
    public void ReadinessRequiresVerifiedWritePermission(string permission)
    {
        Assert.Throws<InvalidDataException>(() =>
            GitHubActionsIngress.Parse(Issue(), "issues", "123", Evidence(permission: permission)));
    }

    [Fact]
    public void BotCannotImpersonateHumanByDisplayLogin()
    {
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.Parse(
            Issue(actorType: "Bot"), "issues", "123", Evidence(actorType: "Bot")));
    }

    [Theory]
    [InlineData(101, 200)]
    [InlineData(100, 201)]
    public void RejectsForeignRepositoryOrIssue(long repository, long issue)
    {
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.Parse(
            Issue(repository: repository, issue: issue), "issues", "123", Evidence()));
    }

    [Fact]
    public void RejectsActorMismatchEvenWhenLoginMatches()
    {
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.Parse(
            Issue(actor: 43), "issues", "123", Evidence()));
    }

    [Fact]
    public void UnrelatedLabelDoesNotCreateWork()
    {
        Assert.Equal(NotificationKind.Ignore,
            GitHubActionsIngress.Parse(Issue(label: "bug"), "issues", "123", Evidence()).Kind);
    }

    [Theory]
    [InlineData("resume", "", NotificationKind.Resume)]
    [InlineData("pause", "operator hold", NotificationKind.Pause)]
    [InlineData("reactivate", "verified external launch retired", NotificationKind.Reactivate)]
    [InlineData("request-changes", "Fix the edge case", NotificationKind.RequestChanges)]
    public void RecoveryOperationsRemainDistinct(string operation, string reason, NotificationKind expected)
    {
        var result = GitHubActionsIngress.Parse(Manual(operation, reason), "workflow_dispatch", "123", Evidence());
        Assert.Equal(expected, result.Kind);
    }

    [Theory]
    [InlineData("start", "")]
    [InlineData("pause", "")]
    [InlineData("reactivate", "")]
    [InlineData("request-changes", "")]
    public void RecoveryCannotBecomeStartOrClearHoldWithoutReason(string operation, string reason)
    {
        Assert.Throws<InvalidDataException>(() =>
            GitHubActionsIngress.Parse(Manual(operation, reason), "workflow_dispatch", "123", Evidence()));
    }

    [Fact]
    public void RequestedChangesCarryExplicitHumanHandoffAndRevision()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 100, full_name = "owner/repo" },
            sender = new { id = 42, type = "User" },
            inputs = new { issue = "7", operation = "request-changes", reason = "Fix this case",
                handoff = "handoff:7:1:review", round = "2", pull = "11", sha = new string('a', 40) }
        });
        var message = GitHubActionsIngress.PrepareEvent(payload, "workflow_dispatch", "123", Evidence(),
            1, 1, DateTimeOffset.UnixEpoch);
        Assert.Equal(new RequestChanges("handoff:7:1:review", 2, 11, new string('a', 40), "Fix this case"),
            Assert.IsType<RequestChanges>(message!.Data));
        Assert.Equal("User", message.Actor.Type);
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.PrepareEvent(payload,
            "workflow_dispatch", "123", Evidence(permission: "read"), 1, 1, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ReadyNotificationRunsSharedReducerAndReplayCreatesNoSecondTask()
    {
        var context = Context();
        var first = OfflineNotificationReplay.Run(Issue(), "issues", context);
        var replay = OfflineNotificationReplay.Run(Issue(), "issues", context with { State = first.State });
        Assert.Equal(Phase.CODING, replay.State.Issues[7].Phase);
        Assert.Single(replay.State.Issues[7].Tasks);
        Assert.Null(replay.State.Issues[7].Ci);
        Assert.All(replay.State.Outbox.Values, intent => Assert.Equal(DeliveryStatus.Pending, intent.Status));
    }

    [Fact]
    public void ReplayedReadyKeepsImmutableEventBodyWhenLiveReadinessChanges()
    {
        var context = Context();
        var first = OfflineNotificationReplay.Run(Issue(), "issues", context);
        var result = OfflineNotificationReplay.Run(Issue(), "issues", context with
        {
            State = first.State,
            Evidence = Evidence(ready: false),
            Observation = new LiveObservation
            {
                ObservedAt = context.OccurredAt,
                Issues = { [7] = new() { Ready = false } }
            }
        });
        Assert.Single(result.State.Inbox);
        Assert.Single(result.State.Issues[7].Tasks);
        Assert.Equal(Phase.BLOCKED, result.State.Issues[7].Phase);
    }

    [Fact]
    public void ContradictoryEvidenceCannotAuthorizeWork()
    {
        Assert.Throws<InvalidDataException>(() => OfflineNotificationReplay.Run(Issue(), "issues",
            Context() with { Evidence = Evidence(ready: false) }));
    }

    [Fact]
    public void SecondReadyIssueQueuesWithoutBlockingOrDuplicatingAdmission()
    {
        var first = OfflineNotificationReplay.Run(Issue(), "issues", Context());
        var next = Context() with
        {
            State = first.State,
            RunId = "124",
            Evidence = Evidence(number: 8),
            Observation = new LiveObservation
            {
                ObservedAt = Context().OccurredAt,
                Issues = { [7] = new() { Ready = true }, [8] = new() { Ready = true } }
            }
        };
        var result = OfflineNotificationReplay.Run(Issue(number: 8), "issues", next);
        Assert.Equal(7, result.State.AdmittedIssue);
        Assert.Equal(Phase.READY, result.State.Issues[8].Phase);
        Assert.Empty(result.State.Issues[8].Tasks);
    }

    [Theory]
    [InlineData("agent:review")]
    [InlineData("agent:ci")]
    [InlineData("agent:blocked")]
    public void StageTamperingCannotAdvanceCanonicalCoding(string label)
    {
        var first = OfflineNotificationReplay.Run(Issue(), "issues", Context());
        var result = OfflineNotificationReplay.Run(Issue(label: label), "issues",
            Context() with { State = first.State, RunId = "124" });
        Assert.Equal(Phase.CODING, result.State.Issues[7].Phase);
        Assert.Null(result.State.Issues[7].Ci);
        Assert.Null(result.State.Issues[7].Review);
        Assert.Single(result.State.Issues[7].Tasks);
    }

    [Fact]
    public void WithdrawalThenResumeCannotClearHold()
    {
        var first = OfflineNotificationReplay.Run(Issue(), "issues", Context());
        var withdrawnContext = Context() with
        {
            State = first.State, RunId = "124", Evidence = Evidence(ready: false),
            Observation = new LiveObservation
            {
                ObservedAt = Context().OccurredAt,
                Issues = { [7] = new() { Ready = false } }
            }
        };
        var withdrawn = OfflineNotificationReplay.Run(Issue("unlabeled"), "issues", withdrawnContext);
        var resumed = OfflineNotificationReplay.Run(Manual("resume", ""), "workflow_dispatch",
            withdrawnContext with { State = withdrawn.State, RunId = "125" });
        Assert.Equal(Phase.BLOCKED, resumed.State.Issues[7].Phase);
        Assert.False(resumed.State.Issues[7].Ready);
        Assert.Equal(withdrawn.State.Issues[7].HoldReason, resumed.State.Issues[7].HoldReason);
    }

    [Fact]
    public void NotificationBridgeRejectsStaleGeneration()
    {
        Assert.Throws<InvalidOperationException>(() => OfflineNotificationReplay.Run(Issue(), "issues",
            Context() with { RepositoryGeneration = 2 }));
    }

    [Fact]
    public void NotificationBridgePreservesExplicitReactivationReason()
    {
        var message = GitHubActionsIngress.PrepareEvent(Manual("reactivate", "verified retirement"),
            "workflow_dispatch", "123", Evidence(), 1, 1, Context().OccurredAt);
        Assert.NotNull(message);
        Assert.IsType<Reactivate>(message.Data);
        Assert.Contains("verified retirement", JsonSerializer.Serialize(message, StateJson.Options));
    }

    private static NotificationReplayContext Context() => new(
        RepositoryState.Create("owner/repo"),
        new LiveObservation
        {
            ObservedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Issues = { [7] = new() { Ready = true } }
        },
        Evidence(), "123", 1, 1, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static JsonElement Manual(string operation, string reason) =>
        JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 100, full_name = "owner/repo" },
            sender = new { id = 42, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation, reason }
        });
}
