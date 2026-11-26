using System.Net;

namespace IntelligentTradingSimulator.Orchestration;

public static class StatusProjection
{
    public static string Marker(int issue, long generation) => $"<!-- its-agent-state:{issue}:{generation} -->";

    public static string Render(IssueState issue, bool isolatedProfile = false) =>
        $"{Marker(issue.Number, issue.Generation)}\n" +
        $"Agent workflow: **{issue.Phase}**\n\n" +
        $"Generation: {issue.Generation}; round: {issue.Round}.\n" +
        $"PR: {(issue.PullRequest is { } pr ? $"#{pr}" : "unmapped")}; head: {Value(issue.HeadSha)}.\n" +
        $"Task: {(issue.ActiveTaskId is { } task ? issue.Tasks[task].Status.ToString() : "none")}.\n" +
        $"Consecutive CI failures: {issue.ConsecutiveCiFailures}/{issue.CiBudget}; " +
        $"review failures: {issue.ConsecutiveReviewFailures}/{issue.ReviewBudget}.\n" +
        $"Automation held: {issue.ReactivationRequired || issue.Paused}; terminal outcome: {issue.TerminalOutcome}.\n\n" +
        $"Handoff: {Value(issue.CurrentHandoffId)}.\n\n" +
        (OrchestrationReducer.AwaitingRestartReadiness(issue)
            ? "Restart prepared; waiting for the maintainer to add `agent:ready`. No task has been started.\n\n" : "") +
        ClassificationSummary(issue, isolatedProfile) +
        ClosingLinkGuidance(issue) +
        "Canonical Git state is authoritative. Human approval is required; no automatic merge.";

    private static string ClosingLinkGuidance(IssueState issue) =>
        issue.Phase == Phase.HUMAN_REVIEW && issue.Number > 0 && issue.PullRequest is > 0
            ? "### Issue closing link\n\n" +
              $"A separate durable projection links live PR #{issue.PullRequest} to Issue #{issue.Number} " +
              "after checking its unique mapping, exact head and the repository's default branch. " +
              "It preserves the PR description and existing links; conflicting links require reconciliation. " +
              "This snapshot does not confirm delivery. Before merging, verify the Issue appears in Development. " +
              "If manual reconciliation is needed, add " +
              $"`Closes #{issue.Number}` to the PR description, or link the Issue manually in Development. " +
              "Inspect existing links first; the Issue closes only after the human merges the PR.\n\n"
            : "";

    private static string ClassificationSummary(IssueState issue, bool isolatedProfile)
    {
        if (issue.Phase != Phase.BLOCKED || issue.HoldReason != "Review requires human classification." ||
            issue.Review is not { Classification: ReviewClassification.NeedsHuman } review)
            return "";

        issue.Handoffs.TryGetValue(issue.CurrentHandoffId ?? "", out var handoff);
        issue.Tasks.TryGetValue(issue.ActiveTaskId ?? "", out var task);
        var ci = issue.Ci;
        var ciResult = ci?.Green switch { true => "passed", false => "failed", null => "not available" };

        return "### Review classification required\n\n" +
            "The following is a canonical snapshot, not a live check or merge approval. " +
            "Compare all identities with the live open PR and current state before deciding.\n\n" +
            $"- Issue: #{issue.Number}; issue generation: {issue.Generation}; PR: " +
            $"{(issue.PullRequest is { } pr ? $"#{pr}" : "unmapped")}; current head: {Value(issue.HeadSha)}.\n" +
            $"- Active task: {Value(issue.ActiveTaskId)}; status: {task?.Status.ToString() ?? "not captured"}; " +
            $"round: {task?.Round.ToString() ?? "not captured"}; final SHA: {Value(task?.FinalSha)}.\n" +
            $"- Managed CI (recorded): {ciResult}; run: {Value(ci?.RunId)}; task: {Value(ci?.TaskId)}; " +
            $"round: {ci?.Round.ToString() ?? "not captured"}; SHA: {Value(ci?.Sha)}.\n" +
            $"- Review: {review.LifecycleState}; classification: {review.Classification}; " +
            $"ID: {Value(review.ReviewId)}; task: {Value(review.TaskId)}; round: {review.Round}; SHA: {Value(review.Sha)}.\n" +
            $"- Handoff: {Value(issue.CurrentHandoffId)}; recorded generation: {handoff?.Generation.ToString() ?? "not captured"}; " +
            $"task: {Value(handoff?.TaskId)}; review: {Value(handoff?.ReviewId)}; " +
            $"round: {handoff?.Round.ToString() ?? "not captured"}; SHA: {Value(handoff?.Sha)}.\n\n" +
            "After inspecting the review and any findings, an authorized human may submit one " +
            "`classify-review` decision (`accepted`, `blocking`, or `needs-human`) with the exact handoff, " +
            "round, PR, SHA, issue generation, current repository generation and a reason. " +
            "Supply findings and responses where required; an empty review is not automatic acceptance. " +
            "Classification and the final merge decision are separate.\n\n" +
            ClassificationCommand(issue, review, handoff, isolatedProfile);
    }

