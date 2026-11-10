using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Production composition with explicit prerequisite gates; ordinary CLI defaults remain read-only.</summary>
public sealed class GitHubOrchestrationHost(GitHubRepositoryApi api, IStateStore store, TimeProvider? clock = null,
    GitHubRepositoryApi? taskApi = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<WorkerResult?> ReconcileWakeAsync(JsonElement trustedActionsPayload, string eventName, string runId,
        GitHubWakeRoute wakeRoute, GitHubWakeApproval wakeApproval, long projectionActorId, string baseBranch,
        long taskCreatorId, GitHubExecutionApproval? approval = null, WorkerOptions? options = null,
        ITaskFinalHeadSource? finalHeadSource = null, CancellationToken token = default,
        GitHubReviewApproval? reviewApproval = null, GitHubCiRoute? ciRoute = null, GitHubCiApproval? ciApproval = null,
        GitHubStatusPublisher? statusPublisher = null, GitHubRequiredStatusApproval? statusApproval = null,
        BoundedOrchestrationScope? boundedScope = null)
    {
        if (!wakeApproval.AllowsWake || approval?.AllowsTasks != true)
            throw new InvalidOperationException("Automatic live execution prerequisites are not verified.");
        var scopedStore = ScopeStore(store, boundedScope);
        var state = (await scopedStore.LoadAsync(token)).State;
        if (!await new GitHubWakeSource(api, wakeRoute).VerifyAsync(trustedActionsPayload, eventName, runId, state, token))
            return null;
        if (eventName == "schedule" && !OrchestrationContinuation.IsDue(state, clock.GetUtcNow()) &&
            !OrchestrationContinuation.HasWork(state) && !OrchestrationContinuation.RequiresObservation(state))
            return null;
        return await RunVerifiedAsync(scopedStore, null, projectionActorId, baseBranch, taskCreatorId, approval, options,
            finalHeadSource, token, reviewApproval, ciRoute, ciApproval, statusPublisher, statusApproval,
            boundedScope?.RetiredIssueNumber, false);
    }

    public async Task<WorkerResult> ReconcileAsync(JsonElement trustedActionsPayload, string eventName, string runId,
        long repositoryGeneration, long issueGeneration, DateTimeOffset occurredAt,
        HashSet<long> projectionActorIds, long projectionActorId, string baseBranch, long taskCreatorId,
        GitHubExecutionApproval? approval = null, WorkerOptions? options = null,
        ITaskFinalHeadSource? finalHeadSource = null, CancellationToken token = default,
        GitHubReviewApproval? reviewApproval = null, GitHubCiRoute? ciRoute = null, GitHubCiApproval? ciApproval = null,
        GitHubStatusPublisher? statusPublisher = null, GitHubRequiredStatusApproval? statusApproval = null,
        int? maxRepairTasks = null, BoundedOrchestrationScope? boundedScope = null, bool commandOnly = false)
    {
        if (approval?.AllowsTasks != true) throw new InvalidOperationException("Live activation prerequisites are not verified.");
        if (boundedScope is not null && maxRepairTasks is not null &&
            boundedScope.MaxRepairTasks != maxRepairTasks)
            throw new InvalidDataException("Command repair-task cap does not match the bounded scope.");
        var scopedStore = ScopeStore(store, boundedScope);
        var snapshot = await scopedStore.LoadAsync(token);
        var issueNumber = IssueNumber(trustedActionsPayload, eventName);
        if (boundedScope is { } scope && issueNumber != scope.IssueNumber)
            throw new InvalidDataException("Bounded scope rejected a command for another Issue.");
        var evidence = await api.ReadIngressEvidenceAsync(trustedActionsPayload, eventName, runId, projectionActorIds, token);
        var message = GitHubActionsIngress.PrepareEvent(trustedActionsPayload, eventName, runId, evidence,
            repositoryGeneration, issueGeneration, occurredAt, boundedScope?.MaxRepairTasks ?? maxRepairTasks);
        if (commandOnly && message?.Data is not (Pause or ResolveTaskTerminal))
            throw new InvalidDataException("Command-only execution supports only pause or terminal resolution.");
        if (boundedScope is not null && message?.Data is Ready ready)
            message = message with { Data = ready with { SinglePassTest = true } };
        message = RestoreCommittedReadyReplay(snapshot.State, message);
        ValidateGeneration(snapshot.State, issueNumber,
            repositoryGeneration, issueGeneration, message);
        return await RunVerifiedAsync(scopedStore, message, projectionActorId, baseBranch, taskCreatorId, approval, options,
            finalHeadSource, token, reviewApproval, ciRoute, ciApproval, statusPublisher, statusApproval,
            boundedScope?.RetiredIssueNumber, commandOnly);
    }

    private async Task<WorkerResult> RunVerifiedAsync(IStateStore stateStore, OrchestrationEvent? message,
        long projectionActorId, string baseBranch, long taskCreatorId, GitHubExecutionApproval approval,
        WorkerOptions? options, ITaskFinalHeadSource? finalHeadSource, CancellationToken token,
        GitHubReviewApproval? reviewApproval, GitHubCiRoute? ciRoute, GitHubCiApproval? ciApproval,
        GitHubStatusPublisher? statusPublisher, GitHubRequiredStatusApproval? statusApproval,
        int? excludedIssueNumber, bool commandOnly)
    {
        var authorization = new VerifiedEvents();
        var tasks = new GitHubTaskAdapter(api, stateStore, baseBranch, taskCreatorId, approval, finalHeadSource, taskApi);
        if (message?.Data is HumanClassification classification)
        {
            var state = (await stateStore.LoadAsync(token)).State;
            if (!state.Inbox.ContainsKey(message.Id))
            {
                var live = await api.ObserveAsync(state, clock.GetUtcNow(), token, excludedIssueNumber);
                if (!live.Issues.TryGetValue(message.IssueNumber, out var issue) || issue.Closed ||
                    issue.PullRequest != classification.PullRequest || issue.HeadSha != classification.Sha)
                    throw new InvalidDataException("Review classification no longer matches the live open head.");
            }
        }
        if (message?.Data is ResolveTaskTerminal resolution)
        {
            var snapshot = (await stateStore.LoadAsync(token)).State;
            if (snapshot.Inbox.GetValueOrDefault(message.Id)?.Event is { Data: ResolveTaskTerminal previous } saved)
            {
                if (previous.TaskId != resolution.TaskId || previous.Reason != resolution.Reason ||
                    saved.Actor != message.Actor || saved.IssueNumber != message.IssueNumber ||
                    saved.RepositoryGeneration != message.RepositoryGeneration || saved.IssueGeneration != message.IssueGeneration)
                    throw new InvalidDataException("Replayed terminal resolution changed its command.");
                message = saved;
            }
            else
            {
                if (!snapshot.Issues.TryGetValue(message.IssueNumber, out var issue) || issue.ActiveTaskId != resolution.TaskId)
                    throw new InvalidDataException("Terminal resolution does not match the canonical task.");
                var observed = await tasks.ReadTerminalEventAsync(snapshot, issue, clock.GetUtcNow(), token);
                var evidenceId = $"terminal-for:{message.Id}";
                var terminal = observed with { Id = evidenceId };
                if (snapshot.Inbox.GetValueOrDefault(evidenceId)?.Event is { Data: TaskTerminalObserved prior } priorEvent)
                {
                    var current = (TaskTerminalObserved)observed.Data;
                    if (prior.TaskId != current.TaskId || prior.Evidence.ResponseDigest != current.Evidence.ResponseDigest ||
                        observed.OccurredAt - priorEvent.OccurredAt > TaskTerminalRecovery.MaximumEvidenceAge)
                        throw new InvalidDataException("Terminal evidence changed or expired; a fresh operator decision is required.");
                    terminal = priorEvent;
                }
                authorization.Grant(terminal);
                await new OrchestrationReconciler(new(authorization)).DispatchAsync(stateStore, terminal, token);
                message = message with { OccurredAt = observed.OccurredAt,
                    Data = resolution with { EvidenceEventId = terminal.Id } };
            }
        }
        if (message is not null)
        {
            authorization.Grant(message);
            // Persist a verified command before unrelated service reads can fail or the invocation yields.
            await new OrchestrationReconciler(new(authorization)).DispatchAsync(stateStore, message, token);
        }
        if (commandOnly)
        {
            var state = (await stateStore.LoadAsync(token)).State;
            return new(WorkerStopReason.EffectsDisabled, 0, new(state, [], true));
        }
        var reviews = new GitHubReviewAdapter(api, stateStore, clock, reviewApproval, tasks);
        var ci = ciRoute is null ? null : new GitHubCiAdapter(api, stateStore, ciRoute, clock, ciApproval, tasks);
        var events = new ProductionEvents(message, api, tasks, reviews, ci, authorization, projectionActorId,
            excludedIssueNumber);
        var statuses = statusPublisher is null ? null : new GitHubRequiredStatusAdapter(api, stateStore, statusPublisher, statusApproval, clock);
        var effects = new ProductionEffects(tasks, reviews, ci, statuses, new GitHubProjectionAdapter(api, projectionActorId, true));
        return await new OrchestrationWorker(new OrchestrationReducer(authorization, statuses is not null &&
            statusApproval?.AllowsPublication == true, excludedIssueNumber), clock).RunAsync(stateStore, events,
            new TaskObservations(tasks, clock, excludedIssueNumber), effects, new ReconcileOptions(false, true, true, true),
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

    private static OrchestrationEvent? RestoreCommittedReadyReplay(RepositoryState state, OrchestrationEvent? message)
    {
        if (message?.Data is not Ready || state.Inbox.GetValueOrDefault(message.Id)?.Event is not
            { Data: Ready } committed)
            return message;
        if ((message with { Data = new Ready() }).Digest != (committed with { Data = new Ready() }).Digest)
            throw new InvalidDataException("Replayed Ready command changed its immutable event content.");
        return committed;
    }

    private static IStateStore ScopeStore(IStateStore target, BoundedOrchestrationScope? scope) =>
        scope is null ? target : new ScopedStateStore(target, scope);

    private sealed class ScopedStateStore(IStateStore target, BoundedOrchestrationScope scope) : IStateStore
    {
        public async Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            var snapshot = await target.LoadAsync(cancellationToken);
            Validate(snapshot.State);
            return snapshot;
        }

        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState state, CancellationToken cancellationToken)
        {
            Validate(expected.State);
            Validate(state);
            return target.TrySaveAsync(expected, state, cancellationToken);
        }

        private void Validate(RepositoryState state)
        {
            scope.Validate(state);
        }
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
        GitHubTaskAdapter tasks, GitHubReviewAdapter reviews, GitHubCiAdapter? ci,
        VerifiedEvents authorization, long projectionActorId, int? excludedIssueNumber) : IWorkerEventSource
    {
        private readonly Dictionary<string, OrchestrationEvent> acceptedObservations = [];
        private bool reactivationBoundaryPending = command?.Data is Reactivate;
        public async Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token)
        {
            var messages = new List<OrchestrationEvent>();
            if (command is not null && !state.Inbox.ContainsKey(command.Id)) messages.Add(command);
            if (reactivationBoundaryPending)
            {
                reactivationBoundaryPending = false;
                authorization.Grant(command!);
                // Commit the generation change before reading any feedback for its resulting generation.
                return new(messages, true);
            }
            var feedbackState = StateJson.Clone(state);
            if (command?.Data is Ready && !feedbackState.Issues.ContainsKey(command.IssueNumber))
                feedbackState.Issues.Add(command.IssueNumber, new IssueState
                {
                    Number = command.IssueNumber, Generation = command.IssueGeneration, FeedbackSince = command.OccurredAt
                });
            var mappings = new List<OrchestrationEvent>();
            var completions = new List<OrchestrationEvent>();
            foreach (var issue in state.Issues.Values)
            {
                if (issue.Number == excludedIssueNumber) continue;
                var mapping = await tasks.ReadMappingEventAsync(state, issue, token);
                if (mapping?.Data is TaskPrMapped mapped)
                    feedbackState.Issues[issue.Number].PullRequest = mapped.PullRequest;
                if (mapping is not null && !state.Inbox.ContainsKey(mapping.Id)) mappings.Add(mapping);
                var completion = await tasks.ReadEventAsync(state, issue, token);
                if (completion?.Data is TaskCompleted completed)
                    feedbackState.Issues[issue.Number].PullRequest = completed.PullRequest;
                if (completion is not null && !state.Inbox.ContainsKey(completion.Id)) completions.Add(completion);
            }
            // Fetch newly verified PR feedback before applying completion or authorizing downstream effects.
            messages.AddRange(mappings);
            messages.AddRange(await new GitHubFeedbackSource(api, projectionActorId)
                .ReadAsync(feedbackState, token, excludedIssueNumber));
            messages.AddRange(completions);
            if (ci is not null)
                foreach (var issue in state.Issues.Values)
                {
                    if (issue.Number == excludedIssueNumber) continue;
                    var result = await ci.ReadEventAsync(state, issue, token);
                    if (result is not null && !state.Inbox.ContainsKey(result.Id)) messages.Add(result);
                }
            foreach (var issue in state.Issues.Values)
            {
                if (issue.Number == excludedIssueNumber) continue;
                var result = await reviews.ReadEventAsync(state, issue, token);
                if (result is not null && !state.Inbox.ContainsKey(result.Id)) messages.Add(result);
            }
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

    private sealed class TaskObservations(GitHubTaskAdapter tasks, TimeProvider clock, int? excludedIssueNumber) : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken token) =>
            tasks.ObserveAsync(state, clock.GetUtcNow(), token, excludedIssueNumber);
    }

    private sealed class ProductionEffects(GitHubTaskAdapter tasks, GitHubReviewAdapter reviews,
        GitHubCiAdapter? ci, GitHubRequiredStatusAdapter? statuses, GitHubProjectionAdapter projections) : IVerifiedAgentEffectAdapter
    {
        public bool AgentEffectsAuthorized => tasks.AgentEffectsAuthorized;
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token) => intent.Kind switch
        {
            EffectKind.LaunchTask => tasks.ExecuteAsync(intent, token),
            EffectKind.RequestReview => reviews.ExecuteAsync(intent, token),
            EffectKind.RunCi when ci is not null => ci.ExecuteAsync(intent, token),
            EffectKind.PublishRequiredStatus when statuses is not null => statuses.ExecuteAsync(intent, token),
            EffectKind.ProjectLabels or EffectKind.ProjectStatus => projections.ExecuteAsync(intent, token),
            // Missing adapters/capabilities must never issue speculative effects.
            _ => Task.FromResult(new EffectOutcome(DeliveryStatus.Unknown))
        };
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token) => intent.Kind switch
        {
            EffectKind.LaunchTask => tasks.ReconcileAsync(intent, token),
            EffectKind.RequestReview => reviews.ReconcileAsync(intent, token),
            EffectKind.RunCi when ci is not null => ci.ReconcileAsync(intent, token),
            EffectKind.PublishRequiredStatus when statuses is not null => statuses.ReconcileAsync(intent, token),
            EffectKind.ProjectLabels or EffectKind.ProjectStatus => projections.ReconcileAsync(intent, token),
            _ => Task.FromResult(new EffectOutcome(DeliveryStatus.Unknown))
        };
    }
}
