using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public class GitHubActionsIngressTests
{
    private static IngressEvidence Evidence(
        bool ready = true, bool open = true, string permission = "write",
        long actor = 42, string actorType = "User") =>
        new("owner/repo", 100, actor, actorType, permission, 200, 7, open, ready, false,
            new HashSet<long> { 999 });

    private static JsonElement Issue(
        string action = "labeled", string label = "agent:ready",
        long actor = 42, string actorType = "User", long repository = 100, long issue = 200) =>
        JsonSerializer.SerializeToElement(new
        {
            action,
            repository = new { id = repository, full_name = "owner/repo" },
            sender = new { id = actor, login = "untrusted-display-name", type = actorType },
            issue = new { id = issue, number = 7, state = "open" },
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
    public void RecoveryOperationsRemainDistinct(string operation, string reason, NotificationKind expected)
    {
        var result = GitHubActionsIngress.Parse(Manual(operation, reason), "workflow_dispatch", "123", Evidence());
        Assert.Equal(expected, result.Kind);
    }

    [Theory]
    [InlineData("start", "")]
    [InlineData("pause", "")]
    [InlineData("reactivate", "")]
    public void RecoveryCannotBecomeStartOrClearHoldWithoutReason(string operation, string reason)
    {
        Assert.Throws<InvalidDataException>(() =>
            GitHubActionsIngress.Parse(Manual(operation, reason), "workflow_dispatch", "123", Evidence()));
    }

    private static JsonElement Manual(string operation, string reason) =>
        JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 100, full_name = "owner/repo" },
            sender = new { id = 42, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation, reason }
        });
}
