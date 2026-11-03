using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public class OrchestrationCommandTests
{
    [Theory]
    [InlineData("--execute")]
    [InlineData("--context")]
    [InlineData("--replay")]
    public async Task GitHubReadOnlyModeRejectsMutationAndOfflineEvidenceOverridesBeforeAnyIo(string argument)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await OrchestrationCommand.RunAsync(
            ["--dry-run", "--github-read-only", argument], output, error));
        Assert.Contains("no live effects", error.ToString());
        Assert.Equal("", output.ToString());
    }

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
    public void NotificationWithOfflineContextUsesValidatedIngressAndSharedReducer()
    {
        var path = Path.GetTempFileName();
        var contextPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {"repository":{"id":100,"full_name":"owner/repo"},
                 "sender":{"id":42,"type":"User"},
                 "action":"labeled","issue":{"id":200,"number":7},
                 "label":{"name":"agent:ready"}}
                """);
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var context = new NotificationReplayContext(
                RepositoryState.Create("owner/repo"),
                new LiveObservation
                {
                    ObservedAt = now,
                    Issues = { [7] = new() { Ready = true } }
                },
                new IngressEvidence("owner/repo", 100, 42, "User", "write", 200, 7,
                    true, true, false, []),
                "123", 1, 1, now);
            File.WriteAllText(contextPath, JsonSerializer.Serialize(context, StateJson.Options));
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, OrchestrationCommand.Run(
                ["--dry-run", "--notification", path, "--event-name", "issues", "--context", contextPath],
                output, error));
            using var result = JsonDocument.Parse(output.ToString());
            Assert.False(result.RootElement.GetProperty("AcceptedDurably").GetBoolean());
            Assert.Equal(0, result.RootElement.GetProperty("ExternalEffectsExecuted").GetInt32());
            Assert.Equal("CODING",
                result.RootElement.GetProperty("State").GetProperty("Issues").GetProperty("7")
                    .GetProperty("Phase").GetString());
            Assert.Equal("", error.ToString());
        }
        finally
        {
            File.Delete(path);
            File.Delete(contextPath);
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
