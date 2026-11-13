namespace IntelligentTradingSimulator.Orchestration;

public sealed class PocStateStore(IStateStore inner, string repository, int issueNumber,
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
        if (state.Repository != repository || state.Generation != 1 ||
            state.IsolatedTestManifestDigest != manifestDigest ||
            state.Issues.Keys.Any(number => number != issueNumber) ||
            state.Inbox.Values.Any(entry => entry.Event is not { } message ||
                message.IssueNumber != issueNumber || message.Data is Reactivate) ||
            state.Inbox.Values.Count(entry => entry.Event?.Data is Ready) > 1 ||
            state.AdmittedIssue is { } admitted && admitted != issueNumber ||
            state.Outbox.Values.Any(effect => effect.IssueNumber != issueNumber ||
                effect.Kind is not (EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview or
                    EffectKind.ProjectLabels or EffectKind.ProjectStatus)) ||
            state.Outbox.Values.Count(effect => effect.Kind == EffectKind.LaunchTask) > 2 ||
            state.Outbox.Values.Count(effect => effect.Kind == EffectKind.RunCi) > 2 ||
            state.Outbox.Values.Count(effect => effect.Kind == EffectKind.RequestReview) > 2)
            throw new InvalidDataException("PoC state exceeds its isolated scope or execution limits.");
        if (!state.Issues.TryGetValue(issueNumber, out var issue)) return;
        if (issue.Number != issueNumber || issue.Generation != 1 || issue.SinglePassTest ||
            issue.MaxRepairTasks != 1 || issue.Round > 1 || issue.Reactivations.Count != 0 ||
            issue.Tasks.Count > 2 || issue.Tasks.Values.Count(task => task.Kind == TaskKind.Coding) > 1 ||
            issue.Tasks.Values.Count(task => task.Kind != TaskKind.Coding) > 1 ||
            issue.Tasks.Values.Any(task => task.Kind is TaskKind.CiRepair or TaskKind.FeedbackRepair) ||
            issue.TerminalOutcome == TerminalOutcome.Merged ||
            issue.TerminalHistory.Any(record => record.Outcome == TerminalOutcome.Merged))
            throw new InvalidDataException("PoC Issue exceeds one coding task, one review repair, or the no-merge boundary.");
    }
}