    private static string ClassificationCommand(IssueState issue, ReviewIdentity review, HumanHandoff? handoff,
        bool isolatedProfile)
    {
        if (isolatedProfile)
            return "No dispatch template is provided for an isolated profile. Follow that profile's " +
                "documented classification procedure and verify its state reference and workflow.\n\n";

        var sha = issue.HeadSha;
        var id = issue.CurrentHandoffId;
        if (issue.Paused || issue.ClosurePending || issue.PendingFeedbackEventIds.Count != 0 ||
            !issue.ReactivationRequired || issue.ActiveAuthorized || issue.Number <= 0 || issue.Generation <= 0 ||
            issue.Round <= 0 || issue.PullRequest is not > 0 ||
            sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit) ||
            id is not { Length: > 0 and <= 256 } ||
            !id.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.') ||
            handoff is null || handoff.Id != id || handoff.Generation != issue.Generation ||
            handoff.ClassificationEventId is not null || handoff.Round != issue.Round || handoff.Sha != sha ||
            handoff.TaskId != issue.ActiveTaskId || handoff.ReviewId != review.ReviewId ||
            string.IsNullOrEmpty(issue.ActiveTaskId) || string.IsNullOrEmpty(review.ReviewId) ||
            review.TaskId != issue.ActiveTaskId || review.Round != issue.Round || review.Sha != sha ||
            review.LifecycleState != ReviewLifecycleState.Completed ||
            !issue.Tasks.TryGetValue(issue.ActiveTaskId, out var task) ||
            task.Status != TaskStatus.Completed || task.Round != issue.Round || task.FinalSha != sha ||
            issue.Ci is not { Green: true, TerminalStopReason: null } ci ||
            ci.TaskId != issue.ActiveTaskId || ci.Round != issue.Round || ci.Sha != sha)
            return "The recorded handoff is incomplete or inconsistent; no dispatch template is provided.\n\n";

        return "For Bash, after verifying the snapshot and live PR, set `REPO`, " +
            "`REPOSITORY_GENERATION`, `CLASSIFICATION` and `REASON` explicitly. " +
            "Set `FINDINGS` and `RESPONSES` to the required JSON arrays when applicable. " +
            "This template does not verify current state or submit a decision by itself:\n\n" +
            "```bash\n" +
            ": \"${REPO:?Set REPO}\" \"${REPOSITORY_GENERATION:?Set REPOSITORY_GENERATION}\" " +
            "\"${CLASSIFICATION:?Set CLASSIFICATION}\" \"${REASON:?Set REASON}\" && \\\n" +
            "gh workflow run agent-state-machine.yml --repo \"$REPO\" --ref master \\\n" +
            $"  -f issue={issue.Number} -f operation=classify-review -f issue_generation={issue.Generation} \\\n" +
            "  -f \"repository_generation=$REPOSITORY_GENERATION\" \\\n" +
            $"  -f handoff={id} -f round={issue.Round} -f pull={issue.PullRequest} -f sha={sha} \\\n" +
            "  -f \"classification=$CLASSIFICATION\" -f \"reason=$REASON\" \\\n" +
            "  -f \"findings=${FINDINGS:-}\" -f \"responses=${RESPONSES:-}\"\n" +
            "```\n\n";
    }

    private static string Value(string? value) =>
        value is null ? "not captured" : $"<code>{WebUtility.HtmlEncode(value)}</code>";
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
