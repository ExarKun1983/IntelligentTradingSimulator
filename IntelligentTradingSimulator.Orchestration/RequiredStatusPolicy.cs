namespace IntelligentTradingSimulator.Orchestration;

/// <summary>The required context is independent of Actions checks on the trusted workflow-definition SHA.</summary>
public static class RequiredStatusPolicy
{
    public const string Context = "Agent managed CI";

    public static RequiredStatusPayload Desired(RepositoryState state, IssueState issue)
    {
        var ci = issue.Ci;
        var finalApprovalHold = issue.Phase == Phase.HUMAN_REVIEW &&
            issue.HoldReason == "Awaiting human approval." &&
            issue.Review is { Classification: ReviewClassification.Accepted } review &&
            review.TaskId == issue.ActiveTaskId && review.Sha == issue.HeadSha && review.Round == issue.Round &&
            review.Findings.All(finding => review.Responses.Any(response => response.FindingId == finding.Id &&
                response.Resolved && !string.IsNullOrWhiteSpace(response.Evidence)));
        var eligiblePhase = finalApprovalHold || issue.Phase == Phase.REVIEW_PENDING &&
            issue.ActiveAuthorized && !issue.ReactivationRequired && issue.HoldReason is null;
        var green = eligiblePhase && !issue.Paused && !issue.ClosurePending &&
            issue.PendingFeedbackEventIds.Count == 0 && ci is { Green: true, TerminalStopReason: null } &&
            ci.Sha == issue.HeadSha && ci.TaskId == issue.ActiveTaskId && ci.Round == issue.Round &&
            issue.Tasks.TryGetValue(ci.TaskId, out var task) && task.Status == TaskStatus.Completed &&
            task.LaunchConfirmed && task.FinalSha == ci.Sha && task.Round == ci.Round &&
            ci.DispatchBoundary is { RunId: { } runId } && ci.RunId == runId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            state.Outbox.TryGetValue(ci.IntentId, out var dispatch) && dispatch.Kind == EffectKind.RunCi &&
            dispatch.Status == DeliveryStatus.Succeeded && dispatch.ExternalId == ci.RunId &&
            dispatch.IssueNumber == issue.Number && dispatch.Generation == issue.Generation &&
            dispatch.TaskId == ci.TaskId && dispatch.Sha == ci.Sha && dispatch.Round == ci.Round;
        var stopped = issue.Phase is Phase.BLOCKED or Phase.CLOSED || issue.Paused ||
            issue.ReactivationRequired && !finalApprovalHold || issue.ClosurePending || issue.PendingFeedbackEventIds.Count != 0;
        var result = green ? "success" : stopped ? "error" : ci?.Green == false && ci.Sha == issue.HeadSha ? "failure" : "pending";
        var target = ci?.RunId is { } id && long.TryParse(id, out var numeric) && numeric > 0
            ? $"https://github.com/{state.Repository}/actions/runs/{id}"
            : $"https://github.com/{state.Repository}/pull/{issue.PullRequest}";
        return new(Context, result, green ? "Exact tested head CI passed; human approval required." :
            stopped ? "Automation stopped or feedback pending; not eligible." :
            result == "failure" ? "Exact-head managed CI failed; repair required." :
            "Tracked coding or exact-head CI is not yet complete.", target, ci?.IntentId, ci?.RunId);
    }

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
