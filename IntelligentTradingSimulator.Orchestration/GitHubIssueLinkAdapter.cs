using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public static class IssueLinkPolicy
{
    public static string IntentId(IssueState issue) =>
        $"issue-link:{issue.Number}:{issue.Generation}:{issue.PullRequest}:{issue.ActiveTaskId}:{issue.Round}:{issue.HeadSha}";

    public static bool Eligible(RepositoryState state, IssueState issue) =>
        state.IsolatedTestManifestDigest is null && issue.Phase == Phase.HUMAN_REVIEW &&
        issue.ReactivationRequired && !issue.ActiveAuthorized &&
        issue.Review?.LifecycleState == ReviewLifecycleState.Completed &&
        issue.PullRequest is > 0 && RequiredStatusPolicy.Desired(state, issue).State == "success" &&
        !state.Issues.Values.Any(other => other.Number != issue.Number && other.PullRequest == issue.PullRequest);

    public static bool Matches(RepositoryState state, IssueState issue, EffectIntent intent) =>
        intent.Kind == EffectKind.LinkIssue && intent.Id == IntentId(issue) &&
        intent.IssueNumber == issue.Number && intent.Generation == issue.Generation &&
        intent.TaskId == issue.ActiveTaskId && intent.Round == issue.Round && intent.Sha == issue.HeadSha &&
        Eligible(state, issue);
}

/// <summary>Adds an atomic closing relationship, never rewrites a PR body or merges a PR.
/// Interrupted mutations are lookup-only, like other durable orchestration effects.</summary>
public sealed class GitHubIssueLinkAdapter(GitHubRepositoryApi api, IStateStore store,
    bool allowMutations = false) : IEffectAdapter
{
    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
    {
        if (!allowMutations) throw new InvalidOperationException("Issue-link writes are disabled.");
        var scope = await ReadScopeAsync(intent, true, token);
        if (await HasLinkAsync(scope, token)) return new(DeliveryStatus.Succeeded, scope.PullId);
        if (await ReadScopeAsync(intent, true, token) != scope)
            throw new InvalidDataException("Closing-reference node identities changed before mutation.");
        const string mutation = """
            mutation($issue: ID!, $pull: ID!) {
              addCloseIssueReferences(input: {issueId: $issue, pullRequestIds: [$pull]}) {
                issue { id }
              }
            }
            """;
        using var response = await api.PostGlobalAsync("graphql",
            new { query = mutation, variables = new { issue = scope.IssueId, pull = scope.PullId } }, token);
        var data = Data(response);
        if (data.GetProperty("addCloseIssueReferences").GetProperty("issue").GetProperty("id").GetString() != scope.IssueId)
            throw new InvalidDataException("Closing-reference acknowledgement has a different Issue identity.");
        var confirmed = await ReadScopeAsync(intent, true, token);
        if (confirmed != scope || !await HasLinkAsync(scope, token))
            throw new InvalidDataException("Closing reference was not confirmed on the mapped PR.");
        return new(DeliveryStatus.Succeeded, scope.PullId);
    }

    public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token)
    {
        var scope = await ReadScopeAsync(intent, false, token);
        return await HasLinkAsync(scope, token)
            ? new(DeliveryStatus.Succeeded, scope.PullId) : new(DeliveryStatus.Unknown);
    }

    private async Task<Scope> ReadScopeAsync(EffectIntent intent, bool executing, CancellationToken token)
    {
        var state = (await store.LoadAsync(token)).State;
        if (state.Repository != api.Repository || intent.Kind != EffectKind.LinkIssue ||
            !state.Outbox.TryGetValue(intent.Id, out var saved) || saved.Digest != intent.Digest ||
            saved.Kind != intent.Kind || saved.IssueNumber != intent.IssueNumber || saved.Generation != intent.Generation ||
            saved.TaskId != intent.TaskId || saved.Round != intent.Round || saved.Sha != intent.Sha ||
            saved.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown) ||
            executing && saved.Status != DeliveryStatus.Executing ||
            !state.Issues.TryGetValue(intent.IssueNumber, out var issue) || issue.Generation != intent.Generation ||
            intent.Id != IssueLinkPolicy.IntentId(issue) ||
            executing && !IssueLinkPolicy.Matches(state, issue, intent))
            throw new InvalidDataException("Closing reference is not a canonical eligible intent.");
        using var repository = await api.GetAsync("", token);
        var repo = repository.RootElement;
        if (repo.GetProperty("id").GetInt64() != api.RepositoryId ||
            repo.GetProperty("full_name").GetString() != api.Repository ||
            string.IsNullOrWhiteSpace(repo.GetProperty("default_branch").GetString()))
            throw new InvalidDataException("Closing-reference repository identity is invalid.");
        using var liveIssue = await api.GetAsync($"issues/{issue.Number}", token);
        using var pull = await api.GetAsync($"pulls/{issue.PullRequest}", token);
        var pr = pull.RootElement;
        var item = liveIssue.RootElement;
        if (item.GetProperty("number").GetInt32() != issue.Number || item.TryGetProperty("pull_request", out _) ||
            pr.GetProperty("number").GetInt32() != issue.PullRequest ||
            pr.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pr.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pr.GetProperty("base").GetProperty("ref").GetString() != repo.GetProperty("default_branch").GetString() ||
            pr.GetProperty("head").GetProperty("sha").GetString() != intent.Sha ||
            executing && (item.GetProperty("state").GetString() != "open" || pr.GetProperty("state").GetString() != "open"))
            throw new InvalidDataException("Closing-reference live mapping, default branch or head changed.");
        var issueId = item.GetProperty("node_id").GetString();
        var pullId = pr.GetProperty("node_id").GetString();
        if (string.IsNullOrWhiteSpace(issueId) || string.IsNullOrWhiteSpace(pullId))
            throw new InvalidDataException("Closing-reference node identities are unavailable.");
        state = (await store.LoadAsync(token)).State;
        if (executing && !IssueLinkPolicy.Matches(state, state.Issues[intent.IssueNumber], intent))
            throw new InvalidDataException("Closing-reference eligibility changed during preflight.");
        return new(issueId, pullId);
    }

    private async Task<bool> HasLinkAsync(Scope scope, CancellationToken token)
    {
        const string query = """
            query($pull: ID!) {
              node(id: $pull) {
                id
                ... on PullRequest {
                  closingIssuesReferences(first: 100) {
                    nodes { id }
                    pageInfo { hasNextPage }
                  }
                }
              }
            }
            """;
        using var response = await api.PostGlobalAsync("graphql", new { query, variables = new { pull = scope.PullId } }, token);
        var node = Data(response).GetProperty("node");
        if (node.GetProperty("id").GetString() != scope.PullId)
            throw new InvalidDataException("Closing-reference lookup returned a different PR.");
        var references = node.GetProperty("closingIssuesReferences");
        var nodes = references.GetProperty("nodes");
        if (references.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean() ||
            nodes.GetArrayLength() > 1 || nodes.GetArrayLength() == 1 && nodes[0].GetProperty("id").GetString() != scope.IssueId)
            throw new InvalidDataException("Existing closing references are ambiguous or address another Issue.");
        return nodes.GetArrayLength() == 1;
    }

    private static JsonElement Data(JsonDocument response)
    {
        var root = response.RootElement;
        if (root.TryGetProperty("errors", out var errors) &&
            (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
            throw new InvalidDataException("Closing-reference GraphQL operation failed or returned incomplete data.");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Closing-reference GraphQL data is unavailable.");
        return data;
    }

    private sealed record Scope(string IssueId, string PullId);
}
