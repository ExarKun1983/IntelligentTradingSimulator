using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Current authorized human comments, not a lossless edited/deleted comment history.</summary>
public sealed class GitHubFeedbackSource(GitHubRepositoryApi api, long? projectionActorId = null,
    TimeProvider? clock = null)
{
    public async Task<IReadOnlyList<OrchestrationEvent>> ReadAsync(RepositoryState state, CancellationToken token,
        int? excludedIssueNumber = null)
    {
        if (state.Repository != api.Repository) throw new InvalidDataException("Feedback repository mismatch.");
        await api.VerifyRepositoryAsync(token);
        var events = new List<OrchestrationEvent>();
        foreach (var issue in state.Issues.Values)
        {
            if (issue.Number == excludedIssueNumber) continue;
            var since = issue.FeedbackSince ?? issue.ReadyAt;
            if (since is null) continue;
            var terminal = issue.TerminalHistory.LastOrDefault();
            if (issue.Phase == Phase.CLOSED &&
                (terminal?.Approval is null ||
                 issue.TerminalOutcome == TerminalOutcome.Merged && issue.MergeFeedback is { } sealedFeedback &&
                 sealedFeedback.Generation == issue.Generation && sealedFeedback.PullRequest == issue.PullRequest &&
                 sealedFeedback.Sha == issue.HeadSha && sealedFeedback.MergedAt == terminal.MergedAt))
                continue;
            DateTimeOffset? mergedAt = null;
            var collected = new List<(int Target, JsonElement Comment)>();
            foreach (var target in new[] { issue.Number, issue.PullRequest ?? issue.Number }.Distinct())
            {
                var comments = await api.ListAsync($"issues/{target}/comments", token);
                collected.AddRange(comments.Select(comment => (target, comment)));
            }
            if (issue.PullRequest is { } prNumber && issue.Phase is Phase.HUMAN_REVIEW or Phase.CLOSED)
            {
                using var pull = await api.GetAsync($"pulls/{prNumber}", token);
                var pr = pull.RootElement;
                if (pr.GetProperty("number").GetInt32() != prNumber ||
                    pr.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId)
                    throw new InvalidDataException("Feedback PR repository mapping changed.");
                if (pr.GetProperty("merged").GetBoolean() &&
                    pr.GetProperty("head").GetProperty("sha").GetString() == issue.HeadSha)
                    mergedAt = pr.GetProperty("merged_at").GetDateTimeOffset();
            }
            foreach (var (target, comment) in collected)
            {
                if (comment.GetProperty("user").GetProperty("type").GetString() == "Bot") continue;
                var actor = UpstreamIdentity.RestUser(comment, ActorKind.Human);
                var updated = comment.GetProperty("updated_at").GetDateTimeOffset();
                if (mergedAt is { } boundary && comment.GetProperty("created_at").GetDateTimeOffset() > boundary)
                    continue;
                var id = comment.GetProperty("id").GetInt64();
                var body = comment.GetProperty("body").GetString();
                if (actor.Id == projectionActorId && body is not null &&
                    body.StartsWith(StatusProjection.Marker(issue.Number, issue.Generation) + "\n", StringComparison.Ordinal))
                    continue;
                if (id <= 0) throw new InvalidDataException("Invalid feedback identity.");
                var eventId = $"feedback:{api.RepositoryId}:{issue.Generation}:{target}:{id}:{updated:O}";
                if (updated < since || state.Inbox.ContainsKey(eventId) || string.IsNullOrWhiteSpace(body)) continue;
                using var permission = await api.GetAsync(
                    $"collaborators/{Uri.EscapeDataString(actor.Login)}/permission", token);
                var user = permission.RootElement.GetProperty("user");
                if (user.GetProperty("id").GetInt64() != actor.Id ||
                    user.GetProperty("login").GetString() != actor.Login || user.GetProperty("type").GetString() != "User")
                    throw new InvalidDataException("Feedback permission identity mismatch.");
                if (permission.RootElement.GetProperty("permission").GetString() is not ("admin" or "maintain" or "write"))
                    continue;
                events.Add(new(eventId, api.Repository, issue.Number, state.Generation, issue.Generation,
                    actor, updated, new HumanFeedback(body)));
                if (events.Count > OrchestrationReducer.MaximumIndexEntries)
                    throw new InvalidDataException("Feedback capacity exceeded.");
            }
            if (mergedAt is { } at)
            {
                var observedAt = (clock ?? TimeProvider.System).GetUtcNow();
                if (observedAt < at) throw new InvalidDataException("Feedback merge boundary is in the future.");
                var id = $"merge-feedback:v2:{api.RepositoryId}:{issue.Number}:{issue.Generation}:{issue.PullRequest}:{issue.HeadSha}:{at:O}";
                if (!state.Inbox.ContainsKey(id))
                    events.Add(new(id, api.Repository, issue.Number, state.Generation, issue.Generation,
                        new("github-feedback-api", ActorKind.TaskService), observedAt,
                        new MergeFeedbackObserved(issue.PullRequest!.Value, issue.HeadSha!, at, observedAt)));
            }
        }
        return events.OrderBy(message => message.OccurredAt).ThenBy(message => message.Id, StringComparer.Ordinal).ToList();
    }
}
