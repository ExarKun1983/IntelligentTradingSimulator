using System.Net;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubLiveCredentialsTests
{
    [Fact]
    public async Task BothCredentialsAreVerifiedBeforeStateAndOnlyWorkflowCredentialWritesState()
    {
        using var handler = new IdentityApi();
        using var workflow = Client(handler, "offline-workflow");
        using var user = Client(handler, "offline-user");
        var publisher = await GitHubLiveCredentials.VerifyAsync(new(workflow, "owner/repo", 1),
            new(user, "owner/repo", 1), 55);
        Assert.Equal(new GitHubStatusPublisher(41898282, "github-actions[bot]", "bot-node", "Bot"), publisher);
        Assert.Equal(new[] { "/user", "/graphql", "/users/github-actions[bot]", "/repos/owner/repo" },
            handler.Paths);
        var transport = new GitHubGitTransport(workflow, "owner/repo", true);
        var parent = new string('a', 40);
        var child = new string('b', 40);
        Assert.Equal(child, await transport.CreateBlobAsync("{}", default));
        Assert.Equal(child, await transport.CreateTreeAsync("state.json", child, default));
        Assert.Equal(child, await transport.CreateCommitAsync(child, parent, "state", default));
        Assert.True((await transport.TryAdvanceRefAsync("refs/heads/agent-state", parent, child, default)).Advanced);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.TryAdvanceRefAsync("refs/heads/master", parent, child, default));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("heads/master"));
    }

    [Theory]
    [InlineData("user-id")]
    [InlineData("user-type")]
    [InlineData("viewer-id")]
    [InlineData("viewer-login")]
    [InlineData("viewer-node")]
    [InlineData("graphql-errors")]
    [InlineData("bot-id")]
    [InlineData("bot-login")]
    [InlineData("bot-type")]
    [InlineData("repository")]
    public async Task EitherCredentialIdentityMismatchFailsClosedBeforeEffects(string corruption)
    {
        using var handler = new IdentityApi(corruption);
        using var workflow = Client(handler, "offline-workflow");
        using var user = Client(handler, "offline-user");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GitHubLiveCredentials.VerifyAsync(new(workflow, "owner/repo", 1), new(user, "owner/repo", 1), 55));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/git/") || path.StartsWith("/agents/"));
    }

    [Fact]
    public async Task CredentialsCannotVerifyDifferentRepositories()
    {
        using var handler = new IdentityApi();
        using var workflow = Client(handler, "offline-workflow");
        using var user = Client(handler, "offline-user");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GitHubLiveCredentials.VerifyAsync(new(workflow, "owner/repo", 1), new(user, "owner/other", 2), 55));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public void TestWorkflowExposesSeparateCredentialsAndOnlyRequiredPermissions()
    {
        var root = FindRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/agent-state-machine.yml"));
        Assert.Contains("github.workflow_ref == format('{0}/.github/workflows/agent-state-machine.yml@refs/heads/master', github.repository)", workflow);
        Assert.DoesNotContain("github.ref == 'refs/heads/master'", workflow);
        Assert.Contains("vars.ORCHESTRATION_ACTIVATION_APPROVED == 'true'", workflow);
        Assert.Contains("vars.ORCHESTRATION_TEST_ISSUE == '227'", workflow);
        Assert.Contains("vars.ORCHESTRATION_TEST_MAX_REPAIR_TASKS == '1'", workflow);
        Assert.Contains("GITHUB_TOKEN: ${{ github.token }}", workflow);
        Assert.Contains("ORCHESTRATION_USER_TOKEN: ${{ secrets.ORCHESTRATION_USER_TOKEN }}", workflow);
        Assert.Contains("ORCHESTRATION_TEST_ISSUE: ${{ vars.ORCHESTRATION_TEST_ISSUE }}", workflow);
        Assert.Contains("ORCHESTRATION_TEST_MAX_REPAIR_TASKS: ${{ vars.ORCHESTRATION_TEST_MAX_REPAIR_TASKS }}", workflow);
        Assert.Contains("environment: agent-orchestration", workflow);
        Assert.Contains("persist-credentials: false", workflow);
        var permissions = workflow.Split("permissions:\n")[1].Split("\njobs:")[0].Trim();
        Assert.Equal("contents: write\n  issues: write\n  pull-requests: write\n  actions: write\n  statuses: write", permissions);
        var ci = File.ReadAllText(Path.Combine(root, ".github/workflows/agent-managed-ci.yml"));
        Assert.Contains("github.ref == vars.ORCHESTRATION_CI_REF", ci);
        Assert.Contains("startsWith(github.ref, 'refs/tags/')", ci);
        Assert.Contains("vars.ORCHESTRATION_ACTIVATION_APPROVED == 'true'", ci);
        Assert.Contains("vars.ORCHESTRATION_TEST_ISSUE == '227'", ci);
        Assert.Contains("vars.ORCHESTRATION_TEST_MAX_REPAIR_TASKS == '1'", ci);
    }

    [Fact]
    public void GateDiagnosticsAreManualAndDoNotExposeLiveCapabilities()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRoot(), ".github/workflows/agent-gate-diagnostics.yml"));
        Assert.Contains("on:\n  workflow_dispatch:\n", workflow);
        Assert.Contains("permissions: {}", workflow);
        Assert.Contains("timeout-minutes: 2", workflow);
        Assert.Contains("github.workflow_ref == format('{0}/.github/workflows/agent-gate-diagnostics.yml@refs/heads/master', github.repository)", workflow);
        Assert.Contains("vars.ORCHESTRATION_ACTIVATION_APPROVED == 'true'", workflow);
        Assert.Contains("vars.ORCHESTRATION_TEST_ISSUE == '227'", workflow);
        Assert.Contains("vars.ORCHESTRATION_TEST_MAX_REPAIR_TASKS == '1'", workflow);
        Assert.Contains("vars.ORCHESTRATION_DEFINITION_SHA == github.sha", workflow);
        Assert.Contains("vars.ORCHESTRATION_CI_REF == 'refs/tags/agent-managed-ci-test-227-v2'", workflow);
        Assert.Contains("vars.ORCHESTRATION_CI_DEFINITION_SHA == '5d122e64ea5eafc51eba1fd150ec33f7869b5731'", workflow);
        Assert.Contains("GITHUB_STEP_SUMMARY", workflow);
        Assert.DoesNotContain("if:", workflow);
        Assert.DoesNotContain("environment:", workflow);
        Assert.DoesNotContain("secrets.", workflow);
        Assert.DoesNotContain("github.token", workflow);
        Assert.DoesNotContain("uses:", workflow);
        Assert.DoesNotContain("--github-live", workflow);
        Assert.DoesNotContain("gh ", workflow);
        Assert.DoesNotContain("curl ", workflow);
        Assert.DoesNotContain("contents: write", workflow);
    }

    [Fact]
    public void TaskMappingDiagnosticsOnlyReadTheSavedTaskWithProtectedCredentials()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRoot(), ".github/workflows/agent-task-mapping-diagnostics.yml"));
        Assert.Contains("on:\n  workflow_dispatch:\n", workflow);
        Assert.Contains("permissions: {}", workflow);
        Assert.Contains("vars.ORCHESTRATION_ACTIVATION_APPROVED == 'false'", workflow);
        Assert.Contains("github.workflow_ref == format('{0}/.github/workflows/agent-task-mapping-diagnostics.yml@refs/heads/master', github.repository)", workflow);
        Assert.Contains("environment: agent-orchestration", workflow);
        Assert.Contains("TASK_READ_TOKEN: ${{ secrets.ORCHESTRATION_USER_TOKEN }}", workflow);
        Assert.Contains("/tasks/e4743d38-6b27-49cd-a272-83b34465a808", workflow);
        Assert.Contains("method=\"GET\"", workflow);
        Assert.Contains("NoRedirect", workflow);
        Assert.DoesNotContain("uses:", workflow);
        Assert.DoesNotContain("inputs:", workflow);
        Assert.DoesNotContain("--github-live", workflow);
        Assert.DoesNotContain("github.token", workflow);
        Assert.DoesNotContain("POST", workflow);
        Assert.DoesNotContain("print(payload)", workflow);
        Assert.DoesNotContain("print(task)", workflow);
    }

    [Fact]
    public void BoundedTestModeRequiresOneIssueAndExactlyOneRepairTask()
    {
        var values = LiveSettingsValues();
        values["ORCHESTRATION_TEST_ISSUE"] = "224";
        values["ORCHESTRATION_TEST_MAX_REPAIR_TASKS"] = "1";
        var settings = GitHubLiveSettings.Read(name => values.GetValueOrDefault(name));
        Assert.Equal(224, settings.TestIssue);
        Assert.Equal(1, settings.TestMaxRepairTasks);

        values["ORCHESTRATION_TEST_MAX_REPAIR_TASKS"] = "2";
        Assert.Throws<InvalidDataException>(() =>
            GitHubLiveSettings.Read(name => values.GetValueOrDefault(name)));
        values.Remove("ORCHESTRATION_TEST_ISSUE");
        values["ORCHESTRATION_TEST_MAX_REPAIR_TASKS"] = "1";
        Assert.Throws<InvalidDataException>(() =>
            GitHubLiveSettings.Read(name => values.GetValueOrDefault(name)));
    }

    [Fact]
    public void SinglePassTestCanRunWithoutRequiredStatusContextAndDoesNotEnableStatusPublication()
    {
        var values = LiveSettingsValues();
        values["ORCHESTRATION_TEST_ISSUE"] = "227";
        values["ORCHESTRATION_TEST_MAX_REPAIR_TASKS"] = "1";
        values.Remove("ORCHESTRATION_REQUIRED_CONTEXT_VERIFIED");

        var settings = GitHubLiveSettings.Read(name => values.GetValueOrDefault(name));

        Assert.False(settings.Status.AllowsPublication);
    }

    [Fact]
    public void ProductionModeStillRequiresVerifiedRequiredStatusContext()
    {
        var values = LiveSettingsValues();
        values.Remove("ORCHESTRATION_REQUIRED_CONTEXT_VERIFIED");

        Assert.Throws<InvalidOperationException>(() =>
            GitHubLiveSettings.Read(name => values.GetValueOrDefault(name)));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IntelligentTradingSimulator.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, string> LiveSettingsValues()
    {
        var values = new Dictionary<string, string>
        {
            ["GITHUB_REPOSITORY"] = "owner/repo",
            ["GITHUB_REPOSITORY_ID"] = "1",
            ["ORCHESTRATION_ACTOR_ID"] = "55",
            ["ORCHESTRATION_BASE_BRANCH"] = "master",
            ["ORCHESTRATION_WORKFLOW_ID"] = "10",
            ["ORCHESTRATION_DEFINITION_SHA"] = new string('a', 40),
            ["ORCHESTRATION_CI_WORKFLOW_ID"] = "11",
            ["ORCHESTRATION_CI_REF"] = "refs/tags/agent-managed-ci-v1",
            ["ORCHESTRATION_CI_DEFINITION_SHA"] = new string('b', 40),
        };
        foreach (var name in new[]
        {
            "ORCHESTRATION_ACTIVATION_APPROVED", "ORCHESTRATION_USER_TOKEN_VERIFIED",
            "ORCHESTRATION_PROTECTED_CREDENTIAL_ENVIRONMENT_VERIFIED",
            "ORCHESTRATION_NO_ADDED_CHARGES_VERIFIED", "ORCHESTRATION_TASK_ENTITLEMENT_VERIFIED",
            "ORCHESTRATION_TRUSTED_DEFINITION_VERIFIED", "ORCHESTRATION_REVIEW_POLICY_VERIFIED",
            "ORCHESTRATION_AUTOMATIC_REVIEWS_DISABLED", "ORCHESTRATION_TRUSTED_CI_VERIFIED",
            "ORCHESTRATION_TRUSTED_STATUS_PUBLISHER_VERIFIED", "ORCHESTRATION_REQUIRED_CONTEXT_VERIFIED",
        })
            values[name] = "true";
        return values;
    }

    private static HttpClient Client(HttpMessageHandler handler, string credential)
    {
        var client = new HttpClient(handler, false) { BaseAddress = new("https://api.github.com/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        return client;
    }

    private sealed class IdentityApi(string corruption = "") : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Paths.Add(path);
            Assert.Equal(path == "/user" ? "offline-user" : "offline-workflow", request.Headers.Authorization?.Parameter);
            var parent = new string('a', 40);
            var child = new string('b', 40);
            object body = path switch
            {
                "/user" => new { id = corruption == "user-id" ? 56 : 55,
                    login = "maintainer", type = corruption == "user-type" ? "Bot" : "User" },
                "/graphql" => new { data = new { viewer = new {
                    databaseId = corruption == "viewer-id" ? 55 : 41898282,
                    login = corruption == "viewer-login" ? "maintainer" : "github-actions[bot]",
                    id = corruption == "viewer-node" ? "other-node" : "bot-node" } },
                    errors = corruption == "graphql-errors" ? new[] { new { message = "incomplete" } } : [] },
                "/users/github-actions[bot]" => new { id = corruption == "bot-id" ? 55 : 41898282,
                    login = corruption == "bot-login" ? "other[bot]" : "github-actions[bot]",
                    type = corruption == "bot-type" ? "User" : "Bot", node_id = "bot-node" },
                "/repos/owner/repo" => new { id = corruption == "repository" ? 2 : 1, full_name = "owner/repo" },
                "/repos/owner/repo/git/blobs" or "/repos/owner/repo/git/trees" or "/repos/owner/repo/git/commits" =>
                    new { sha = child },
                _ when path == "/repos/owner/repo/git/commits/" + child => new { parents = new[] { new { sha = parent } } },
                "/repos/owner/repo/git/ref/heads/agent-state" => new { @object = new { sha = parent } },
                "/repos/owner/repo/git/refs/heads/agent-state" => new { @object = new { sha = child } },
                _ => throw new InvalidOperationException("Unexpected HTTP request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }
}
