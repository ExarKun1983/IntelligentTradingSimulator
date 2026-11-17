namespace IntelligentTradingSimulator.Orchestration;

/// <summary>The required context is independent of Actions checks on the trusted workflow-definition SHA.</summary>
public static class RequiredStatusPolicy
{
    public const string Context = "Agent managed CI";

    public static RequiredStatusPayload Desired(RepositoryState state, IssueState issue)
    {
        var ci = issue.Ci;
        var acceptedReview = issue.Review is { Classification: ReviewClassification.Accepted } review &&
            review.TaskId == issue.ActiveTaskId && review.Sha == issue.HeadSha && review.Round == issue.Round &&
            review.Findings.All(finding => review.Responses.Any(response => response.FindingId == finding.Id &&
                response.Resolved && !string.IsNullOrWhiteSpace(response.Evidence)));
        var finalApprovalHold = issue.Phase == Phase.HUMAN_REVIEW &&
            issue.HoldReason == "Awaiting human approval." && acceptedReview;
        var merged = IsRecordedMerge(issue) && acceptedReview &&
            issue.Review?.LifecycleState == ReviewLifecycleState.Completed &&
            !state.Outbox.Values.Any(effect => effect.IssueNumber == issue.Number &&
                effect.Kind != EffectKind.PublishRequiredStatus &&
                effect.Status is DeliveryStatus.Executing or DeliveryStatus.Unknown);
        var eligiblePhase = merged || finalApprovalHold || issue.Phase == Phase.REVIEW_PENDING &&
            issue.ActiveAuthorized && !issue.ReactivationRequired && issue.HoldReason is null;
        var verifiedCi = !issue.Paused && !issue.ClosurePending &&
            issue.PendingFeedbackEventIds.Count == 0 && ci is { Green: true, TerminalStopReason: null } &&
            ci.Sha == issue.HeadSha && ci.TaskId == issue.ActiveTaskId && ci.Round == issue.Round &&
            issue.Tasks.TryGetValue(ci.TaskId, out var task) && task.Status == TaskStatus.Completed &&
            task.LaunchConfirmed && task.FinalSha == ci.Sha && task.Round == ci.Round &&
            ci.DispatchBoundary is { RunId: { } runId } && ci.RunId == runId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            state.Outbox.TryGetValue(ci.IntentId, out var dispatch) && dispatch.Kind == EffectKind.RunCi &&
            dispatch.Status == DeliveryStatus.Succeeded && dispatch.ExternalId == ci.RunId &&
            dispatch.IssueNumber == issue.Number && dispatch.Generation == issue.Generation &&
            dispatch.TaskId == ci.TaskId && dispatch.Sha == ci.Sha && dispatch.Round == ci.Round;
        var green = eligiblePhase && verifiedCi;
        var awaitingClassification = verifiedCi && issue.Phase == Phase.BLOCKED &&
            issue.ReactivationRequired && !issue.ActiveAuthorized &&
            issue.HoldReason == "Review requires human classification." &&
            issue.Review is { Classification: ReviewClassification.NeedsHuman, LifecycleState: ReviewLifecycleState.Completed } pendingReview &&
            pendingReview.TaskId == issue.ActiveTaskId && pendingReview.Sha == issue.HeadSha &&
            pendingReview.Round == issue.Round && issue.CurrentHandoffId is { } handoffId &&
            issue.Handoffs.TryGetValue(handoffId, out var handoff) && handoff.Generation == issue.Generation &&
            handoff.TaskId == issue.ActiveTaskId && handoff.Round == issue.Round && handoff.Sha == issue.HeadSha &&
            handoff.ReviewId == pendingReview.ReviewId && handoff.ClassificationEventId is null;
        var stopped = issue.Phase is Phase.BLOCKED or Phase.CLOSED || issue.Paused ||
            issue.ReactivationRequired && !finalApprovalHold || issue.ClosurePending || issue.PendingFeedbackEventIds.Count != 0;
        var result = green ? "success" : awaitingClassification ? "pending" : stopped ? "error" :
            ci?.Green == false && ci.Sha == issue.HeadSha ? "failure" : "pending";
        var target = ci?.RunId is { } id && long.TryParse(id, out var numeric) && numeric > 0
            ? $"https://github.com/{state.Repository}/actions/runs/{id}"
            : $"https://github.com/{state.Repository}/pull/{issue.PullRequest}";
        return new(Context, result, green && merged ? "Merged; exact tested head CI and accepted review verified." :
            green ? "Exact tested head CI passed; human approval required." :
            awaitingClassification ? "Exact tested head CI passed; review classification required." :
            stopped ? "Automation stopped or feedback pending; not eligible." :
            result == "failure" ? "Exact-head managed CI failed; repair required." :
            "Tracked coding or exact-head CI is not yet complete.", target, ci?.IntentId, ci?.RunId);
    }

    internal static bool IsRecordedMerge(IssueState issue) =>
        issue.Phase == Phase.CLOSED && issue.TerminalOutcome == TerminalOutcome.Merged &&
        issue.HoldReason == "Closed." && !issue.ActiveAuthorized &&
        issue.PullRequest is > 0 && issue.HeadSha is not null &&
        issue.TerminalHistory.LastOrDefault() is { Outcome: TerminalOutcome.Merged } terminal &&
        terminal.Generation == issue.Generation && terminal.PullRequest == issue.PullRequest &&
        terminal.Sha == issue.HeadSha;

    public static bool Matches(RepositoryState state, IssueState issue, EffectIntent intent) =>
        intent.Kind == EffectKind.PublishRequiredStatus && issue.RequiredStatusIntentId == intent.Id &&
        issue.Generation == intent.Generation && issue.HeadSha == intent.Sha &&
        issue.ActiveTaskId == intent.TaskId && issue.Round == intent.Round &&
        intent.RequiredStatus == ForIntent(state, issue, intent.Id);

    public static RequiredStatusPayload ForIntent(RepositoryState state, IssueState issue, string id)
    {
        var payload = Desired(state, issue);
        return payload with { TargetUrl = payload.TargetUrl + "#agent-status-" + StateJson.Digest(id).ToLowerInvariant() };
    }
}
