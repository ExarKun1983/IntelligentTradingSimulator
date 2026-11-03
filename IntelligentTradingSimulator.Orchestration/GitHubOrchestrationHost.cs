using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Read-only production composition. Activation and service-completion adapters are not implied by API access.</summary>
public sealed class GitHubOrchestrationHost(GitHubRepositoryApi api, IStateStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<WorkerResult> PreviewAsync(JsonElement trustedActionsPayload, string eventName, string runId,
        long repositoryGeneration, long issueGeneration, DateTimeOffset occurredAt,
        HashSet<long> projectionActorIds, CancellationToken token = default)
    {
        var snapshot = await store.LoadAsync(token);
        if (snapshot.State.Generation != repositoryGeneration ||
            snapshot.State.Issues.TryGetValue(IssueNumber(trustedActionsPayload, eventName), out var issue) &&
            issue.Generation != issueGeneration)
            throw new InvalidDataException("Expected workflow generation does not match canonical state.");
        var evidence = await api.ReadIngressEvidenceAsync(trustedActionsPayload, eventName, runId, projectionActorIds, token);
        var message = GitHubActionsIngress.PrepareEvent(trustedActionsPayload, eventName, runId, evidence,
            repositoryGeneration, issueGeneration, occurredAt);
        var reducer = new OrchestrationReducer(new VerifiedCommand(message));
        return await new OrchestrationWorker(reducer, clock).RunAsync(new SnapshotStore(snapshot),
            new Source(message), new Observations(api, clock), new DisabledEffects(),
            cancellationToken: token);
    }

    private static int IssueNumber(JsonElement payload, string eventName) => eventName switch
    {
        "issues" => payload.GetProperty("issue").GetProperty("number").GetInt32(),
        "workflow_dispatch" => int.Parse(payload.GetProperty("inputs").GetProperty("issue").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("Unsupported Actions event.")
    };

    private sealed class VerifiedCommand(OrchestrationEvent? command) : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => command is not null && message.Digest == command.Digest;
    }

    private sealed class SnapshotStore(StateSnapshot snapshot) : IStateStore
    {
        public Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StateSnapshot(snapshot.Revision, StateJson.Clone(snapshot.State)));
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState state, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preview cannot publish canonical state.");
    }

    private sealed class Source(OrchestrationEvent? message) : IWorkerEventSource
    {
        public Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerEventBatch(message is null ? [] : [message]));
    }

    private sealed class Observations(GitHubRepositoryApi api, TimeProvider clock) : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken cancellationToken) =>
            api.ObserveAsync(state, clock.GetUtcNow(), cancellationToken);
    }

    private sealed class DisabledEffects : IEffectAdapter
    {
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Live orchestration is disabled.");
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Live orchestration is disabled.");
    }
}
