using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public class OrchestrationCommandTests
{
    [Theory]
    [InlineData("--execute")]
    [InlineData("--replay")]
    public void LiveExecutionCannotBeEnabledByCommandLine(string argument)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, OrchestrationCommand.Run([argument], output, error));
        Assert.Contains("no live effects", error.ToString());
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void ReplayPlansReadinessWithoutMutatingInputOrPersistingState()
    {
        var state = new RepositoryState { Repository = "owner/repo" };
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var message = new OrchestrationEvent("ready:1", "owner/repo", 7, 1, 1,
            new Actor("maintainer", ActorKind.Human), now, new Ready());
        var fixture = new ReplayFixture(state, [message],
            new LiveObservation
            {
                ObservedAt = now,
                Issues = new()
                {
                    [7] = new LiveIssueObservation { Ready = true }
                }
            }, ["maintainer"]);
        var decision = OfflineReplay.Run(fixture);
        Assert.Empty(state.Issues);
        Assert.Contains(decision.PendingEffects, effect => effect.Kind == EffectKind.LaunchTask);
        Assert.Equal(Phase.CODING, decision.State.Issues[7].Phase);
    }

    [Fact]
    public void CommandReplayExecutesNoExternalEffects()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new ReplayFixture(
                new RepositoryState { Repository = "owner/repo" }, [],
                new LiveObservation(), []), StateJson.Options));
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, OrchestrationCommand.Run(["--dry-run", "--replay", path], output, error));
            Assert.Contains("\"ExternalEffectsExecuted\": 0", output.ToString());
            Assert.Contains("\"AcceptedDurably\": false", output.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NotificationPreviewDoesNotPretendToAcceptOrAuthorizeWork()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"repository":{"full_name":"owner/repo"}}""");
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, OrchestrationCommand.Run(
                ["--dry-run", "--notification", path, "--event-name", "issues"], output, error));
            Assert.Contains("\"AcceptedDurably\": false", output.ToString());
            Assert.Contains("Not activated", output.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MalformedInputDoesNotEchoPayloadInDiagnostics()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "untrusted-sensitive-content");
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, OrchestrationCommand.Run(["--dry-run", "--replay", path], output, error));
            Assert.DoesNotContain("untrusted-sensitive-content", error.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
