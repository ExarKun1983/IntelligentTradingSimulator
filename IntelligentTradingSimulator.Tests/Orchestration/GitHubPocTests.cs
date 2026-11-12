using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubPocTests
{
    private const string Repository = "ExarKun1983/IntelligentTradingSimulator";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
    private static readonly string Definition = new('a', 40);

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("CREDENTIALS_VERIFIED")]
    [InlineData("TASK_ENTITLEMENT_VERIFIED")]
    [InlineData("TRUSTED_DEFINITION_VERIFIED")]
    [InlineData("NO_ADDED_CHARGES_VERIFIED")]
    [InlineData("REVIEW_POLICY_VERIFIED")]
    [InlineData("AUTOMATIC_REVIEWS_DISABLED")]
    public void MissingApprovalRejectsExecution(string name)
    {
        var values = Settings();
        values.Remove("ORCHESTRATION_POC_" + name);
        Assert.Throws<InvalidOperationException>(() => GitHubPocSettings.Read(values.GetValueOrDefault, Now));
    }

    [Theory]
    [InlineData("GITHUB_REPOSITORY", "other/repository")]
    [InlineData("ORCHESTRATION_POC_CI_WORKFLOW_ID", "10")]
    [InlineData("ORCHESTRATION_POC_DEFINITION_SHA", "master")]
    [InlineData("ORCHESTRATION_POC_EXPIRES_AT", "2026-11-12T12:00:00Z")]
    [InlineData("ORCHESTRATION_POC_EXPIRES_AT", "2026-11-16T12:00:00Z")]
    public void InvalidIdentityOrExpiryRejectsProfile(string name, string value)
    {
        var values = Settings();
        values[name] = value;
        Assert.Throws<InvalidDataException>(() => GitHubPocSettings.Read(values.GetValueOrDefault, Now));
    }

    [Fact]
    public void ChangedDefinitionOrExpiryCannotReuseBootstrappedManifest()
    {
        var values = Settings();
        var original = GitHubPocSettings.Read(values.GetValueOrDefault, Now);
        values["ORCHESTRATION_POC_DEFINITION_SHA"] = new string('b', 40);
        Assert.NotEqual(original.ManifestDigest, GitHubPocSettings.Read(values.GetValueOrDefault, Now).ManifestDigest);
        values["ORCHESTRATION_POC_DEFINITION_SHA"] = Definition;
        values["ORCHESTRATION_POC_EXPIRES_AT"] = Now.AddHours(3).ToString("O");
        Assert.NotEqual(original.ManifestDigest, GitHubPocSettings.Read(values.GetValueOrDefault, Now).ManifestDigest);
    }

    [Theory]
    [InlineData("foreign-issue")]
    [InlineData("manifest")]
    [InlineData("single-pass")]
    [InlineData("generation")]
    [InlineData("extra-coding")]
    [InlineData("extra-repair")]
    [InlineData("ci-repair")]
    [InlineData("feedback-repair")]
    [InlineData("extra-ci")]
    [InlineData("extra-review")]
    [InlineData("status")]
    [InlineData("merged")]
    public async Task StateGuardRejectsScopeDriftAndExtraEffectsBeforeSaving(string corruption)
    {
        var state = State("manifest");
        var inner = new GitHubTaskIntegrationTests.Store(state);
        var guarded = new PocStateStore(inner, Repository, 249, "manifest");
        var snapshot = await guarded.LoadAsync(default);
        var next = StateJson.Clone(snapshot.State);
        var issue = next.Issues[249];
        switch (corruption)
        {
            case "foreign-issue": next.Issues[237] = new() { Number = 237 }; break;
            case "manifest": next.IsolatedTestManifestDigest = "other"; break;
            case "single-pass": issue.SinglePassTest = true; break;
            case "generation": issue.Generation++; break;
            case "extra-coding":
            case "extra-repair":
                var kind = corruption == "extra-coding" ? TaskKind.Coding : TaskKind.ReviewRepair;
                issue.Tasks["one"] = new() { Id = "one", Kind = kind };
                issue.Tasks["two"] = new() { Id = "two", Kind = kind };
                break;
            case "ci-repair":
            case "feedback-repair":
                issue.Tasks["repair"] = new() { Id = "repair",
                    Kind = corruption == "ci-repair" ? TaskKind.CiRepair : TaskKind.FeedbackRepair };
                break;
            case "extra-ci":
            case "extra-review":
                for (var index = 0; index < 3; index++)
                    next.Outbox[index.ToString()] = new() { IssueNumber = 249,
                        Kind = corruption == "extra-ci" ? EffectKind.RunCi : EffectKind.RequestReview };
                break;
            case "status":
                next.Outbox["status"] = new() { IssueNumber = 249, Kind = EffectKind.PublishRequiredStatus };
                break;
            case "merged": issue.TerminalHistory.Add(new(1, TerminalOutcome.Merged, 17, Definition, Now)); break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => guarded.TrySaveAsync(snapshot, next, default));
        Assert.Equal(StateJson.Serialize(snapshot.State), StateJson.Serialize(inner.State));
    }

    [Fact]
    public void ManualStartRequiresLiveReadyAndPreservesOneRepairRatherThanSinglePass()
    {
        var evidence = new IngressEvidence(Repository, 1, 55, "User", "write", 2490, 249, true, true, false, []);
        var payload = Notification("poc-start");
        var message = GitHubActionsIngress.PrepareEvent(payload, "workflow_dispatch", "44", evidence, 1, 1, Now, 1);
        var ready = Assert.IsType<Ready>(message!.Data);
        Assert.Equal(1, ready.MaxRepairTasks);
        Assert.NotEqual(true, ready.SinglePassTest);
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.PrepareEvent(payload,
            "workflow_dispatch", "44", evidence with { Ready = false }, 1, 1, Now, 1));
        Assert.Throws<InvalidDataException>(() => GitHubActionsIngress.PrepareEvent(payload,
            "workflow_dispatch", "44", evidence with { Permission = "read" }, 1, 1, Now, 1));
    }

    [Fact]
    public async Task PocStartCannotBeSentThroughTheMainHostProfile()
    {
        using var api = new GitHubTaskIntegrationTests.ApiFixture();
        using var client = api.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store());
        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReconcileAsync(Notification("poc-start"),
            "workflow_dispatch", "44", 1, 1, Now, [], 99, "master", 55,
            new(true, true, true, true), maxRepairTasks: 1));
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("rerun")]
    [InlineData("triggering-actor")]
    [InlineData("moved-tag")]
    [InlineData("moved-base")]
    [InlineData("missing-state")]
    [InlineData("manifest")]
    [InlineData("second-start")]
    [InlineData("foreign-issue")]
    [InlineData("foreign-actor")]
    [InlineData("confirmation")]
    [InlineData("reactivation")]
    [InlineData("bootstrap-not-approved")]
    public async Task CommandRejectsUnsafeInvocationWithoutAnyMutation(string corruption)
    {
        await WithCommand("poc-start", corruption, async (values, api, output, error, result) =>
        {
            Assert.Equal(1, result);
            Assert.Equal(0, api.Mutations);
            Assert.Contains("github-poc-failure", output.ToString());
            Assert.Contains("inspect canonical state", error.ToString());
            Assert.DoesNotContain("private-user-token", output.ToString() + error);
            Assert.DoesNotContain("private-workflow-token", output.ToString() + error);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task BootstrapCreatesOnlyItsOwnStateAndCannotRepeat()
    {
        await WithCommand("poc-bootstrap", "", async (values, api, output, error, result) =>
        {
            Assert.Equal(0, result);
            Assert.Equal(4, api.Mutations);
            Assert.True(api.Exists);
            Assert.Equal(GitHubPocSettings.Read(values.GetValueOrDefault, DateTimeOffset.UtcNow).ManifestDigest,
                api.StoredState!.IsolatedTestManifestDigest);
            Assert.Empty(api.StoredState.Issues);
            Assert.Empty(error.ToString());
            using var receipt = JsonDocument.Parse(output.ToString());
            Assert.Equal("poc-bootstrap", receipt.RootElement.GetProperty("Operation").GetString());
            Assert.False(receipt.RootElement.GetProperty("Completed").GetBoolean());
            var before = api.Mutations;
            using var replayOutput = new StringWriter();
            using var replayError = new StringWriter();
            var replay = await Run(values, api, replayOutput, replayError);
            Assert.Equal(1, replay);
            Assert.Equal(before, api.Mutations);
        });
    }

    private static async Task WithCommand(string operation, string corruption,
        Func<Dictionary<string, string>, CommandApi, StringWriter, StringWriter, int, Task> assert)
    {
        var path = Path.GetTempFileName();
        try
        {
            if (corruption == "reactivation") operation = "reactivate";
            if (corruption == "bootstrap-not-approved") operation = "poc-bootstrap";
            await File.WriteAllTextAsync(path, Notification(operation,
                issue: corruption == "foreign-issue" ? "237" : "249",
                actor: corruption == "foreign-actor" ? 66 : 55,
                confirmation: corruption == "confirmation" ? "wrong" : GitHubPocCommand.Confirmation).GetRawText());
            var values = Settings();
            if (corruption == "bootstrap-not-approved") values["ORCHESTRATION_POC_BOOTSTRAP_APPROVED"] = "false";
            values["ORCHESTRATION_POC_EXPIRES_AT"] = DateTimeOffset.UtcNow.AddHours(2).ToString("O");
            values["GITHUB_EVENT_PATH"] = path;
            values["GITHUB_EVENT_NAME"] = "workflow_dispatch";
            values["GITHUB_RUN_ID"] = "44";
            values["GITHUB_REF"] = GitHubPocSettings.DefinitionReference;
            values["GITHUB_WORKFLOW_REF"] =
                $"{Repository}/{GitHubPocSettings.DriverWorkflowPath}@{GitHubPocSettings.DefinitionReference}";
            values["GITHUB_TOKEN"] = "private-workflow-token";
            values["ORCHESTRATION_USER_TOKEN"] = "private-user-token";
            var manifest = GitHubPocSettings.Read(values.GetValueOrDefault, DateTimeOffset.UtcNow).ManifestDigest;
            using var api = new CommandApi(corruption, manifest, operation != "poc-bootstrap");
            using var output = new StringWriter();
            using var error = new StringWriter();
            await assert(values, api, output, error, await Run(values, api, output, error));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Task<int> Run(Dictionary<string, string> values, CommandApi api, TextWriter output, TextWriter error) =>
        GitHubPocCommand.RunAsync(["--execute"], output, error, values.GetValueOrDefault, credential =>
        {
            var client = new HttpClient(api, false) { BaseAddress = new("https://api.github.com/") };
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            return client;
        });

    private static JsonElement Notification(string operation, string issue = "249", long actor = 55,
        string confirmation = GitHubPocCommand.Confirmation) => JsonSerializer.SerializeToElement(new
    {
        repository = new { id = 1, full_name = Repository },
        sender = new { id = actor, login = "maintainer", type = "User" },
        inputs = new { issue, operation, confirmation,
            repository_generation = "1", issue_generation = "1" }
    });

    private static RepositoryState State(string manifest)
    {
        var state = RepositoryState.Create(Repository);
        state.IsolatedTestManifestDigest = manifest;
        state.Issues[249] = new() { Number = 249, MaxRepairTasks = 1 };
        return state;
    }

    private static Dictionary<string, string> Settings()
    {
        var values = new Dictionary<string, string>
        {
            ["GITHUB_REPOSITORY"] = Repository, ["GITHUB_REPOSITORY_ID"] = "1",
            ["ORCHESTRATION_POC_ACTOR_ID"] = "55", ["ORCHESTRATION_POC_WORKFLOW_ID"] = "10",
            ["ORCHESTRATION_POC_CI_WORKFLOW_ID"] = "88", ["ORCHESTRATION_POC_DEFINITION_SHA"] = Definition,
            ["ORCHESTRATION_POC_EXPIRES_AT"] = Now.AddHours(2).ToString("O", CultureInfo.InvariantCulture)
        };
        foreach (var approval in new[] { "APPROVED", "BOOTSTRAP_APPROVED", "CREDENTIALS_VERIFIED",
            "TASK_ENTITLEMENT_VERIFIED", "TRUSTED_DEFINITION_VERIFIED", "NO_ADDED_CHARGES_VERIFIED",
            "REVIEW_POLICY_VERIFIED", "AUTOMATIC_REVIEWS_DISABLED" })
            values["ORCHESTRATION_POC_" + approval] = "true";
        return values;
    }

    private sealed class CommandApi(string corruption, string manifest, bool exists) : HttpMessageHandler
    {
        private static readonly string Tip = new('b', 40);
        private static readonly string Tree = new('c', 40);
        private static readonly string Blob = new('d', 40);
        public bool Exists { get; private set; } = exists && corruption != "missing-state";
        public int Mutations { get; private set; }
        public RepositoryState? StoredState { get; private set; }
        private RepositoryState CurrentState
        {
            get
            {
                if (StoredState is not null) return StoredState;
                var state = corruption == "second-start" ? State(manifest) : RepositoryState.Create(Repository);
                state.IsolatedTestManifestDigest = corruption == "manifest" ? "wrong" : manifest;
                return state;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            const string prefix = "/repos/" + Repository;
            object response;
            if (path == "/user")
            {
                Assert.Equal("private-user-token", request.Headers.Authorization?.Parameter);
                response = new { id = 55, login = "maintainer", type = "User" };
            }
            else if (path == "/graphql")
                response = new { data = new { viewer = new { login = GitHubLiveCredentials.ProjectionLogin,
                    databaseId = GitHubLiveCredentials.ProjectionActorId, id = "bot-node" } } };
            else if (path == "/users/github-actions[bot]")
                response = new { id = GitHubLiveCredentials.ProjectionActorId,
                    login = GitHubLiveCredentials.ProjectionLogin, type = "Bot", node_id = "bot-node" };
            else if (path == prefix) response = new { id = 1, full_name = Repository };
            else if (path == prefix + "/actions/runs/44")
                response = new { id = 44, workflow_id = 10, path = GitHubPocSettings.DriverWorkflowPath,
                    head_sha = Definition, @event = "workflow_dispatch", repository = new { id = 1 },
                    run_attempt = corruption == "rerun" ? 2 : 1, actor = new { id = 55 },
                    triggering_actor = new { id = corruption == "triggering-actor" ? 66 : 55 }, created_at = Now };
            else if (path == prefix + "/commits/" + GitHubPocSettings.DefinitionReference)
                response = new { sha = corruption == "moved-tag" ? Tip : Definition };
            else if (path == prefix + "/branches/" + GitHubPocSettings.BaseBranch)
                response = new { commit = new { sha = corruption == "moved-base" ? Tip : Definition } };
            else if (path == "/users/maintainer") response = new { id = 55, type = "User" };
            else if (path == prefix + "/collaborators/maintainer/permission")
                response = new { user = new { id = 55 }, permission = "write" };
            else if (path == prefix + "/issues/249")
                response = new { id = 2490, number = 249, state = "open", labels = Array.Empty<object>() };
            else if (path == prefix + "/git/ref/" + GitHubPocSettings.StateReference["refs/".Length..])
            {
                if (!Exists) return new(HttpStatusCode.NotFound);
                response = new { @object = new { sha = Tip } };
            }
            else if (request.Method == HttpMethod.Post && path.StartsWith(prefix + "/git/", StringComparison.Ordinal))
            {
                Mutations++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (path.EndsWith("/blobs", StringComparison.Ordinal))
                {
                    StoredState = StateJson.Deserialize(body.RootElement.GetProperty("content").GetString()!);
                    response = new { sha = Blob };
                }
                else if (path.EndsWith("/trees", StringComparison.Ordinal)) response = new { sha = Tree };
                else if (path.EndsWith("/commits", StringComparison.Ordinal)) response = new { sha = Tip };
                else if (path.EndsWith("/refs", StringComparison.Ordinal))
                {
                    Assert.Equal(GitHubPocSettings.StateReference, body.RootElement.GetProperty("ref").GetString());
                    Exists = true;
                    response = new { @ref = GitHubPocSettings.StateReference, @object = new { sha = Tip } };
                }
                else throw new InvalidOperationException("Unexpected PoC mutation.");
            }
            else if (path == prefix + "/git/commits/" + Tip) response = new { tree = new { sha = Tree } };
            else if (path == prefix + "/git/trees/" + Tree)
                response = new { truncated = false, tree = new[]
                    { new { path = "state.json", type = "blob", mode = "100644", sha = Blob } } };
            else if (path == prefix + "/git/blobs/" + Blob)
            {
                var json = StateJson.Serialize(CurrentState);
                response = new { encoding = "base64", size = Encoding.UTF8.GetByteCount(json),
                    content = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) };
            }
            else throw new InvalidOperationException("Unexpected PoC API call: " + path);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }
    }
}
