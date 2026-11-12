namespace IntelligentTradingSimulator.Orchestration;

public static class StatusProjection
{
    public static string Marker(int issue, long generation) => $"<!-- its-agent-state:{issue}:{generation} -->";

    public static string Render(IssueState issue) =>
        $"{Marker(issue.Number, issue.Generation)}\n" +
        $"Agent workflow: **{issue.Phase}**\n\n" +
        $"Generation: {issue.Generation}; round: {issue.Round}.\n" +
        $"PR: {(issue.PullRequest is { } pr ? $"#{pr}" : "unmapped")}; head: `{issue.HeadSha ?? "not captured"}`.\n" +
        $"Task: {(issue.ActiveTaskId is { } task ? issue.Tasks[task].Status.ToString() : "none")}.\n" +
        $"Consecutive CI failures: {issue.ConsecutiveCiFailures}/{issue.CiBudget}; " +
        $"review failures: {issue.ConsecutiveReviewFailures}/{issue.ReviewBudget}.\n" +
        $"Automation held: {issue.ReactivationRequired || issue.Paused}; terminal outcome: {issue.TerminalOutcome}.\n\n" +
        $"Handoff: `{issue.CurrentHandoffId ?? "none"}`.\n\n" +
        "Canonical Git state is authoritative. Human approval is required; no automatic merge.";
}

/// <summary>Projection-only adapter. Unknown/lost writes are reconciled by reads, never repeated creates.</summary>
public sealed class GitHubProjectionAdapter(
    GitHubRepositoryApi api, long projectionActorId, bool allowMutations = false) : IEffectAdapter
{
    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken cancellationToken)
    {
        RequireProjection(intent);
        if (!allowMutations) throw new InvalidOperationException("Projection writes are disabled.");
        await api.VerifyRepositoryAsync(cancellationToken);
        if (intent.Kind == EffectKind.ProjectLabels)
        {
            // Never replace the collection: concurrent human edits to unrelated labels must survive.
            var labels = await api.ListAsync($"issues/{intent.IssueNumber}/labels", cancellationToken);
            if (intent.Labels.Count > 0)
            {
                using var response = await api.WriteAsync(HttpMethod.Post, $"issues/{intent.IssueNumber}/labels",
                    new { labels = intent.Labels }, cancellationToken);
            }
            foreach (var label in labels.Select(label => label.GetProperty("name").GetString()!)
                         .Where(label => OrchestrationReducer.IsProjectionLabel(label) && !intent.Labels.Contains(label)))
            {
                using var removed = await api.WriteAsync(HttpMethod.Delete,
                    $"issues/{intent.IssueNumber}/labels/{Uri.EscapeDataString(label)}", new { }, cancellationToken);
            }
            return await ReconcileAsync(intent, cancellationToken);
        }

        var existing = await FindCommentAsync(intent, cancellationToken);
        if (existing is { } comment && comment.Body == intent.StatusBody)
            return new(DeliveryStatus.Succeeded, comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var result = await api.WriteAsync(existing is null ? HttpMethod.Post : HttpMethod.Patch,
            existing is null ? $"issues/{intent.IssueNumber}/comments" : $"issues/comments/{existing.Value.Id}",
            new { body = intent.StatusBody }, cancellationToken);
        var root = result.RootElement;
        if (root.GetProperty("user").GetProperty("id").GetInt64() != projectionActorId ||
            root.GetProperty("body").GetString() != intent.StatusBody)
            return new(DeliveryStatus.Unknown);
        return new(DeliveryStatus.Succeeded,
            root.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken cancellationToken)
    {
        RequireProjection(intent);
        await api.VerifyRepositoryAsync(cancellationToken);
        if (intent.Kind == EffectKind.ProjectLabels)
        {
            var labels = await api.ListAsync($"issues/{intent.IssueNumber}/labels", cancellationToken);
            var observed = labels.Select(label => label.GetProperty("name").GetString()!)
                .Where(OrchestrationReducer.IsProjectionLabel).OrderBy(label => label, StringComparer.Ordinal);
            return observed.SequenceEqual(intent.Labels.OrderBy(label => label, StringComparer.Ordinal))
                ? new(DeliveryStatus.Succeeded) : new(DeliveryStatus.Unknown);
        }
        var comment = await FindCommentAsync(intent, cancellationToken);
        return comment is { } found && found.Body == intent.StatusBody
            ? new(DeliveryStatus.Succeeded, found.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new(DeliveryStatus.Unknown);
    }

    private async Task<(long Id, string Body)?> FindCommentAsync(EffectIntent intent, CancellationToken token)
    {
        var marker = StatusProjection.Marker(intent.IssueNumber, intent.Generation);
        var comments = await api.ListAsync($"issues/{intent.IssueNumber}/comments", token);
        var candidates = comments.Where(comment =>
            comment.GetProperty("user").GetProperty("id").GetInt64() == projectionActorId &&
            (comment.GetProperty("body").GetString() ?? "").StartsWith(marker + "\n", StringComparison.Ordinal)).ToList();
        if (candidates.Count > 1) throw new InvalidDataException("Multiple owned status comments require reconciliation.");
        if (candidates.Count == 0) return null;
        return (candidates[0].GetProperty("id").GetInt64(), candidates[0].GetProperty("body").GetString()!);
    }

    private void RequireProjection(EffectIntent intent)
    {
        if (projectionActorId <= 0 || intent.IssueNumber <= 0 || intent.Generation <= 0 ||
            intent.Kind is not (EffectKind.ProjectLabels or EffectKind.ProjectStatus) ||
            intent.Kind == EffectKind.ProjectStatus &&
            (intent.StatusBody is null || !intent.StatusBody.StartsWith(
                StatusProjection.Marker(intent.IssueNumber, intent.Generation) + "\n", StringComparison.Ordinal)) ||
            intent.Labels.Any(label => !OrchestrationReducer.IsProjectionLabel(label)))
            throw new InvalidDataException("Only scoped lifecycle projections are supported.");
    }
}
