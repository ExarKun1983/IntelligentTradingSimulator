namespace IntelligentTradingSimulator.Orchestration;

/// <summary>The required context is independent of Actions checks on the trusted workflow-definition SHA.</summary>
public static class RequiredStatusPolicy
{
    public const string Context = "Agent managed CI";

    internal static List<RestartStatusAcknowledgement> RestartStatusAcknowledgements(
        RepositoryState state, IssueState issue, PrepareRestart restart)
    {
        var ids = restart.AcknowledgedUncertainStatusIntentIds ?? [];
        var uncertain = state.Outbox.Values.Where(effect =>
            effect.IssueNumber == issue.Number && effect.Status == DeliveryStatus.Unknown).ToList();
        if (ids.Count > 16 || ids.Any(string.IsNullOrWhiteSpace) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Count || ids.Count != uncertain.Count ||
            uncertain.Any(effect => !ids.Contains(effect.Id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Restart requires exact acknowledgement of every uncertain status intent.");
        var acknowledgements = new List<RestartStatusAcknowledgement>();
        foreach (var effect in uncertain)
        {
            if (effect.Kind != EffectKind.PublishRequiredStatus || effect.Generation != issue.Generation ||
                effect.RequiredStatus is not { State: "pending", Context: Context } ||
                effect.RequiredStatusBoundary is not null || effect.ExternalId is not null ||
                effect.CompletedAt is not { } completedAt || effect.Sha is not { Length: 40 } sha ||
                !sha.All(Uri.IsHexDigit) || sha == restart.Sha ||
                !issue.Tasks.Values.Any(task => task.Status == TaskStatus.Completed && task.LaunchConfirmed &&
                    task.FinalSha == sha && task.CandidateShaEvidence?.PullRequest == restart.PullRequest &&
                    state.Outbox.Values.Any(launch => launch.Kind == EffectKind.LaunchTask &&
                        launch.IssueNumber == issue.Number && launch.Generation == issue.Generation &&
                        launch.TaskId == task.Id && launch.Status == DeliveryStatus.Succeeded &&
                        launch.ExternalId == task.ExternalId && !string.IsNullOrWhiteSpace(launch.ExternalId))))
                throw new InvalidOperationException("Only unprepared pending status uncertainty on a discarded earlier task head can be acknowledged.");
            var correction = state.Outbox.Values.Where(candidate =>
                candidate.Kind == EffectKind.PublishRequiredStatus && candidate.IssueNumber == issue.Number &&
                candidate.Generation == issue.Generation && candidate.Sha == sha &&
                candidate.Status == DeliveryStatus.Succeeded &&
                candidate.RequiredStatus is { State: "error", Context: Context } &&
                candidate.RequiredStatusBoundary is { StatusId: > 0 } boundary &&
                boundary.PreparedAt > completedAt &&
                candidate.ExternalId == boundary.StatusId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .OrderByDescending(candidate => candidate.RequiredStatusBoundary!.PreparedAt).FirstOrDefault();
            if (correction is null)
                throw new InvalidOperationException("Acknowledged status uncertainty requires a confirmed later corrective error on its exact SHA.");
            acknowledgements.Add(new(effect.Id, correction.Id, sha, correction.RequiredStatusBoundary!.StatusId!.Value));
        }
        return acknowledgements;
    }

    internal static bool RequiresIssueLinkRevocation(RepositoryState state, IssueState issue) =>
        issue.PullRequest is > 0 && HasOutstandingIssueLinkRevocation(state, issue);

    internal static bool HasOutstandingIssueLinkRevocation(RepositoryState state, IssueState issue) =>
        state.Outbox.Values.Any(effect => effect.Kind == EffectKind.LinkIssue &&
            effect.IssueNumber == issue.Number && RequiresIssueLinkRevocation(state, effect));

    /// <summary>A link saved before the revocation marker existed has a null marker. Infer the obligation
    /// from its own recorded generation/SHA and status history rather than treating null as "no obligation",
    /// while never reopening an obligation that was already discharged.</summary>
    internal static bool RequiresIssueLinkRevocation(RepositoryState state, EffectIntent link) =>
        link.IssueLinkRevocationDischargedBy is null &&
        (link.IssueLinkRevocationRequired == true ||
         link.IssueLinkRevocationRequired is null && link.Status is DeliveryStatus.Failed or DeliveryStatus.Unknown &&
            state.Outbox.Values.Any(status => status.Kind == EffectKind.PublishRequiredStatus &&
                status.IssueNumber == link.IssueNumber && status.Generation == link.Generation &&
                status.Sha == link.Sha &&
                status.Status is DeliveryStatus.Succeeded or DeliveryStatus.Executing or DeliveryStatus.Unknown));

    public static RequiredStatusPayload Desired(RepositoryState state, IssueState issue)
    {
        var ci = issue.Ci;
        var acceptedReview = AcceptedReviewMatches(issue);
        var finalApprovalHold = issue.Phase == Phase.HUMAN_REVIEW &&
            issue.HoldReason == "Awaiting human approval." && acceptedReview;
        var terminal = issue.TerminalHistory.LastOrDefault();
        var merged = IsRecordedMerge(issue) && terminal?.Approval is { } approval &&
            terminal.MergedAt is { } mergedAt && approval.PublicationConfirmedAt < mergedAt &&
            FinalApprovalMatches(state, issue, approval) &&
            issue.MergeFeedback is { } feedback && feedback.Generation == issue.Generation &&
            feedback.PullRequest == issue.PullRequest && feedback.Sha == issue.HeadSha &&
            feedback.MergedAt == terminal.MergedAt && feedback.ObservedAt >= feedback.MergedAt &&
            !state.Issues.Values.Any(other => other.Number != issue.Number &&
                (other.HeadSha == issue.HeadSha || other.PullRequest == issue.PullRequest));
        var eligiblePhase = merged || finalApprovalHold || issue.Phase == Phase.REVIEW_PENDING &&
            issue.ActiveAuthorized && !issue.ReactivationRequired && issue.HoldReason is null;
        var verifiedCi = VerifiedCiMatches(state, issue);
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

    internal static FinalApprovalEvidence? CaptureFinalApproval(RepositoryState state, IssueState issue,
        bool requirePublication = true)
    {
        if (issue.Phase != Phase.HUMAN_REVIEW || issue.HoldReason != "Awaiting human approval." ||
            issue.ActiveAuthorized || !issue.ReactivationRequired || issue.CurrentHandoffId is not { } id ||
            issue.Review?.ReviewId is not { } review || issue.Ci?.RunId is not { } run)
            return null;
        var evidence = new FinalApprovalEvidence(id, issue.ActiveTaskId!, review,
            issue.Ci.IntentId, run, issue.Round, issue.HeadSha!)
        {
            PublicationConfirmedAt = issue.Handoffs.GetValueOrDefault(id)?.PublicationConfirmedAt
        };
        return FinalApprovalMatches(state, issue, evidence, requirePublication) ? evidence : null;
    }

    internal static bool FinalApprovalMatches(RepositoryState state, IssueState issue, FinalApprovalEvidence evidence,
        bool requirePublication = true) =>
        VerifiedCiMatches(state, issue) && AcceptedReviewMatches(issue) &&
        issue.Review?.LifecycleState == ReviewLifecycleState.Completed &&
        evidence.HandoffId == issue.CurrentHandoffId && evidence.TaskId == issue.ActiveTaskId &&
        evidence.ReviewId == issue.Review.ReviewId && evidence.Round == issue.Round && evidence.Sha == issue.HeadSha &&
        evidence.CiIntentId == issue.Ci!.IntentId && evidence.CiRunId == issue.Ci.RunId &&
        issue.Handoffs.TryGetValue(evidence.HandoffId, out var handoff) &&
        handoff.Id == evidence.HandoffId && handoff.Generation == issue.Generation &&
        handoff.Reason == "Awaiting human approval." && handoff.TaskId == evidence.TaskId &&
        handoff.ReviewId == evidence.ReviewId && handoff.Round == evidence.Round && handoff.Sha == evidence.Sha &&
        (!requirePublication || evidence.PublicationConfirmedAt is { } confirmedAt && confirmedAt != default &&
            handoff.PublicationConfirmedAt == confirmedAt) &&
        !state.Outbox.Values.Any(effect => effect.IssueNumber == issue.Number &&
            effect.Kind != EffectKind.PublishRequiredStatus &&
            effect.Status is DeliveryStatus.Executing or DeliveryStatus.Unknown);

    private static bool AcceptedReviewMatches(IssueState issue) =>
        issue.Review is { Classification: ReviewClassification.Accepted } review &&
        review.TaskId == issue.ActiveTaskId && review.Sha == issue.HeadSha && review.Round == issue.Round &&
        review.Findings.All(finding => review.Responses.Any(response => response.FindingId == finding.Id &&
            response.Resolved && !string.IsNullOrWhiteSpace(response.Evidence)));

    private static bool VerifiedCiMatches(RepositoryState state, IssueState issue) =>
        !issue.Paused && !issue.ClosurePending && issue.PendingFeedbackEventIds.Count == 0 &&
        issue.Ci is { Green: true, TerminalStopReason: null } ci &&
        ci.Sha == issue.HeadSha && ci.TaskId == issue.ActiveTaskId && ci.Round == issue.Round &&
        issue.Tasks.TryGetValue(ci.TaskId, out var task) && task.Status == TaskStatus.Completed &&
        task.LaunchConfirmed && task.FinalSha == ci.Sha && task.Round == ci.Round &&
        ci.DispatchBoundary is { RunId: { } runId } &&
        ci.RunId == runId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
        state.Outbox.TryGetValue(ci.IntentId, out var dispatch) && dispatch.Kind == EffectKind.RunCi &&
        dispatch.Status == DeliveryStatus.Succeeded && dispatch.ExternalId == ci.RunId &&
        dispatch.IssueNumber == issue.Number && dispatch.Generation == issue.Generation &&
        dispatch.TaskId == ci.TaskId && dispatch.Sha == ci.Sha && dispatch.Round == ci.Round;

    public static bool Matches(RepositoryState state, IssueState issue, EffectIntent intent) =>
        intent.Kind == EffectKind.PublishRequiredStatus && issue.RequiredStatusIntentId == intent.Id &&
        (issue.Generation == intent.Generation && issue.HeadSha == intent.Sha &&
         issue.ActiveTaskId == intent.TaskId && issue.Round == intent.Round &&
         intent.RequiredStatus == ForIntent(state, issue, intent.Id) ||
         MatchesIssueLinkRevocation(state, issue, intent));

    internal static RequiredStatusPayload ForIssueLinkRevocation(RepositoryState state, IssueState issue, string id) =>
        new(Context, "error", "Issue link requires revocation on its recorded tested head.",
            $"https://github.com/{state.Repository}/pull/{issue.PullRequest}#agent-status-{StateJson.Digest(id).ToLowerInvariant()}",
            null, null);

    internal static bool MatchesIssueLinkRevocation(RepositoryState state, IssueState issue, EffectIntent intent) =>
        intent.RequiredStatus?.State == "error" &&
        intent.RequiredStatus == ForIssueLinkRevocation(state, issue, intent.Id) &&
        state.Outbox.Values.Any(link => link.Kind == EffectKind.LinkIssue &&
            link.IssueNumber == issue.Number && link.Generation == intent.Generation && link.Sha == intent.Sha &&
            link.IssueLink is { Version: 1 } payload && payload.PullRequest == issue.PullRequest &&
            (RequiresIssueLinkRevocation(state, link) ||
             // A discharged receipt remains matchable for recovery, but once reactivation has advanced the
             // Issue to a new generation it must not block normal planning from replacing it.
             link.IssueLinkRevocationDischargedBy == intent.Id && issue.Generation == intent.Generation));

    public static RequiredStatusPayload ForIntent(RepositoryState state, IssueState issue, string id)
    {
        var payload = Desired(state, issue);
        return payload with { TargetUrl = payload.TargetUrl + "#agent-status-" + StateJson.Digest(id).ToLowerInvariant() };
    }
}
