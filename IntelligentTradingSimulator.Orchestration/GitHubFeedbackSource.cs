using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Current authorized human comments, not a lossless edited/deleted comment history.</summary>
public sealed class GitHubFeedbackSource(GitHubRepositoryApi api, long? projectionActorId = null)
{
    public async Task<IReadOnlyList<OrchestrationEvent>> ReadAsync(RepositoryState state, CancellationToken token)
    {
        if (state.Repository != api.Repository) throw new InvalidDataException("Feedback repository mismatch.");
        await api.VerifyRepositoryAsync(token);
        var events = new List<OrchestrationEvent>();
        foreach (var issue in state.Issues.Values)
        {
            var since = issue.FeedbackSince ?? issue.ReadyAt;
            if (since is null || issue.Phase == Phase.CLOSED) continue;
            var targets = new[] { issue.Number, issue.PullRequest ?? issue.Number }.Distinct();
            foreach (var target in targets)
            {
                var comments = await api.ListAsync($"issues/{target}/comments", token);
                foreach (var comment in comments)
                {
                    if (comment.GetProperty("user").GetProperty("type").GetString() == "Bot") continue;
                    var actor = UpstreamIdentity.RestUser(comment, ActorKind.Human);
                    var updated = comment.GetProperty("updated_at").GetDateTimeOffset();
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
            }
        }
        return events.OrderBy(message => message.OccurredAt).ThenBy(message => message.Id, StringComparer.Ordinal).ToList();
    }
}
