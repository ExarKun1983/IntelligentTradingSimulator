namespace IntelligentTradingSimulator.Orchestration;

public sealed record ManagedPushRouteRequest(string Repository, int PullRequest, string EventHeadSha,
    string CurrentHeadSha, bool IsOpen = true, bool SameRepository = true);
public sealed record ManagedPushRouteDecision(bool RunFullCi, string Reason);

/// <summary>Read-only canonical routing; labels, pushes and draft state never prove task completion.</summary>
public static class ManagedPushRouting
{
    public static ManagedPushRouteDecision Decide(RepositoryState? state, ManagedPushRouteRequest request,
        bool routeApproved = false)
    {
        if (!routeApproved) return new(true, "Ordinary CI routing (managed routing disabled).");
        if (state is null || state.SchemaVersion != 1 || state.Generation <= 0 ||
            state.Issues.Count > OrchestrationReducer.MaximumIndexEntries ||
            state.Repository != request.Repository || !request.SameRepository ||
            !request.IsOpen || request.PullRequest <= 0 || request.EventHeadSha is not { Length: 40 } ||
            !request.EventHeadSha.All(Uri.IsHexDigit) || request.EventHeadSha != request.CurrentHeadSha)
            return new(true, "Ordinary CI routing (unverified current-head context).");
        var issues = state.Issues.Values.Where(issue => issue.PullRequest == request.PullRequest).ToList();
        if (issues.Count != 1) return new(true, "Ordinary CI routing (unmanaged or ambiguous mapping).");
        var managed = issues[0];
        if (managed.SchemaVersion != 1 || managed.Generation <= 0 || managed.Number <= 0 || managed.Round <= 0 ||
            !state.Issues.TryGetValue(managed.Number, out var indexed) || !ReferenceEquals(indexed, managed) ||
            state.AdmittedIssue != managed.Number ||
            managed.Phase is not (Phase.CODING or Phase.CI_PENDING or Phase.REVIEW_PENDING or Phase.FIX_PENDING or
                Phase.HUMAN_REVIEW or Phase.BLOCKED or Phase.CLOSED) ||
            managed.ActiveTaskId is not { } taskId || !managed.Tasks.TryGetValue(taskId, out var task) ||
            task.Id != taskId || task.Round != managed.Round)
            return new(true, "Ordinary CI routing (not a canonical owned workflow push).");
        // Canonical head updates can lag this notification. The independently verified live head
        // binds the push; ownership suppresses ordinary CI even while drift/holds are reconciled.
        return new(false, "Canonical owned managed push; only completion-gated CI may test an eligible final head.");
    }
}
