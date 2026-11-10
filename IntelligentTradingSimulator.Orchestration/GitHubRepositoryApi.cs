using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Repository-scoped REST reads. Neither payload fields nor labels authorize service completion.</summary>
public sealed partial class GitHubRepositoryApi
{
    private readonly HttpClient client;
    private readonly string prefix;
    public string Repository { get; }
    public long RepositoryId { get; }

    public GitHubRepositoryApi(HttpClient client, string repository, long repositoryId)
    {
        if (client.BaseAddress != new Uri("https://api.github.com/") ||
            !RepositoryPattern().IsMatch(repository) || repositoryId <= 0)
            throw new ArgumentException("A verified GitHub repository and API origin are required.");
        this.client = client;
        Repository = repository;
        RepositoryId = repositoryId;
        prefix = $"repos/{repository}/";
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IntelligentTradingSimulator-Orchestration");
        if (client.DefaultRequestHeaders.Accept.Count == 0)
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (!client.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task VerifyRepositoryAsync(CancellationToken token)
    {
        using var repository = await GetAsync("", token);
        if (repository.RootElement.GetProperty("id").GetInt64() != RepositoryId ||
            repository.RootElement.GetProperty("full_name").GetString() != Repository)
            throw new InvalidDataException("Repository API identity mismatch.");
    }

    public async Task<IngressEvidence> ReadIngressEvidenceAsync(JsonElement payload, string eventName,
        string runId, HashSet<long> projectionActorIds, CancellationToken token)
    {
        await VerifyRepositoryAsync(token);
        if (!long.TryParse(runId, out var runNumber) || runNumber <= 0)
            throw new InvalidDataException("Invalid Actions run identity.");
        using var run = await GetAsync($"actions/runs/{runId}", token);
        var sender = payload.GetProperty("sender");
        var senderId = sender.GetProperty("id").GetInt64();
        if (run.RootElement.GetProperty("event").GetString() != eventName ||
            run.RootElement.GetProperty("path").GetString() != ".github/workflows/agent-state-machine.yml" ||
            run.RootElement.GetProperty("actor").GetProperty("id").GetInt64() != senderId)
            throw new InvalidDataException("Notification does not match the trusted Actions workflow run.");
        var login = sender.GetProperty("login").GetString();
        if (string.IsNullOrWhiteSpace(login)) throw new InvalidDataException("Missing actor login.");
        using var user = await GetGlobalAsync("users/" + Uri.EscapeDataString(login), token);
        if (user.RootElement.GetProperty("id").GetInt64() != senderId ||
            user.RootElement.GetProperty("type").GetString() != sender.GetProperty("type").GetString())
            throw new InvalidDataException("API actor identity mismatch.");
        var actorPermission = "";
        if (!projectionActorIds.Contains(senderId))
        {
            using var permission = await GetAsync($"collaborators/{Uri.EscapeDataString(login)}/permission", token);
            if (permission.RootElement.GetProperty("user").GetProperty("id").GetInt64() != senderId)
                throw new InvalidDataException("Permission identity mismatch.");
            actorPermission = permission.RootElement.GetProperty("permission").GetString()!;
        }
        var number = eventName == "issues" ? payload.GetProperty("issue").GetProperty("number").GetInt32() :
            int.Parse(payload.GetProperty("inputs").GetProperty("issue").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
        if (number <= 0) throw new InvalidDataException("Invalid Issue number.");
        using var issue = await GetAsync($"issues/{number}", token);
        var root = issue.RootElement;
        if (root.GetProperty("number").GetInt32() != number)
            throw new InvalidDataException("API Issue identity mismatch.");
        return new(Repository, RepositoryId, senderId, user.RootElement.GetProperty("type").GetString()!,
            actorPermission, root.GetProperty("id").GetInt64(),
            number, root.GetProperty("state").GetString() == "open",
            Labels(root).Contains("agent:ready"), root.TryGetProperty("pull_request", out _), projectionActorIds);
    }

    public async Task<LiveObservation> ObserveAsync(RepositoryState state, DateTimeOffset now, CancellationToken token,
        int? excludedIssueNumber = null)
    {
        if (state.Repository != Repository || state.Issues.Count > OrchestrationReducer.MaximumIndexEntries)
            throw new InvalidDataException("Observation repository or capacity mismatch.");
        await VerifyRepositoryAsync(token);
        var result = new LiveObservation { ObservedAt = now };
        foreach (var issue in state.Issues.Values)
        {
            if (issue.Number == excludedIssueNumber) continue;
            using var response = await GetAsync($"issues/{issue.Number}", token);
            var root = response.RootElement;
            if (root.GetProperty("number").GetInt32() != issue.Number || root.TryGetProperty("pull_request", out _))
                throw new InvalidDataException("Canonical Issue is not the observed Issue.");
            var live = new LiveIssueObservation
            {
                Closed = root.GetProperty("state").GetString() == "closed",
                Labels = Labels(root),
            };
            live.Ready = live.Labels.Contains("agent:ready");
            if (issue.PullRequest is { } number)
            {
                using var pull = await GetAsync($"pulls/{number}", token);
                var pr = pull.RootElement;
                if (pr.GetProperty("number").GetInt32() != number ||
                    pr.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != RepositoryId)
                    throw new InvalidDataException("Pull request repository mapping mismatch.");
                var sha = pr.GetProperty("head").GetProperty("sha").GetString();
                if (sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Invalid live PR head.");
                live.PullRequest = number;
                live.HeadSha = sha;
                live.PullRequestMerged = pr.GetProperty("merged").GetBoolean();
                live.Closed |= pr.GetProperty("state").GetString() == "closed";
            }
            // Deliberately absent: a PR head, draft flag or CI run cannot prove task completion.
            live.CompletedTaskId = null;
            result.Issues.Add(issue.Number, live);
        }
        return result;
    }

    internal Task<JsonDocument> GetAsync(string path, CancellationToken token) =>
        GetGlobalAsync(path.Length == 0 ? prefix.TrimEnd('/') : prefix + path, token);

    internal async Task<List<JsonElement>> ListAsync(string path, CancellationToken token)
    {
        var values = new List<JsonElement>();
        for (var page = 1; page <= 21; page++)
        {
            using var response = await GetAsync($"{path}{(path.Contains('?') ? '&' : '?')}per_page=100&page={page}", token);
            var items = response.RootElement.EnumerateArray().ToList();
            if (items.Count > 100 || values.Count + items.Count > OrchestrationReducer.MaximumIndexEntries)
                throw new InvalidDataException("API collection capacity exceeded; reconciliation is incomplete.");
            values.AddRange(items.Select(item => item.Clone()));
            if (items.Count < 100) return values;
        }
        throw new InvalidDataException("API pagination limit exceeded.");
    }

    internal async Task<JsonDocument> WriteAsync(HttpMethod method, string path, object payload, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, prefix + path) { Content = JsonContent.Create(payload) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return await ReadJsonAsync(response, token);
    }

    internal async Task<JsonDocument> GetGlobalAsync(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        return await ReadJsonAsync(response, token);
    }

    internal async Task<JsonDocument> PostGlobalAsync(string path, object payload, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return await ReadJsonAsync(response, token);
    }

    internal async Task<int> ResolvePullArtifactAsync(JsonElement artifact, CancellationToken token,
        int? branchPullRequest = null)
    {
        var databaseId = artifact.GetProperty("id").GetInt64();
        var globalId = artifact.GetProperty("global_id").GetString();
        if (databaseId > 0 && globalId == "" && branchPullRequest is > 0)
            return await ValidatePullArtifactRestAsync(databaseId, branchPullRequest.Value, token);
        if (databaseId <= 0 || string.IsNullOrWhiteSpace(globalId) || globalId.Length > 256)
            throw new InvalidDataException("Pull artifact requires database and global node identities.");
        const string query = """
            query($id: ID!) {
              node(id: $id) {
                __typename
                ... on PullRequest {
                  fullDatabaseId number
                  repository { nameWithOwner }
                }
              }
            }
            """;
        // GraphQL POST is a read-only query, never a mutation or a decoded/guessed PR number.
        using var response = await PostGlobalAsync("graphql", new { query, variables = new { id = globalId } }, token);
        var root = response.RootElement;
        if (root.TryGetProperty("errors", out var errors) &&
            (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
            throw new InvalidDataException("GraphQL artifact lookup is incomplete.");
        var node = root.GetProperty("data").GetProperty("node");
        if (node.ValueKind != JsonValueKind.Object || node.GetProperty("__typename").GetString() != "PullRequest" ||
            ReadDatabaseId(node.GetProperty("fullDatabaseId")) != databaseId ||
            node.GetProperty("repository").GetProperty("nameWithOwner").GetString() != Repository)
            throw new InvalidDataException("Pull artifact node or repository identity mismatch.");
        var number = node.GetProperty("number").GetInt32();
        if (number <= 0) throw new InvalidDataException("Invalid resolved PR number.");
        return await ValidatePullArtifactRestAsync(databaseId, number, token);
    }

    private async Task<int> ValidatePullArtifactRestAsync(long databaseId, int number, CancellationToken token)
    {
        using var pull = await GetAsync($"pulls/{number}", token);
        if (pull.RootElement.GetProperty("id").GetInt64() != databaseId ||
            pull.RootElement.GetProperty("number").GetInt32() != number ||
            pull.RootElement.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != RepositoryId)
            throw new InvalidDataException("Resolved PR REST identity mismatch.");
        return number;
    }

    private static long ReadDatabaseId(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericId)
        ? numericId
        : value.ValueKind == JsonValueKind.String &&
          long.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
              System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id : throw new InvalidDataException("Invalid GraphQL database identity.");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        const int maximumBytes = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("API response capacity exceeded.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (content.Length + read > maximumBytes) throw new InvalidDataException("API response capacity exceeded.");
            content.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(content.ToArray());
    }

    private static List<string> Labels(JsonElement issue) => issue.GetProperty("labels").EnumerateArray()
        .Select(label => label.GetProperty("name").GetString() ?? throw new InvalidDataException("Missing label name.")).ToList();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex RepositoryPattern();
}
