using System.Net;
using System.Globalization;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class IsolatedStatusTestTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
    private static readonly string Definition = new('a', 40);
    private static readonly string CiDefinition = new('b', 40);
    private const string Manifest = "manifest-digest";

    [Fact]
    public void SettingsPinDistinctResourcesAndAllowMissingCiApprovalForPauseOnly()
    {
        var values = ValidSettings();
        values["ORCHESTRATION_STATUS_TEST_CI_VERIFIED"] = "false";
        var settings = GitHubIsolatedStatusTestSettings.Read(name => values.GetValueOrDefault(name), Now);

        Assert.False(settings.Ci.AllowsCi);
        Assert.True(settings.Status.AllowsPublication);
        Assert.NotEqual(GitHubIsolatedStatusTestSettings.StateReference, "refs/heads/agent-state");
        Assert.Equal(GitHubIsolatedStatusTestSettings.CiWorkflowPath, settings.CiRoute.WorkflowPath);

        values["ORCHESTRATION_STATUS_TEST_ISSUE"] = "237";
        Assert.Throws<InvalidDataException>(() =>
            GitHubIsolatedStatusTestSettings.Read(name => values.GetValueOrDefault(name), Now));
    }

    [Theory]
    [InlineData("ORCHESTRATION_STATUS_TEST_STATE_REF", "refs/heads/agent-state")]
    [InlineData("ORCHESTRATION_STATUS_TEST_BASE_BRANCH", "master")]
    [InlineData("ORCHESTRATION_STATUS_TEST_CI_REF", "refs/tags/agent-managed-ci-test-237-v1")]
    [InlineData("ORCHESTRATION_STATUS_TEST_DRIVER_REF", "refs/heads/master")]
    [InlineData("ORCHESTRATION_STATUS_TEST_ENVIRONMENT", "production")]
    public void SettingsRejectProductionResources(string name, string value)
    {
        var values = ValidSettings();
        values[name] = value;
        Assert.Throws<InvalidDataException>(() =>
            GitHubIsolatedStatusTestSettings.Read(key => values.GetValueOrDefault(key), Now));
    }

    [Fact]
    public void SettingsRejectExpiredOrOverlongAuthorization()
    {
        var values = ValidSettings();
        values["ORCHESTRATION_STATUS_TEST_EXPIRES_AT"] = Now.ToString("O");
        Assert.Throws<InvalidDataException>(() =>
            GitHubIsolatedStatusTestSettings.Read(key => values.GetValueOrDefault(key), Now));
        values["ORCHESTRATION_STATUS_TEST_EXPIRES_AT"] = Now.AddHours(73).ToString("O");
        Assert.Throws<InvalidDataException>(() =>
            GitHubIsolatedStatusTestSettings.Read(key => values.GetValueOrDefault(key), Now));
    }

    [Fact]
    public async Task StateStoreRejectsCrossScopeManifestExtraEffectsRepairsAndMergeHistory()
    {
        var inner = new Store(State());
        var guarded = new IsolatedStatusTestStateStore(inner, "owner/repo", 51, Manifest);
        var snapshot = await guarded.LoadAsync(default);
        var outOfScope = State();
        outOfScope.Issues[52] = new() { Number = 52 };
        await Assert.ThrowsAsync<InvalidDataException>(() => guarded.TrySaveAsync(snapshot, outOfScope, default));
        var wrongManifest = State();
        wrongManifest.IsolatedTestManifestDigest = "altered";
        await Assert.ThrowsAsync<InvalidDataException>(() => guarded.TrySaveAsync(snapshot, wrongManifest, default));
        var repair = State();
        repair.Issues[51].Tasks["repair"] = new() { Id = "repair", Kind = TaskKind.CiRepair };
        await Assert.ThrowsAsync<InvalidDataException>(() => guarded.TrySaveAsync(snapshot, repair, default));
        var review = State();
        review.Outbox["review"] = new() { Id = "review", IssueNumber = 51, Kind = EffectKind.RequestReview };
        await Assert.ThrowsAsync<InvalidDataException>(() => guarded.TrySaveAsync(snapshot, review, default));
        var merged = State();
        merged.Issues[51].TerminalOutcome = TerminalOutcome.Merged;
        await Assert.ThrowsAsync<InvalidDataException>(() => guarded.TrySaveAsync(snapshot, merged, default));
        Assert.Equal(0, inner.Saves);
    }

    [Fact]
    public async Task StateStoreAllowsOnlyOneTaskOneCiAndSuccessThenCorrectiveError()
    {
        var state = State();
        state.Issues[51].Tasks["task"] = new() { Id = "task", Kind = TaskKind.Coding, Round = 1 };
        state.Outbox["launch"] = new() { Id = "launch", IssueNumber = 51, Kind = EffectKind.LaunchTask };
        state.Outbox["ci"] = new() { Id = "ci", IssueNumber = 51, Kind = EffectKind.RunCi };
        state.Outbox["required-status:51:1:0"] = new()
        {
            Id = "required-status:51:1:0", IssueNumber = 51, Kind = EffectKind.PublishRequiredStatus,
            Status = DeliveryStatus.Cancelled, Sha = new('c', 40),
            RequiredStatus = new(RequiredStatusPolicy.Context, "pending", "not ready",
                "https://github.com/owner/repo/pull/9", null, null)
        };
        var success = new EffectIntent
        {
            Id = "required-status:51:1:1", IssueNumber = 51, Kind = EffectKind.PublishRequiredStatus,
            Status = DeliveryStatus.Succeeded, Sha = new('c', 40),
            RequiredStatus = new(RequiredStatusPolicy.Context, "success", "green",
                "https://github.com/owner/repo/pull/9", null, null)
        };
        state.Outbox[success.Id] = success;
        var inner = new Store(state);
        var guarded = new IsolatedStatusTestStateStore(inner, "owner/repo", 51, Manifest);
        var snapshot = await guarded.LoadAsync(default);
        var error = new EffectIntent
        {
            Id = "required-status:51:1:2", IssueNumber = 51, Kind = EffectKind.PublishRequiredStatus,
            Status = DeliveryStatus.Pending, Sha = success.Sha,
            RequiredStatus = new(RequiredStatusPolicy.Context, "error", "paused",
                "https://github.com/owner/repo/pull/9", null, null)
        };
        var corrective = StateJson.Deserialize(StateJson.Serialize(state));
        corrective.Outbox[error.Id] = error;
        corrective.Issues[51].RequiredStatusIntentId = error.Id;
        Assert.True(await guarded.TrySaveAsync(snapshot, corrective, default));

        var duplicateCi = StateJson.Deserialize(StateJson.Serialize(corrective));
        duplicateCi.Outbox["ci-2"] = new() { Id = "ci-2", IssueNumber = 51, Kind = EffectKind.RunCi };
        var next = await guarded.LoadAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => guarded.TrySaveAsync(next, duplicateCi, default));
    }

    [Fact]
    public void IsolatedStartIngressRequiresExplicitReadyHumanCommand()
    {
        var payload = JsonDocument.Parse("""
            {"repository":{"id":1,"full_name":"owner/repo"},
             "sender":{"id":55,"login":"maintainer","type":"User"},
             "inputs":{"issue":"51","operation":"status-test-start"}}
            """).RootElement;
        var evidence = new IngressEvidence("owner/repo", 1, 55, "User", "write", 500, 51, true,
            true, false, []);
        var message = GitHubActionsIngress.PrepareEvent(payload, "workflow_dispatch", "99", evidence,
            1, 1, Now);
        Assert.IsType<Ready>(message!.Data);
        Assert.True(((Ready)message.Data).SinglePassTest);
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.PrepareEvent(payload,
            "workflow_dispatch", "99", evidence with { Ready = false }, 1, 1, Now));
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.PrepareEvent(payload,
            "workflow_dispatch", "99", evidence with { ActorType = "Bot" }, 1, 1, Now));
    }

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["GITHUB_REPOSITORY"] = "owner/repo",
        ["GITHUB_REPOSITORY_ID"] = "1",
        ["ORCHESTRATION_STATUS_TEST_ACTOR_ID"] = "55",
        ["ORCHESTRATION_STATUS_TEST_ISSUE"] = "51",
        ["ORCHESTRATION_STATUS_TEST_WORKFLOW_ID"] = "101",
        ["ORCHESTRATION_STATUS_TEST_CI_WORKFLOW_ID"] = "102",
        ["ORCHESTRATION_STATUS_TEST_DEFINITION_SHA"] = Definition,
        ["ORCHESTRATION_STATUS_TEST_CI_DEFINITION_SHA"] = CiDefinition,
        ["ORCHESTRATION_STATUS_TEST_STATE_REF"] = GitHubIsolatedStatusTestSettings.StateReference,
        ["ORCHESTRATION_STATUS_TEST_BASE_BRANCH"] = GitHubIsolatedStatusTestSettings.BaseBranch,
        ["ORCHESTRATION_STATUS_TEST_CI_REF"] = GitHubIsolatedStatusTestSettings.CiReference,
        ["ORCHESTRATION_STATUS_TEST_DRIVER_REF"] = GitHubIsolatedStatusTestSettings.DriverReference,
        ["ORCHESTRATION_STATUS_TEST_ENVIRONMENT"] = GitHubIsolatedStatusTestSettings.EnvironmentName,
        ["ORCHESTRATION_STATUS_TEST_EXPIRES_AT"] = Now.AddHours(2).ToString("O"),
        ["ORCHESTRATION_STATUS_TEST_APPROVED"] = "true",
        ["ORCHESTRATION_STATUS_TEST_BOOTSTRAP_APPROVED"] = "false",
        ["ORCHESTRATION_STATUS_TEST_CREDENTIALS_VERIFIED"] = "true",
        ["ORCHESTRATION_STATUS_TEST_TASK_ENTITLEMENT_VERIFIED"] = "true",
        ["ORCHESTRATION_STATUS_TEST_CI_VERIFIED"] = "true",
        ["ORCHESTRATION_STATUS_TEST_STATUS_PUBLISHER_VERIFIED"] = "true",
        ["ORCHESTRATION_STATUS_TEST_CONTEXT_PROTECTION_VERIFIED"] = "true",
        ["ORCHESTRATION_STATUS_TEST_NO_ADDED_CHARGES_VERIFIED"] = "true"
    };

    private static RepositoryState State()
    {
        var state = RepositoryState.Create("owner/repo");
        state.IsolatedTestManifestDigest = Manifest;
        state.Issues[51] = new() { Number = 51, SinglePassTest = true };
        return state;
    }

    private sealed class Store(RepositoryState state) : IStateStore
    {
        private RepositoryState value = state;
        public int Saves { get; private set; }
        public Task<StateSnapshot> LoadAsync(CancellationToken token) =>
            Task.FromResult(new StateSnapshot(value.Version.ToString(CultureInfo.InvariantCulture),
                StateJson.Deserialize(StateJson.Serialize(value))));
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken token)
        {
            if (expected.Revision != value.Version.ToString(CultureInfo.InvariantCulture))
                return Task.FromResult(false);
            value = StateJson.Deserialize(StateJson.Serialize(next));
            value.Version++;
            Saves++;
            return Task.FromResult(true);
        }
    }
}
