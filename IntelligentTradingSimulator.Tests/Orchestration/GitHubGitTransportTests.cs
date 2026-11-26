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

    [Theory]
    [InlineData("Ref", "RefIdentity")]
    [InlineData("Commit", "TreeIdentity")]
    [InlineData("Tree", "TruncatedTree")]
    [InlineData("Blob", "BlobEncoding")]
    [InlineData("RepositorySchema", "Repository")]
    public async Task FailedCanonicalReadIdentifiesItsStepWithoutExposingResponseContent(string step, string check)
    {
        var state = RepositoryState.Create(step == "RepositorySchema" ? "private/repository" : "owner/repo");
        var bytes = Encoding.UTF8.GetBytes(StateJson.Serialize(state));
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/repos/owner/repo/git/ref/heads/agent-state" =>
                Json(new { @object = new { sha = step == "Ref" ? "private-git-identity" : Parent } }),
            $"/repos/owner/repo/git/commits/{Parent}" =>
                Json(new { tree = new { sha = step == "Commit" ? "private-tree-identity" : Tree } }),
            $"/repos/owner/repo/git/trees/{Tree}" => Json(new
            {
                truncated = step == "Tree",
                tree = new[] { new { path = "state.json", type = "blob", mode = "100644", sha = Blob } }
            }),
            $"/repos/owner/repo/git/blobs/{Blob}" => Json(new
            {
                encoding = step == "Blob" ? "private-encoding" : "base64",
                size = bytes.Length, content = Convert.ToBase64String(bytes)
            }),
            _ => throw new InvalidOperationException("Unexpected HTTP")
        });
        using var client = Client(handler);
        using var output = new StringWriter();
        var diagnostics = new GitHubLiveDiagnostics();
        diagnostics.Mark(GitHubLiveStage.RequiredStatusInitialStateLoad);
        var store = new GitStateStore(new GitHubGitTransport(client, "owner/repo", diagnostics: diagnostics),
            "owner/repo", diagnostics: diagnostics);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        diagnostics.Capture(exception);
        diagnostics.Mark(GitHubLiveStage.WorkerLeaseRelease);
        diagnostics.MarkStateRead(GitStateReadStep.Ref);
        diagnostics.WriteFailure(output, exception);

        using var json = JsonDocument.Parse(output.ToString());
        var failure = json.RootElement.GetProperty("LiveDiagnostic");
        Assert.Equal("RequiredStatusInitialStateLoad", failure.GetProperty("Stage").GetString());
        Assert.Equal(step, failure.GetProperty("StateRead").GetString());
        Assert.Equal(check, failure.GetProperty("StateReadCheck").GetString());
        Assert.DoesNotContain("private", output.ToString());
        Assert.DoesNotContain("owner/repo", output.ToString());
        Assert.DoesNotContain(Parent, output.ToString());
        Assert.DoesNotContain("state.json", output.ToString());
    }

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
    public async Task StateStoreCachesImmutableCommitButAlwaysReadsRefAndReturnsIndependentState()
    {
        string? tip = Parent;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/git/ref/heads/agent-state", StringComparison.Ordinal))
                return tip is null ? new(HttpStatusCode.NotFound) : Json(new { @object = new { sha = tip } });
            if (path.Contains("/git/commits/", StringComparison.Ordinal))
                return Json(new { tree = new { sha = path.EndsWith(Parent, StringComparison.Ordinal) ? Parent : Child } });
            if (path.Contains("/git/trees/", StringComparison.Ordinal))
                return Json(new { truncated = false, tree = new[] { new { path = "state.json", type = "blob",
                    mode = "100644", sha = path.EndsWith(Parent, StringComparison.Ordinal) ? Parent : Child } } });
            if (path.Contains("/git/blobs/", StringComparison.Ordinal))
            {
                var state = RepositoryState.Create("owner/repo");
                state.Version = path.EndsWith(Parent, StringComparison.Ordinal) ? 1 : 2;
                var bytes = Encoding.UTF8.GetBytes(StateJson.Serialize(state));
                return Json(new { encoding = "base64", size = bytes.Length, content = Convert.ToBase64String(bytes) });
            }
            throw new InvalidOperationException("Unexpected HTTP");
        });
        using var client = Client(handler);
        var store = new GitStateStore(new GitHubGitTransport(client, "owner/repo"), "owner/repo");

        var first = await store.LoadAsync(default);
        first.State.Version = 99;
        Assert.Equal(1, (await store.LoadAsync(default)).State.Version);
        Assert.Equal(5, handler.Calls);
        tip = Child;
        Assert.Equal(2, (await store.LoadAsync(default)).State.Version);
        Assert.Equal(9, handler.Calls);
        Assert.Equal(2, (await store.LoadAsync(default)).State.Version);
        Assert.Equal(10, handler.Calls);
        tip = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        Assert.Equal(11, handler.Calls);
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

    [Theory]
    [InlineData(GitHubIsolatedStatusTestSettings.StateReference)]
    [InlineData(GitHubPocSettings.StateReference)]
    [InlineData(GitHubReviewE2ETestProfile.StateReference)]
    public async Task IsolatedTransportCanOnlyReadItsDedicatedRefAndCannotCreateItDuringRuntime(string reference)
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.EndsWith("/git/ref/" + reference["refs/".Length..], request.RequestUri!.AbsolutePath);
            return Json(new { @object = new { sha = Parent } });
        });
        using var client = Client(handler);
        var transport = new GitHubGitTransport(client, "owner/repo", stateReference: reference);

        Assert.Equal(Parent, await transport.ReadRefAsync(reference, default));
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ReadRefAsync("refs/heads/agent-state", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.TryAdvanceRefAsync(reference, null, Child, default));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(GitHubIsolatedStatusTestSettings.StateReference)]
    [InlineData(GitHubPocSettings.StateReference)]
    [InlineData(GitHubReviewE2ETestProfile.StateReference)]
    public async Task IsolatedBootstrapSendsFullyQualifiedRefAndDoesNotRepeatCreation(string reference)
    {
        var exists = false;
        var posts = 0;
        var handler = new StubHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.EndsWith("/git/ref/" + reference["refs/".Length..], request.RequestUri!.AbsolutePath);
                return exists ? Json(new { @object = new { sha = Child } }) : new(HttpStatusCode.NotFound);
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/git/refs", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(body!);
            Assert.Equal(reference, json.RootElement.GetProperty("ref").GetString());
            Assert.Equal(Child, json.RootElement.GetProperty("sha").GetString());
            posts++;
            exists = true;
            return Json(new { @ref = reference, @object = new { sha = Child } });
        });
        using var client = Client(handler);
        var transport = new GitHubGitTransport(client, "owner/repo", allowMutations: true,
            stateReference: reference, allowIsolatedInitialization: true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.TryAdvanceRefAsync("refs/heads/agent-state", null, Child, default));
        Assert.Equal(0, handler.Calls);
        Assert.True((await transport.TryAdvanceRefAsync(reference, null, Child, default)).Advanced);
        Assert.False((await transport.TryAdvanceRefAsync(reference, null, Child, default)).Advanced);
        Assert.Equal(1, posts);
    }

    [Fact]
    public void IsolatedInitializationCannotBeEnabledForTheProductionStateRef()
    {
        using var client = Client(new StubHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP")));
        Assert.Throws<ArgumentException>(() => new GitHubGitTransport(client, "owner/repo",
            allowMutations: true, stateReference: "refs/heads/agent-state", allowIsolatedInitialization: true));
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
