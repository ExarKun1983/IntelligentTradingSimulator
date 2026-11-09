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
    public void DisabledWorkflowExposesSeparateCredentialsAndOnlyRequiredPermissions()
    {
        var root = FindRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/agent-state-machine.yml"));
        Assert.Contains("if: ${{ false }}", workflow);
        Assert.Contains("GITHUB_TOKEN: ${{ github.token }}", workflow);
        Assert.Contains("ORCHESTRATION_USER_TOKEN: ${{ secrets.ORCHESTRATION_USER_TOKEN }}", workflow);
        Assert.Contains("environment: agent-orchestration", workflow);
        Assert.Contains("persist-credentials: false", workflow);
        var permissions = workflow.Split("permissions:\n")[1].Split("\njobs:")[0].Trim();
        Assert.Equal("contents: write\n  issues: write\n  pull-requests: write\n  actions: write\n  statuses: write", permissions);
        Assert.Contains("if: ${{ false }}",
            File.ReadAllText(Path.Combine(root, ".github/workflows/agent-managed-ci.yml")));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IntelligentTradingSimulator.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
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
