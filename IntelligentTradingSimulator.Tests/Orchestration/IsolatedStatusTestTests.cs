using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class IsolatedStatusTestTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
    private static readonly string Definition = new('a', 40);
    private static readonly string CiDefinition = Definition;
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
        Assert.Equal("refs/tags/agent-status-revocation-definition-v2",
            GitHubIsolatedStatusTestSettings.DriverReference);
        Assert.Equal(GitHubIsolatedStatusTestSettings.DriverReference,
            GitHubIsolatedStatusTestSettings.CiReference);

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
    public void SharedDefinitionTagRequiresMatchingDriverAndCiShas()
    {
        var values = ValidSettings();
        values["ORCHESTRATION_STATUS_TEST_CI_DEFINITION_SHA"] = new string('b', 40);
        Assert.Throws<InvalidDataException>(() =>
            GitHubIsolatedStatusTestSettings.Read(values.GetValueOrDefault, Now));
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

    [Theory]
    [InlineData("manifest", false, "InvalidDataException")]
    [InlineData("state-timeout", false, "TaskCanceledException")]
    [InlineData("identity-timeout", true, "TaskCanceledException")]
    [InlineData("diagnostic-timeout", true, "TaskCanceledException")]
    [InlineData("authorization-expiry", false, "TaskCanceledException")]
    [InlineData("moved-tag", true, "InvalidDataException")]
    public async Task CommandKeepsClientsAliveForSanitizedFailureDiagnosticsAndHandlesTimeouts(
        string failure, bool stateUnavailable, string errorType)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """
                {"inputs":{"issue":"51","operation":"status-test-start"}}
                """);
            var values = ValidSettings();
            values["ORCHESTRATION_STATUS_TEST_EXPIRES_AT"] =
                (failure == "authorization-expiry" ? DateTimeOffset.UtcNow.AddSeconds(2) :
                    DateTimeOffset.UtcNow.AddHours(2)).ToString("O");
            values["GITHUB_EVENT_PATH"] = path;
            values["GITHUB_EVENT_NAME"] = "workflow_dispatch";
            values["GITHUB_RUN_ID"] = "99";
            values["GITHUB_REF"] = GitHubIsolatedStatusTestSettings.DriverReference;
            values["GITHUB_WORKFLOW_REF"] =
                $"owner/repo/{GitHubIsolatedStatusTestSettings.DriverWorkflowPath}@{GitHubIsolatedStatusTestSettings.DriverReference}";
            values["GITHUB_TOKEN"] = "workflow-test-token";
            values["ORCHESTRATION_STATUS_TEST_USER_TOKEN"] = "user-test-token";
            var handler = new CommandHandler(failure);
            var userHandler = new CommandHandler(failure);
            var clients = new List<HttpClient>();
            using var output = new StringWriter();
            using var error = new StringWriter();

            var result = await GitHubIsolatedStatusTestCommand.RunAsync(["--execute"], output, error,
                name => values.GetValueOrDefault(name), token =>
                {
                    var client = new HttpClient(token == "workflow-test-token" ? handler : userHandler)
                    {
                        BaseAddress = new("https://api.github.com/")
                    };
                    clients.Add(client);
                    return client;
                });

            Assert.Equal(1, result);
            using var evidence = JsonDocument.Parse(output.ToString());
            var root = evidence.RootElement;
            Assert.Equal("isolated-status-test-failure", root.GetProperty("Mode").GetString());
            Assert.Equal(errorType, root.GetProperty("ErrorType").GetString());
            if (stateUnavailable)
                Assert.True(root.GetProperty("StateUnavailable").GetBoolean());
            else
            {
                Assert.False(root.TryGetProperty("StateUnavailable", out _));
                Assert.Equal(new string('c', 40), root.GetProperty("HeadSha").GetString());
                Assert.Equal("remote-task", root.GetProperty("TaskHistory")[0].GetProperty("ExternalId").GetString());
                Assert.Equal("90", root.GetProperty("Ci").GetProperty("RunId").GetString());
                Assert.Equal(100, root.GetProperty("StatusHistory")[0].GetProperty("Boundary").GetProperty("StatusId").GetInt64());
                Assert.Equal(2, handler.RefReads);
            }
            Assert.DoesNotContain("workflow-test-token", output.ToString() + error);
            Assert.DoesNotContain("user-test-token", output.ToString() + error);
            Assert.Contains("reconcile saved test state", error.ToString());
            Assert.True(handler.Disposed);
            Assert.True(userHandler.Disposed);
            foreach (var client in clients)
                await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("user"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CommandHandler(string failure) : HttpMessageHandler
    {
        public int RefReads { get; private set; }
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.False(Disposed);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path == "/user" && failure == "identity-timeout")
                throw new TaskCanceledException("user-test-token must not appear in diagnostics");
            if (path.Contains("/git/ref/", StringComparison.Ordinal))
            {
                RefReads++;
                if (failure == "authorization-expiry" && RefReads == 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                if (failure == "diagnostic-timeout" || failure == "state-timeout" && RefReads == 1)
                    throw new TaskCanceledException("workflow-test-token must not appear in diagnostics");
            }
            Assert.True(request.Method == HttpMethod.Get || path == "/graphql",
                "Failure diagnostics must not retry mutations.");
            object body = path switch
            {
                "/user" => new { id = 55, login = "maintainer", type = "User" },
                "/graphql" => new { data = new { viewer = new
                {
                    databaseId = GitHubLiveCredentials.ProjectionActorId,
                    login = GitHubLiveCredentials.ProjectionLogin, id = "bot-node"
                } } },
                "/users/github-actions[bot]" => new
                {
                    id = GitHubLiveCredentials.ProjectionActorId,
                    login = GitHubLiveCredentials.ProjectionLogin, type = "Bot", node_id = "bot-node"
                },
                "/repos/owner/repo" => new { id = 1, full_name = "owner/repo" },
                "/repos/owner/repo/actions/runs/99" => new
                {
                    id = 99, workflow_id = 101, path = GitHubIsolatedStatusTestSettings.DriverWorkflowPath,
                    @event = "workflow_dispatch", head_sha = Definition, repository = new { id = 1 },
                    created_at = Now
                },
                var p when p.StartsWith("/repos/owner/repo/commits/", StringComparison.Ordinal) =>
                    new { sha = failure == "moved-tag" ? new string('b', 40) : Definition },
                "/repos/owner/repo/git/ref/heads/agent-state-status-revocation-v1" =>
                    new { @object = new { sha = new string('d', 40) } },
                var p when p.Contains("/git/commits/", StringComparison.Ordinal) =>
                    new { tree = new { sha = new string('e', 40) } },
                var p when p.Contains("/git/trees/", StringComparison.Ordinal) =>
                    new { tree = new[] { new { path = "state.json", type = "blob", mode = "100644", sha = new string('f', 40) } } },
                var p when p.Contains("/git/blobs/", StringComparison.Ordinal) => StateBlob(),
                _ => throw new InvalidOperationException("Unexpected HTTP " + path)
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body))
            };
        }

        private static object StateBlob()
        {
            var state = State();
            var issue = state.Issues[51];
            issue.HeadSha = new('c', 40);
            issue.Tasks["task"] = new() { Id = "task", ExternalId = "remote-task" };
            issue.Ci = new() { RunId = "90" };
            state.Outbox["status"] = new()
            {
                Id = "status", IssueNumber = 51, Kind = EffectKind.PublishRequiredStatus,
                RequiredStatusBoundary = new(41898282, "github-actions[bot]", "bot-node", "Bot", Now, [], 100)
            };
            var bytes = Encoding.UTF8.GetBytes(StateJson.Serialize(state));
            return new { encoding = "base64", size = bytes.Length, content = Convert.ToBase64String(bytes) };
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
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
