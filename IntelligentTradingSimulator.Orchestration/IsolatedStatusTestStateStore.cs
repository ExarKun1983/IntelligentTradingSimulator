namespace IntelligentTradingSimulator.Orchestration;

public sealed class IsolatedStatusTestStateStore(IStateStore inner, string repository, int issueNumber,
    string manifestDigest) : IStateStore
{
    public async Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await inner.LoadAsync(cancellationToken);
        Validate(snapshot.State);
        return snapshot;
    }

    public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState state, CancellationToken cancellationToken)
    {
        Validate(expected.State);
        Validate(state);
        return inner.TrySaveAsync(expected, state, cancellationToken);
    }

    private void Validate(RepositoryState state)
    {
        var issueEffects = state.Outbox.Values.Where(effect => effect.IssueNumber == issueNumber).ToList();
        var statuses = issueEffects.Where(effect => effect.Kind == EffectKind.PublishRequiredStatus)
            .OrderBy(effect => effect.Id, StringComparer.Ordinal).ToList();
        var statusWrites = statuses.Where(effect => effect.RequiredStatusBoundary is not null ||
            effect.Status is DeliveryStatus.Executing or DeliveryStatus.Unknown or DeliveryStatus.Succeeded or DeliveryStatus.Failed)
            .ToList();
        if (state.Repository != repository || state.IsolatedTestManifestDigest != manifestDigest ||
            state.Issues.Keys.Any(number => number != issueNumber) ||
            state.Outbox.Values.Any(effect => effect.IssueNumber != issueNumber) ||
            state.Inbox.Values.Any(entry => entry.Event is not { } message || message.IssueNumber != issueNumber) ||
            state.AdmittedIssue is { } admitted && admitted != issueNumber ||
            issueEffects.Any(effect => effect.Kind is not (EffectKind.LaunchTask or EffectKind.RunCi or
                EffectKind.PublishRequiredStatus or EffectKind.ProjectLabels or EffectKind.ProjectStatus)) ||
            issueEffects.Count(effect => effect.Kind == EffectKind.LaunchTask) > 1 ||
            issueEffects.Count(effect => effect.Kind == EffectKind.RunCi) > 1 ||
            statuses.Count > 8 ||
            statuses.Any(effect => effect.RequiredStatus?.State is not ("pending" or "success" or "failure" or "error")) ||
            statusWrites.Count > 2 ||
            statusWrites.Any(effect => effect.RequiredStatus?.State is not ("success" or "error")) ||
            statusWrites.Count > 0 && statusWrites[0].RequiredStatus?.State != "success" ||
            statusWrites.Count == 2 && (statusWrites[0].Status != DeliveryStatus.Succeeded ||
                statusWrites[0].RequiredStatus?.State != "success" || statusWrites[1].RequiredStatus?.State != "error"))
            throw new InvalidDataException("Isolated canonical state exceeds the manifest or effect-count limits.");
        if (!state.Issues.TryGetValue(issueNumber, out var issue)) return;
        if (!issue.SinglePassTest || issue.Tasks.Count > 1 || issue.Round > 1 ||
            issue.Tasks.Values.Any(task => task.Kind != TaskKind.Coding) ||
            issue.Review is not null || issue.TerminalOutcome == TerminalOutcome.Merged ||
            issue.TerminalHistory.Any(record => record.Outcome == TerminalOutcome.Merged))
            throw new InvalidDataException("Isolated Issue history exceeds the one-task, zero-review, no-merge limits.");
    }
}
