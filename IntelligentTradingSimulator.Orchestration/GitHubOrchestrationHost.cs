using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Production composition; read-only preview remains the only exposed CLI mode.</summary>
public sealed class GitHubOrchestrationHost(GitHubRepositoryApi api, IStateStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<WorkerResult> ReconcileAsync(JsonElement trustedActionsPayload, string eventName, string runId,
        long repositoryGeneration, long issueGeneration, DateTimeOffset occurredAt,
        HashSet<long> projectionActorIds, long projectionActorId, string baseBranch, long taskCreatorId,
        GitHubExecutionApproval? approval = null, WorkerOptions? options = null,
        ITaskFinalHeadSource? finalHeadSource = null, CancellationToken token = default)
    {
        if (approval?.AllowsTasks != true) throw new InvalidOperationException("Live activation prerequisites are not verified.");
        var snapshot = await store.LoadAsync(token);
        var evidence = await api.ReadIngressEvidenceAsync(trustedActionsPayload, eventName, runId, projectionActorIds, token);
        var message = GitHubActionsIngress.PrepareEvent(trustedActionsPayload, eventName, runId, evidence,
            repositoryGeneration, issueGeneration, occurredAt);
        ValidateGeneration(snapshot.State, IssueNumber(trustedActionsPayload, eventName),
            repositoryGeneration, issueGeneration, message);
        var authorization = new VerifiedEvents();
        var tasks = new GitHubTaskAdapter(api, store, baseBranch, taskCreatorId, approval, finalHeadSource);
        var events = new ProductionEvents(message, api, tasks, authorization, projectionActorId);
        var effects = new ProductionEffects(tasks, new GitHubProjectionAdapter(api, projectionActorId, true));
        return await new OrchestrationWorker(new OrchestrationReducer(authorization), clock).RunAsync(store, events,
            new TaskObservations(tasks, clock), effects, new ReconcileOptions(false, true, true, true),
            options, token);
    }

    public async Task<WorkerResult> PreviewAsync(JsonElement trustedActionsPayload, string eventName, string runId,
        long repositoryGeneration, long issueGeneration, DateTimeOffset occurredAt,
        HashSet<long> projectionActorIds, CancellationToken token = default)
    {
        var snapshot = await store.LoadAsync(token);
        var evidence = await api.ReadIngressEvidenceAsync(trustedActionsPayload, eventName, runId, projectionActorIds, token);
        var message = GitHubActionsIngress.PrepareEvent(trustedActionsPayload, eventName, runId, evidence,
            repositoryGeneration, issueGeneration, occurredAt);
        ValidateGeneration(snapshot.State, IssueNumber(trustedActionsPayload, eventName),
            repositoryGeneration, issueGeneration, message);
        var reducer = new OrchestrationReducer(new VerifiedCommand(message));
        return await new OrchestrationWorker(reducer, clock).RunAsync(new SnapshotStore(snapshot),
            new Source(message), new Observations(api, clock), new DisabledEffects(),
            cancellationToken: token);
    }

    private static void ValidateGeneration(RepositoryState state, int issueNumber, long repositoryGeneration,
        long issueGeneration, OrchestrationEvent? message)
    {
        var committedReplay = message is not null && state.Inbox.TryGetValue(message.Id, out var accepted) &&
            accepted.Digest == message.Digest;
        if (state.Generation != repositoryGeneration ||
            state.Issues.TryGetValue(issueNumber, out var issue) && issue.Generation != issueGeneration && !committedReplay)
            throw new InvalidDataException("Expected workflow generation does not match canonical state.");
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

    private sealed class VerifiedEvents : IAuthorizationPolicy
    {
        private readonly HashSet<string> digests = [];
        public void Grant(OrchestrationEvent message) => digests.Add(message.Digest);
        public bool IsAuthorized(OrchestrationEvent message) => digests.Contains(message.Digest);
    }

    private sealed class ProductionEvents(OrchestrationEvent? command, GitHubRepositoryApi api,
        GitHubTaskAdapter tasks, VerifiedEvents authorization, long projectionActorId) : IWorkerEventSource
    {
        private readonly Dictionary<string, OrchestrationEvent> acceptedObservations = [];
        public async Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token)
        {
            var messages = new List<OrchestrationEvent>();
            if (command is not null && !state.Inbox.ContainsKey(command.Id)) messages.Add(command);
            if (messages.Count > 0 && command!.Data is Reactivate)
            {
                authorization.Grant(command);
                // Commit the generation change before reading any feedback for its resulting generation.
                return new([command], true);
            }
            var feedbackState = StateJson.Clone(state);
            if (command?.Data is Ready && !feedbackState.Issues.ContainsKey(command.IssueNumber))
                feedbackState.Issues.Add(command.IssueNumber, new IssueState
                {
                    Number = command.IssueNumber, Generation = command.IssueGeneration, FeedbackSince = command.OccurredAt
                });
            var completions = new List<OrchestrationEvent>();
            foreach (var issue in state.Issues.Values)
            {
                var completion = await tasks.ReadEventAsync(state, issue, token);
                if (completion?.Data is TaskCompleted completed)
                    feedbackState.Issues[issue.Number].PullRequest = completed.PullRequest;
                if (completion is not null && !state.Inbox.ContainsKey(completion.Id)) completions.Add(completion);
            }
            // Fetch newly verified PR feedback before applying completion or authorizing downstream effects.
            messages.AddRange(await new GitHubFeedbackSource(api, projectionActorId).ReadAsync(feedbackState, token));
            messages.AddRange(completions);
            var batch = new List<OrchestrationEvent>();
            foreach (var message in messages.Take(128))
            {
                if (!acceptedObservations.TryGetValue(message.Id, out var immutable))
                {
                    immutable = message;
                    acceptedObservations.Add(message.Id, immutable);
                }
                authorization.Grant(immutable);
                batch.Add(immutable);
            }
            return new(batch, messages.Count > 128);
        }
    }

    private sealed class TaskObservations(GitHubTaskAdapter tasks, TimeProvider clock) : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken token) =>
            tasks.ObserveAsync(state, clock.GetUtcNow(), token);
    }

    private sealed class ProductionEffects(GitHubTaskAdapter tasks, GitHubProjectionAdapter projections) : IVerifiedAgentEffectAdapter
    {
        public bool AgentEffectsAuthorized => tasks.AgentEffectsAuthorized;
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token) => intent.Kind switch
        {
            EffectKind.LaunchTask => tasks.ExecuteAsync(intent, token),
            EffectKind.ProjectLabels or EffectKind.ProjectStatus => projections.ExecuteAsync(intent, token),
            // Concrete CI/review correlation is still required. Do not issue speculative requests.
            _ => Task.FromResult(new EffectOutcome(DeliveryStatus.Unknown))
        };
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token) => intent.Kind switch
        {
            EffectKind.LaunchTask => tasks.ReconcileAsync(intent, token),
            EffectKind.ProjectLabels or EffectKind.ProjectStatus => projections.ReconcileAsync(intent, token),
            _ => Task.FromResult(new EffectOutcome(DeliveryStatus.Unknown))
        };
    }
}
