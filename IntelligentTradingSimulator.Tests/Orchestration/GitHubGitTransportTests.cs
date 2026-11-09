using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public class GitHubGitTransportTests
{
    private const string Parent = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Child = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Tree = "cccccccccccccccccccccccccccccccccccccccc";
    private const string Blob = "dddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public async Task WritesAreDisabledBeforeAnyHttpRequestByDefault()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = Client(handler);
        var transport = new GitHubGitTransport(client, "owner/repo");
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.CreateBlobAsync("{}", default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CanonicalBlobUsesActualGitApiResponseShapes()
    {
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            $"/repos/owner/repo/git/commits/{Parent}" => Json(new { tree = new { sha = Tree } }),
            $"/repos/owner/repo/git/trees/{Tree}" => Json(new
            {
                truncated = false,
                tree = new[] { new { path = "state.json", type = "blob", mode = "100644", sha = Blob } }
            }),
            $"/repos/owner/repo/git/blobs/{Blob}" => Json(new
            {
                encoding = "base64", size = 2,
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
            }),
            _ => throw new InvalidOperationException("Unexpected HTTP")
        });
        using var client = Client(handler);
        var result = await new GitHubGitTransport(client, "owner/repo").ReadCommitAsync(Parent, default);
        Assert.Equal("{}", result.StateJson);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task PublicationNeverUsesForceAndVerifiesSingleParent()
    {
        var handler = new StubHandler((request, body) => request.Method.Method switch
        {
            "GET" when request.RequestUri!.AbsolutePath.Contains("/commits/") =>
                Json(new { parents = new[] { new { sha = Parent } } }),
            "GET" => Json(new { @object = new { sha = Parent } }),
            "PATCH" => ValidatePatch(body),
            _ => throw new InvalidOperationException("Unexpected HTTP")
        });
        using var client = Client(handler);
        var result = await new GitHubGitTransport(client, "owner/repo", true)
            .TryAdvanceRefAsync("refs/heads/agent-state", Parent, Child, default);
        Assert.True(result.Advanced);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task CompetingPublicationDoesNotPatchTheNewTip()
    {
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/commits/")
            ? Json(new { parents = new[] { new { sha = Parent } } })
            : Json(new { @object = new { sha = Tree } }));
        using var client = Client(handler);
        Assert.False((await new GitHubGitTransport(client, "owner/repo", true)
            .TryAdvanceRefAsync("refs/heads/agent-state", Parent, Child, default)).Advanced);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task LostPatchResponseRequiresPublicationReconciliation()
    {
        var handler = new StubHandler((request, _) => request.Method.Method switch
        {
            "GET" when request.RequestUri!.AbsolutePath.Contains("/commits/") =>
                Json(new { parents = new[] { new { sha = Parent } } }),
            "GET" => Json(new { @object = new { sha = Parent } }),
            _ => throw new HttpRequestException("Simulated lost publication response")
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<PublicationUncertainException>(() =>
            new GitHubGitTransport(client, "owner/repo", true)
                .TryAdvanceRefAsync("refs/heads/agent-state", Parent, Child, default));
    }

    [Fact]
    public async Task MissingRefCannotBeCreatedByTransport()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubGitTransport(client, "owner/repo", true)
                .TryAdvanceRefAsync("refs/heads/agent-state", null, Child, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void RefusesNonGitHubOrigin()
    {
        using var client = new HttpClient { BaseAddress = new Uri("https://example.invalid/") };
        Assert.Throws<ArgumentException>(() => new GitHubGitTransport(client, "owner/repo"));
    }

    private static HttpResponseMessage ValidatePatch(string? body)
    {
        using var json = JsonDocument.Parse(body!);
        Assert.False(json.RootElement.GetProperty("force").GetBoolean());
        Assert.Equal(Child, json.RootElement.GetProperty("sha").GetString());
        return Json(new { @object = new { sha = Child } });
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.github.com/") };
    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            return respond(request, body);
        }
    }
}
