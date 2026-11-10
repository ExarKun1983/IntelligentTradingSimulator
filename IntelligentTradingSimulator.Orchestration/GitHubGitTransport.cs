using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Git database transport. The host supplies credentials; writes are disabled unless explicitly injected.</summary>
public sealed partial class GitHubGitTransport : IGitObjectRefTransport
{
    private readonly HttpClient client;
    private readonly string prefix;
    private readonly bool allowMutations;
    private readonly string stateReference;
    private readonly bool allowIsolatedInitialization;
    private const int MaximumStateBytes = 2 * 1024 * 1024;

    public GitHubGitTransport(HttpClient client, string repository, bool allowMutations = false,
        string stateReference = "refs/heads/agent-state", bool allowIsolatedInitialization = false)
    {
        if (!RepositoryPattern().IsMatch(repository))
            throw new ArgumentException("Expected owner/repository.", nameof(repository));
        if (client.BaseAddress != new Uri("https://api.github.com/"))
            throw new ArgumentException("Only the GitHub API origin is permitted.", nameof(client));
        this.client = client;
        this.allowMutations = allowMutations;
        if (stateReference is not ("refs/heads/agent-state" or "refs/heads/agent-state-status-revocation-v1"))
            throw new ArgumentException("Only a production or the dedicated status-test state ref is supported.", nameof(stateReference));
        if (allowIsolatedInitialization && stateReference != "refs/heads/agent-state-status-revocation-v1")
            throw new ArgumentException("Only the dedicated isolated test ref supports one-shot initialization.", nameof(allowIsolatedInitialization));
        this.stateReference = stateReference;
        this.allowIsolatedInitialization = allowIsolatedInitialization;
        prefix = $"repos/{repository}/";
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IntelligentTradingSimulator-Orchestration");
        if (client.DefaultRequestHeaders.Accept.Count == 0)
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (!client.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<string?> ReadRefAsync(string reference, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(prefix + "git/ref/" + RefPath(reference), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var json = await ReadJsonAsync(response, cancellationToken);
        return Sha(json.RootElement.GetProperty("object").GetProperty("sha").GetString());
    }

    public async Task<GitCommitObject> ReadCommitAsync(string commit, CancellationToken cancellationToken)
    {
        commit = Sha(commit);
        using var response = await client.GetAsync(prefix + "git/commits/" + commit, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var tree = Sha(json.RootElement.GetProperty("tree").GetProperty("sha").GetString());
        using var treeResponse = await client.GetAsync(prefix + "git/trees/" + tree, cancellationToken);
        using var treeJson = await ReadJsonAsync(treeResponse, cancellationToken);
        if (treeJson.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
            throw new InvalidDataException("Truncated state tree.");
        var entries = treeJson.RootElement.GetProperty("tree").EnumerateArray()
            .Where(item => item.GetProperty("path").GetString() == "state.json").ToList();
        if (entries.Count != 1 || entries[0].GetProperty("type").GetString() != "blob" ||
            entries[0].GetProperty("mode").GetString() != "100644")
            throw new InvalidDataException("Missing or invalid canonical state blob.");
        var blob = Sha(entries[0].GetProperty("sha").GetString());
        using var blobResponse = await client.GetAsync(prefix + "git/blobs/" + blob, cancellationToken);
        using var blobJson = await ReadJsonAsync(blobResponse, cancellationToken);
        if (blobJson.RootElement.GetProperty("encoding").GetString() != "base64" ||
            blobJson.RootElement.GetProperty("size").GetInt64() > MaximumStateBytes)
            throw new InvalidDataException("Unsupported or oversized state blob.");
        var content = Convert.FromBase64String(blobJson.RootElement.GetProperty("content").GetString()!);
        if (content.Length > MaximumStateBytes) throw new InvalidDataException("Oversized state.");
        return new GitCommitObject(commit, new UTF8Encoding(false, true).GetString(content));
    }

    public async Task<string> CreateBlobAsync(string content, CancellationToken cancellationToken)
    {
        RequireWrites();
        if (Encoding.UTF8.GetByteCount(content) > MaximumStateBytes)
            throw new InvalidDataException("State capacity reached; operator intervention required.");
        return await CreateAsync("git/blobs", new { content, encoding = "utf-8" }, cancellationToken);
    }

    public async Task<string> CreateTreeAsync(string path, string blob, CancellationToken cancellationToken)
    {
        RequireWrites();
        if (path != "state.json") throw new ArgumentException("Only canonical state is supported.", nameof(path));
        return await CreateAsync("git/trees", new
        {
            tree = new[] { new { path, mode = "100644", type = "blob", sha = Sha(blob) } }
        }, cancellationToken);
    }

    public async Task<string> CreateCommitAsync(
        string tree, string? parent, string message, CancellationToken cancellationToken)
    {
        RequireWrites();
        if (parent is null && !allowIsolatedInitialization)
            throw new InvalidOperationException("State bootstrap is an explicit operator prerequisite.");
        var payload = parent is null
            ? new { tree = Sha(tree), parents = Array.Empty<string>(), message }
            : new { tree = Sha(tree), parents = new[] { Sha(parent) }, message };
        return await CreateAsync("git/commits", payload, cancellationToken);
    }

    public async Task<RefUpdateResult> TryAdvanceRefAsync(
        string reference, string? expectedTip, string commit, CancellationToken cancellationToken)
    {
        RequireWrites();
        commit = Sha(commit);
        if (expectedTip is null)
        {
            if (!allowIsolatedInitialization || stateReference != "refs/heads/agent-state-status-revocation-v1")
                throw new InvalidOperationException("Automatic state-ref creation is not supported.");
            if (await ReadRefAsync(reference, cancellationToken) is not null) return new(false);
            using var created = await client.PostAsJsonAsync(prefix + "git/refs",
                new { @ref = RefPath(reference), sha = commit }, cancellationToken);
            if (created.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
                return new(false);
            using var createdJson = await ReadJsonAsync(created, cancellationToken);
            if (createdJson.RootElement.GetProperty("ref").GetString() != reference ||
                createdJson.RootElement.GetProperty("object").GetProperty("sha").GetString() != commit)
                throw new PublicationUncertainException("Isolated state bootstrap response requires reconciliation.");
            return new(true);
        }
        expectedTip = Sha(expectedTip);
        // The REST endpoint has no expected-old-SHA argument. Require an exact single parent and fast-forward only.
        using var commitResponse = await client.GetAsync(prefix + "git/commits/" + commit, cancellationToken);
        using var commitJson = await ReadJsonAsync(commitResponse, cancellationToken);
        var parents = commitJson.RootElement.GetProperty("parents").EnumerateArray().ToList();
        if (parents.Count != 1 || parents[0].GetProperty("sha").GetString() != expectedTip)
            throw new InvalidDataException("State publication must be a single child of the expected tip.");
        if (await ReadRefAsync(reference, cancellationToken) != expectedTip) return new(false);
        try
        {
            using var response = await client.PatchAsJsonAsync(prefix + "git/refs/" + RefPath(reference),
                new { sha = commit, force = false }, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
                return new(false);
            using var result = await ReadJsonAsync(response, cancellationToken);
            if (result.RootElement.GetProperty("object").GetProperty("sha").GetString() != commit)
                throw new PublicationUncertainException("Unexpected publication response.");
            return new(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            throw new PublicationUncertainException("State-ref publication outcome requires reconciliation.");
        }
    }

    public async Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            prefix + "compare/" + Sha(ancestor) + "..." + Sha(descendant), cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        return json.RootElement.GetProperty("status").GetString() is "ahead" or "identical";
    }

    private async Task<string> CreateAsync(string path, object payload, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(prefix + path, payload, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        return Sha(json.RootElement.GetProperty("sha").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    private void RequireWrites()
    {
        if (!allowMutations) throw new InvalidOperationException("Git state writes are disabled.");
    }

    private static string Sha(string? sha) => sha is not null && ShaPattern().IsMatch(sha)
        ? sha : throw new InvalidDataException("Invalid Git object identity.");

    private string RefPath(string reference)
    {
        if (reference != stateReference)
            throw new ArgumentException("Only the approved state reference is supported.", nameof(reference));
        return reference["refs/heads/".Length..] is { Length: > 0 } branch
            ? "heads/" + branch
            : throw new ArgumentException("Invalid state reference.", nameof(reference));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^[a-fA-F0-9]{40}$")]
    private static partial Regex ShaPattern();
}
