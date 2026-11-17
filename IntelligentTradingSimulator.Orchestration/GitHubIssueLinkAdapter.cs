using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public static class IssueLinkPolicy
{
    public static string IntentId(IssueState issue) =>
        $"issue-link:v1:{issue.Number}:{issue.Generation}:{issue.PullRequest}:{issue.ActiveTaskId}:{issue.Round}:{issue.HeadSha}";

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
        intent.IssueLink == new IssueLinkPayload(1, issue.PullRequest ?? 0) &&
        Eligible(state, issue);
}

internal sealed class IssueLinkNotStartedException(Exception inner, IssueLinkFailureEvidence evidence) :
    Exception("Issue-link mutation was not sent.", inner)
{
    public IssueLinkFailureEvidence Evidence { get; } = evidence;
}

/// <summary>Adds an atomic closing relationship, never rewrites a PR body or merges a PR.
/// Interrupted mutations are lookup-only, like other durable orchestration effects.</summary>
public sealed class GitHubIssueLinkAdapter(GitHubRepositoryApi api, IStateStore store,
    bool allowMutations = false, TimeProvider? clock = null) : IEffectAdapter
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
    {
        if (!allowMutations) throw new InvalidOperationException("Issue-link writes are disabled.");
        var saved = Saved((await store.LoadAsync(token)).State, intent);
        if (saved.IssueLink is not { Version: 1 } || saved.IssueLinkBoundary is not null)
            throw new InvalidDataException("Prepared Issue-link mutations are lookup-only.");
        Scope scope;
        bool linked;
        var owner = Guid.NewGuid().ToString("N");
        try
        {
            scope = await ReadScopeAsync(intent, token);
            linked = await HasLinkAsync(scope, token);
            if (await ReadScopeAsync(intent, token) != scope)
                throw new InvalidDataException("Closing-reference node identities changed before mutation.");
            await SaveBoundaryAsync(intent, scope, owner, token);
            if (await ReadScopeAsync(intent, token) != scope)
                throw new InvalidDataException("Closing-reference identities changed after preparation.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
            InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            var boundary = Saved((await store.LoadAsync(token)).State, intent).IssueLinkBoundary;
            // Another executor's boundary cannot prove that the shared operation was not sent.
            if (boundary is not null && boundary.OwnerId != owner) throw;
            throw new IssueLinkNotStartedException(exception, new(boundary));
        }
        if (linked) return new(DeliveryStatus.Succeeded, scope.PullId);
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
        var confirmed = await ReadScopeAsync(intent, token);
        if (confirmed != scope || !await HasLinkAsync(scope, token))
            throw new InvalidDataException("Closing reference was not confirmed on the mapped PR.");
        return new(DeliveryStatus.Succeeded, scope.PullId);
    }

    public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token)
    {
        var saved = Saved((await store.LoadAsync(token)).State, intent);
        if (saved.IssueLinkBoundary is not { } boundary)
            return saved.IssueLink is { Version: 1 }
                ? new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.IssueLinkNotStarted,
                    IssueLinkFailure: new(null))
                : new(DeliveryStatus.Unknown);
        if (boundary.RepositoryId != api.RepositoryId || boundary.Issue != saved.IssueNumber ||
            saved.IssueLink is not { Version: 1 } payload || payload.PullRequest != boundary.PullRequest ||
            string.IsNullOrWhiteSpace(boundary.IssueNodeId) || string.IsNullOrWhiteSpace(boundary.PullNodeId) ||
            string.IsNullOrWhiteSpace(boundary.OwnerId) || boundary.PreparedAt == default)
            throw new InvalidDataException("Invalid saved Issue-link recovery boundary.");
        await api.VerifyRepositoryAsync(token);
        var scope = new Scope(boundary.IssueNodeId, boundary.PullNodeId, boundary.PullRequest);
        return await HasLinkAsync(scope, token, recovering: true)
            ? new(DeliveryStatus.Succeeded, scope.PullId) : new(DeliveryStatus.Unknown);
    }

    private EffectIntent Saved(RepositoryState state, EffectIntent intent)
    {
        if (state.Repository != api.Repository || intent.Kind != EffectKind.LinkIssue ||
            !state.Outbox.TryGetValue(intent.Id, out var saved) || saved.Digest != intent.Digest ||
            saved.Kind != intent.Kind || saved.IssueNumber != intent.IssueNumber || saved.Generation != intent.Generation ||
            saved.TaskId != intent.TaskId || saved.Round != intent.Round || saved.Sha != intent.Sha ||
            saved.IssueLink != intent.IssueLink ||
            saved.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown))
            throw new InvalidDataException("Closing reference is not a canonical in-flight intent.");
        return saved;
    }

    private async Task<Scope> ReadScopeAsync(EffectIntent intent, CancellationToken token)
    {
        var state = (await store.LoadAsync(token)).State;
        if (Saved(state, intent).Status != DeliveryStatus.Executing ||
            !state.Issues.TryGetValue(intent.IssueNumber, out var issue) ||
            !IssueLinkPolicy.Matches(state, issue, intent))
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
            item.GetProperty("state").GetString() != "open" || pr.GetProperty("state").GetString() != "open")
            throw new InvalidDataException("Closing-reference live mapping, default branch or head changed.");
        var issueId = item.GetProperty("node_id").GetString();
        var pullId = pr.GetProperty("node_id").GetString();
        if (string.IsNullOrWhiteSpace(issueId) || string.IsNullOrWhiteSpace(pullId))
            throw new InvalidDataException("Closing-reference node identities are unavailable.");
        state = (await store.LoadAsync(token)).State;
        if (!IssueLinkPolicy.Matches(state, state.Issues[intent.IssueNumber], intent))
            throw new InvalidDataException("Closing-reference eligibility changed during preflight.");
        return new(issueId, pullId, issue.PullRequest!.Value);
    }

    private async Task SaveBoundaryAsync(EffectIntent intent, Scope scope, string owner, CancellationToken token)
    {
        var boundary = new IssueLinkBoundary(api.RepositoryId, intent.IssueNumber, scope.PullRequest,
            scope.IssueId, scope.PullId, clock.GetUtcNow(), owner);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await store.LoadAsync(token);
            Saved(snapshot.State, intent);
            var next = new OrchestrationReducer().RecordIssueLinkBoundary(snapshot.State, intent.Id, boundary);
            if (await store.TrySaveAsync(snapshot, next, token)) return;
        }
        throw new InvalidOperationException("Issue-link boundary publication conflict; mutation was not sent.");
    }

    private async Task<bool> HasLinkAsync(Scope scope, CancellationToken token, bool recovering = false)
    {
        const string query = """
            query($pull: ID!) {
              node(id: $pull) {
                id
                ... on PullRequest {
                  number
                  repository { nameWithOwner }
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
        if (node.GetProperty("id").GetString() != scope.PullId ||
            node.GetProperty("number").GetInt32() != scope.PullRequest ||
            node.GetProperty("repository").GetProperty("nameWithOwner").GetString() != api.Repository)
            throw new InvalidDataException("Closing-reference lookup returned a different PR.");
        var references = node.GetProperty("closingIssuesReferences");
        var nodes = references.GetProperty("nodes");
        if (recovering && nodes.EnumerateArray().Any(item => item.GetProperty("id").GetString() == scope.IssueId))
            return true;
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

    private sealed record Scope(string IssueId, string PullId, int PullRequest);
}
