using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Production composition with explicit prerequisite gates; ordinary CLI defaults remain read-only.</summary>
public sealed class GitHubOrchestrationHost(GitHubRepositoryApi api, IStateStore store, TimeProvider? clock = null,
    GitHubRepositoryApi? taskApi = null, GitHubTaskLaunchDiagnostics? taskLaunchDiagnostics = null,
    GitHubReviewRequestDiagnostics? reviewRequestDiagnostics = null, GitHubLiveDiagnostics? liveDiagnostics = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<WorkerResult?> ReconcileWakeAsync(JsonElement trustedActionsPayload, string eventName, string runId,
        GitHubWakeRoute wakeRoute, GitHubWakeApproval wakeApproval, long projectionActorId, string baseBranch,
        long taskCreatorId, GitHubExecutionApproval? approval = null, WorkerOptions? options = null,
        ITaskFinalHeadSource? finalHeadSource = null, CancellationToken token = default,
        GitHubReviewApproval? reviewApproval = null, GitHubCiRoute? ciRoute = null, GitHubCiApproval? ciApproval = null,
        GitHubStatusPublisher? statusPublisher = null, GitHubRequiredStatusApproval? statusApproval = null,
        BoundedOrchestrationScope? boundedScope = null, bool automaticContinuations = false)
    {
        if (!wakeApproval.AllowsWake || approval?.AllowsTasks != true)
            throw new InvalidOperationException("Automatic live execution prerequisites are not verified.");
        var scopedStore = ScopeStore(store, boundedScope);
        liveDiagnostics?.Mark(GitHubLiveStage.StateLoad);
        var state = (await scopedStore.LoadAsync(token)).State;
        liveDiagnostics?.Mark(GitHubLiveStage.WakeVerification);
        if (!await new GitHubWakeSource(api, wakeRoute).VerifyAsync(trustedActionsPayload, eventName, runId, state, token))
            return null;
        if (eventName == "schedule" && !OrchestrationContinuation.IsDue(state, clock.GetUtcNow()) &&
            !OrchestrationContinuation.HasWork(state) && !OrchestrationContinuation.RequiresObservation(state))
            return null;
        return await RunVerifiedAsync(scopedStore, null, projectionActorId, baseBranch, taskCreatorId, approval, options,
            finalHeadSource, token, reviewApproval, ciRoute, ciApproval, statusPublisher, statusApproval,
            boundedScope?.RetiredIssueNumber, false, automaticContinuations: automaticContinuations);
    }

    public async Task<WorkerResult> ReconcileAsync(JsonElement trustedActionsPayload, string eventName, string runId,
        long repositoryGeneration, long issueGeneration, DateTimeOffset occurredAt,
        HashSet<long> projectionActorIds, long projectionActorId, string baseBranch, long taskCreatorId,
        GitHubExecutionApproval? approval = null, WorkerOptions? options = null,
        ITaskFinalHeadSource? finalHeadSource = null, CancellationToken token = default,
        GitHubReviewApproval? reviewApproval = null, GitHubCiRoute? ciRoute = null, GitHubCiApproval? ciApproval = null,
        GitHubStatusPublisher? statusPublisher = null, GitHubRequiredStatusApproval? statusApproval = null,
        int? maxRepairTasks = null, BoundedOrchestrationScope? boundedScope = null, bool commandOnly = false,
        bool isolatedStatusTest = false, string trustedWorkflowPath = ".github/workflows/agent-state-machine.yml",
        string? isolatedManifestDigest = null, GitHubRecoveryApproval? recoveryApproval = null,
        bool requireDraftPullRequest = false, Func<CancellationToken, Task>? beforeEffect = null,
        bool automaticContinuations = false)
    {
        if (approval is null ||
            (!approval.AllowsTasks && !(commandOnly && recoveryApproval?.AllowsRecovery == true)))
            throw new InvalidOperationException("Live activation or command-only recovery prerequisites are not verified.");
        if (boundedScope is not null && maxRepairTasks is not null &&
            boundedScope.MaxRepairTasks != maxRepairTasks)
            throw new InvalidDataException("Command repair-task cap does not match the bounded scope.");
        if (eventName == "workflow_dispatch" &&
            trustedActionsPayload.GetProperty("inputs").GetProperty("operation").GetString() == "poc-start" &&
            (trustedWorkflowPath != GitHubPocSettings.DriverWorkflowPath ||
             boundedScope is not { SinglePassTest: false, MaxRepairTasks: 1 } || !requireDraftPullRequest))
            throw new InvalidDataException("PoC start requires its bounded draft-PR driver profile.");
        var scopedStore = ScopeStore(store, boundedScope);
        liveDiagnostics?.Mark(GitHubLiveStage.StateLoad);
        var snapshot = await scopedStore.LoadAsync(token);
        var issueNumber = IssueNumber(trustedActionsPayload, eventName);
        if (boundedScope is { } scope && issueNumber != scope.IssueNumber)
            throw new InvalidDataException("Bounded scope rejected a command for another Issue.");
        liveDiagnostics?.Mark(GitHubLiveStage.CommandVerification);
        var evidence = await api.ReadIngressEvidenceAsync(trustedActionsPayload, eventName, runId, projectionActorIds,
            token, trustedWorkflowPath);
        var message = GitHubActionsIngress.PrepareEvent(trustedActionsPayload, eventName, runId, evidence,
            repositoryGeneration, issueGeneration, occurredAt, boundedScope?.MaxRepairTasks ?? maxRepairTasks);
        if (message?.Data is PrepareRestart && (!commandOnly || boundedScope is not null || isolatedStatusTest))
            throw new InvalidDataException("Restart preparation requires production command-only recovery.");
        if (commandOnly && message?.Data is not (Pause or ResolveTaskTerminal or AbandonUncertainReview or PrepareRestart))
            throw new InvalidDataException("Command-only execution supports only pause or audited terminal resolution.");
        if (isolatedStatusTest && (string.IsNullOrWhiteSpace(isolatedManifestDigest) ||
            message?.Data is not (Ready or Pause)))
            throw new InvalidDataException("Isolated status test requires its pinned manifest and start/pause command.");
        if (boundedScope is not null && message?.Data is Ready ready)
            message = message with { Data = ready with { SinglePassTest = boundedScope.SinglePassTest } };
        message = RestoreCommittedReadyReplay(snapshot.State, message);
        message = RestoreCommittedReviewAbandonmentReplay(snapshot.State, message);
        ValidateGeneration(snapshot.State, issueNumber,
            repositoryGeneration, issueGeneration, message);
        if (isolatedStatusTest)
        {
            if (issueNumber is 227 or 237 || stateHasOutOfScopeData(snapshot.State, issueNumber))
                throw new InvalidDataException("Isolated status test rejected production or cross-Issue state.");
            if (message?.Data is Ready && !snapshot.State.Inbox.ContainsKey(message.Id) &&
                (snapshot.State.Issues.Count != 0 || snapshot.State.Outbox.Count != 0))
                throw new InvalidDataException("The isolated start command is allowed only once on fresh state.");
            if (message?.Data is Pause && !snapshot.State.Outbox.Values.Any(effect =>
                effect.IssueNumber == issueNumber && effect.Kind == EffectKind.PublishRequiredStatus &&
                effect.Status == DeliveryStatus.Succeeded && effect.RequiredStatus?.State == "success" &&
                effect.Sha == snapshot.State.Issues.GetValueOrDefault(issueNumber)?.HeadSha))
                throw new InvalidDataException("Isolated pause requires a saved exact-head successful required status.");
            await PinIsolatedManifestAsync(scopedStore, message!, isolatedManifestDigest!, token);
            snapshot = await scopedStore.LoadAsync(token);
            ValidateIsolatedState(snapshot.State, issueNumber, isolatedManifestDigest!);
        }
        Task<WorkerResult> RunVerified(CancellationToken cancellationToken) =>
            RunVerifiedAsync(scopedStore, message, projectionActorId, baseBranch, taskCreatorId, approval, options,
                finalHeadSource, cancellationToken, reviewApproval, ciRoute, ciApproval, statusPublisher, statusApproval,
                boundedScope?.RetiredIssueNumber, commandOnly, isolatedStatusTest, trustedWorkflowPath,
                requireDraftPullRequest, beforeEffect, automaticContinuations);
        if (commandOnly && message?.Data is AbandonUncertainReview or ResolveTaskTerminal or PrepareRestart)
            return await new OrchestrationWorker(timeProvider: clock, diagnostics: liveDiagnostics)
                .RunExclusiveAsync(scopedStore, RunVerified, token);
        return await RunVerified(token);
    }

    private async Task<WorkerResult> RunVerifiedAsync(IStateStore stateStore, OrchestrationEvent? message,
        long projectionActorId, string baseBranch, long taskCreatorId, GitHubExecutionApproval approval,
        WorkerOptions? options, ITaskFinalHeadSource? finalHeadSource, CancellationToken token,
        GitHubReviewApproval? reviewApproval, GitHubCiRoute? ciRoute, GitHubCiApproval? ciApproval,
        GitHubStatusPublisher? statusPublisher, GitHubRequiredStatusApproval? statusApproval,
        int? excludedIssueNumber, bool commandOnly, bool isolatedStatusTest = false,
        string trustedWorkflowPath = ".github/workflows/agent-state-machine.yml",
        bool requireDraftPullRequest = false, Func<CancellationToken, Task>? beforeEffect = null,
        bool automaticContinuations = false)
    {
        var authorization = new VerifiedEvents();
        var tasks = new GitHubTaskAdapter(api, stateStore, baseBranch, taskCreatorId, approval, finalHeadSource,
            taskApi, requireDraftPullRequest: isolatedStatusTest || requireDraftPullRequest,
            launchDiagnostics: taskLaunchDiagnostics, liveDiagnostics: liveDiagnostics);
        var reviews = new GitHubReviewAdapter(api, stateStore, clock, reviewApproval, tasks, reviewRequestDiagnostics);
        if (message?.Data is PrepareRestart restart)
        {
            var state = (await stateStore.LoadAsync(token)).State;
            if (!state.Inbox.ContainsKey(message.Id))
            {
                if (!state.Issues.TryGetValue(message.IssueNumber, out var issue) ||
                    issue.PullRequest != restart.PullRequest || issue.HeadSha != restart.Sha)
                    throw new InvalidDataException("Restart preparation does not match the canonical PR and head.");
                await tasks.ValidateRestartTasksAsync(state, issue, token);
                using var liveIssue = await api.GetAsync($"issues/{issue.Number}", token);
                var issueRoot = liveIssue.RootElement;
                using var pull = await api.GetAsync($"pulls/{restart.PullRequest}", token);
                var root = pull.RootElement;
                if (issueRoot.GetProperty("number").GetInt32() != issue.Number ||
                    issueRoot.GetProperty("state").GetString() != "open" ||
                    issueRoot.TryGetProperty("pull_request", out _) ||
                    issueRoot.GetProperty("labels").EnumerateArray()
                        .Any(label => label.GetProperty("name").GetString() == "agent:ready") ||
                    root.GetProperty("number").GetInt32() != restart.PullRequest ||
                    root.GetProperty("state").GetString() != "closed" || root.GetProperty("merged").GetBoolean() ||
                    root.GetProperty("head").GetProperty("sha").GetString() != restart.Sha ||
                    root.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
                    root.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
                    root.GetProperty("base").GetProperty("ref").GetString() != baseBranch)
                    throw new InvalidDataException("Restart preparation requires an open non-ready Issue and its exact closed unmerged PR.");
            }
        }
        if (message?.Data is HumanClassification classification)
        {
            var state = (await stateStore.LoadAsync(token)).State;
            if (!state.Inbox.ContainsKey(message.Id))
            {
                var live = await api.ObserveAsync(state, clock.GetUtcNow(), token, excludedIssueNumber);
                if (!live.Issues.TryGetValue(message.IssueNumber, out var issue) || issue.Closed ||
                    issue.PullRequest != classification.PullRequest || issue.HeadSha != classification.Sha)
                    throw new InvalidDataException("Review classification no longer matches the live open head.");
                if (requireDraftPullRequest)
                    await tasks.ValidateCandidateShaAsync(state, state.Issues[message.IssueNumber], token);
            }
        }
        if (message?.Data is RequestChanges changes)
        {
            var state = (await stateStore.LoadAsync(token)).State;
            if (!state.Inbox.ContainsKey(message.Id))
            {
                var live = await api.ObserveAsync(state, clock.GetUtcNow(), token, excludedIssueNumber);
                if (!live.Issues.TryGetValue(message.IssueNumber, out var issue) || issue.Closed ||
                    issue.PullRequest != changes.PullRequest || issue.HeadSha != changes.Sha)
                    throw new InvalidDataException("Requested changes no longer match the live open head.");
                await tasks.ValidateCandidateShaAsync(state, state.Issues[message.IssueNumber], token);
            }
        }
        if (message?.Data is AbandonUncertainReview abandonment)
        {
            var snapshot = (await stateStore.LoadAsync(token)).State;
            message = RestoreCommittedReviewAbandonmentReplay(snapshot, message)!;
            if (commandOnly && snapshot.Inbox.TryGetValue(message.Id, out var committed) &&
                committed.Status == DeliveryStatus.Succeeded && committed.Digest == message.Digest &&
                snapshot.Issues.TryGetValue(message.IssueNumber, out var currentIssue) &&
                currentIssue.Generation > message.IssueGeneration)
                return new(WorkerStopReason.EffectsDisabled, 0, new(snapshot, [], true));
            if (!snapshot.Inbox.ContainsKey(message.Id))
            {
                if (!snapshot.Issues.TryGetValue(message.IssueNumber, out var issue))
                    throw new InvalidDataException("Review-abandonment Issue is not in canonical state.");
                var observedAt = clock.GetUtcNow();
                var evidence = await reviews.ReadAbandonmentEvidenceAsync(snapshot, issue, abandonment.IntentId,
                    abandonment.Sha, observedAt, token);
                message = message with { OccurredAt = observedAt, Data = abandonment with { Evidence = evidence } };
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
            liveDiagnostics?.Mark(GitHubLiveStage.CommandPublication);
            await new OrchestrationReconciler(new(authorization)).DispatchAsync(stateStore, message, token);
        }
        if (commandOnly)
        {
            if (message?.Data is PrepareRestart)
            {
                var state = (await stateStore.LoadAsync(token)).State;
                var issue = state.Issues[message.IssueNumber];
                if (issue.Generation != message.IssueGeneration + 1 || issue.ActiveTaskId is not null || issue.Ready)
                    return new(WorkerStopReason.EffectsDisabled, 0, new(state, [], true));
                var projections = new GitHubProjectionAdapter(api, projectionActorId, true);
                var reconciler = new OrchestrationReconciler();
                var projectionOptions = new ReconcileOptions(false, true, true, true,
                    AllowedEffectKinds: new HashSet<EffectKind> { EffectKind.ProjectLabels, EffectKind.ProjectStatus },
                    ProjectionOnly: true) { ProjectionIssueNumber = issue.Number };
                if (state.Outbox.Values.Any(effect => !projectionOptions.AllowsEffect(effect) &&
                    effect.Status is DeliveryStatus.Executing or DeliveryStatus.Unknown))
                    throw new InvalidOperationException("Restart projections cannot reconcile another operation's in-flight effects.");
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var result = await reconciler.RunAsync(stateStore, new Observations(api, clock, issue.Number), projections,
                        projectionOptions, token);
                    if (result.ExecutedEffect is null)
                    {
                        if (result.State.Outbox.Values.Any(effect => effect.Generation == issue.Generation &&
                            projectionOptions.AllowsEffect(effect) &&
                            effect.Status is DeliveryStatus.Pending or DeliveryStatus.Executing or DeliveryStatus.Unknown or DeliveryStatus.Failed))
                            throw new InvalidOperationException("Restart preparation saved, but label/status projection requires reconciliation.");
                        return new(WorkerStopReason.EffectsDisabled, attempt, result);
                    }
                }
                throw new InvalidOperationException("Restart preparation saved, but label/status projection did not settle.");
            }
            async Task<WorkerResult> Revoke(CancellationToken cancellationToken)
            {
                var state = (await stateStore.LoadAsync(cancellationToken)).State;
                var issue = state.Issues[message!.IssueNumber];
                var hasStatusHistory = state.Outbox.Values.Any(effect =>
                    effect.IssueNumber == issue.Number && effect.Kind == EffectKind.PublishRequiredStatus);
                if (statusPublisher is not null && statusApproval?.AllowsPublication == true &&
                    issue.PullRequest is > 0 && issue.HeadSha is { Length: 40 })
                {
                    var revocation = new GitHubRequiredStatusAdapter(api, stateStore, statusPublisher, statusApproval, clock,
                        liveDiagnostics);
                    state = await new OrchestrationReconciler(diagnostics: liveDiagnostics).RevokeRequiredStatusAsync(stateStore, issue.Number,
                        revocation, clock.GetUtcNow(), cancellationToken);
                    var status = state.Outbox[state.Issues[issue.Number].RequiredStatusIntentId!];
                    if (status.Status != DeliveryStatus.Succeeded || status.RequiredStatus?.State != "error" ||
                        !RequiredStatusPolicy.Matches(state, state.Issues[issue.Number], status))
                        return new(WorkerStopReason.Blocked, 0, new(state, [], true));
                }
                else if (hasStatusHistory)
                    throw new InvalidOperationException("Recovery command saved, but required-status revocation prerequisites are unverified.");
                return new(WorkerStopReason.EffectsDisabled, 0, new(state, [], true));
            }
            return message?.Data is Pause && statusPublisher is not null && statusApproval?.AllowsPublication == true
                ? await new OrchestrationWorker(timeProvider: clock, diagnostics: liveDiagnostics)
                    .RunExclusiveWhenAvailableAsync(stateStore, Revoke, token)
                : await Revoke(token);
        }
        var ci = ciRoute is null ? null : new GitHubCiAdapter(api, stateStore, ciRoute, clock, ciApproval, tasks, liveDiagnostics);
        var events = new ProductionEvents(message, api, tasks, reviews, ci, authorization, projectionActorId,
            excludedIssueNumber, clock, !isolatedStatusTest, liveDiagnostics);
        var statuses = statusPublisher is null ? null : new GitHubRequiredStatusAdapter(api, stateStore, statusPublisher,
            statusApproval, clock, liveDiagnostics);
        var statusPublicationEnabled = statuses is not null && statusApproval?.AllowsPublication == true;
        IVerifiedAgentEffectAdapter effects = new ProductionEffects(tasks, reviews, ci, statuses,
            new GitHubProjectionAdapter(api, projectionActorId, true),
            new GitHubIssueLinkAdapter(api, stateStore, true, clock, statusPublicationEnabled), statusPublicationEnabled);
        if (beforeEffect is not null) effects = new GuardedEffects(effects, beforeEffect);
        var reducer = new OrchestrationReducer(authorization, statuses is not null &&
            statusApproval?.AllowsPublication == true, excludedIssueNumber, reviewRequestsEnabled: !isolatedStatusTest,
            stopOnCiFailure: trustedWorkflowPath == GitHubPocSettings.DriverWorkflowPath);
        var allowedEffects = isolatedStatusTest
            ? new HashSet<EffectKind> { EffectKind.LaunchTask, EffectKind.RunCi, EffectKind.PublishRequiredStatus }
            : null;
        var reconcile = new ReconcileOptions(false, true, true, true,
            AllowedEffectKinds: allowedEffects,
            AllowedRequiredStatusStates: isolatedStatusTest ? new HashSet<string>(["success"], StringComparer.Ordinal) : null,
            MaximumEffectExecutions: isolatedStatusTest ? 3 : null)
            { IssueLinkRevocationApproved = statusPublicationEnabled };
        var worker = new OrchestrationWorker(reducer, clock, liveDiagnostics);
        var observations = new TaskObservations(tasks, clock, excludedIssueNumber);
        var workerOptions = isolatedStatusTest
            ? new WorkerOptions { MaximumIterations = 256, MaximumDuration = TimeSpan.FromMinutes(30),
                PollInterval = TimeSpan.FromSeconds(15) } : options;
        if (automaticContinuations)
            workerOptions = (workerOptions ?? new WorkerOptions()) with { TrackAutomaticContinuations = true };
        Task<WorkerResult> RunWorker(CancellationToken cancellationToken) =>
            worker.RunAsync(stateStore, events, observations, effects, reconcile, workerOptions, cancellationToken);
        async Task<WorkerResult> PublishHold(int issueNumber, CancellationToken cancellationToken)
        {
            var result = await worker.RunAsync(stateStore, events, observations, effects,
                reconcile with { ProjectionOnly = true, ProjectionIssueNumber = issueNumber, AllowedEffectKinds = new HashSet<EffectKind>
                    { EffectKind.ProjectLabels, EffectKind.ProjectStatus, EffectKind.PublishRequiredStatus } },
                new WorkerOptions { MaximumIterations = 8, MaximumDuration = TimeSpan.FromMinutes(1),
                    PollInterval = TimeSpan.Zero }, cancellationToken);
            if (result.Reason != WorkerStopReason.LeaseHeld)
            {
                VerifyHoldProjection(result, issueNumber, statuses is not null && statusApproval?.AllowsPublication == true);
            }
            return result;
        }
        return automaticContinuations
            ? await new AutomaticWorkerContinuation(stateStore, clock).RunForIssueAsync(RunWorker, PublishHold, token)
            : await RunWorker(token);
    }

    internal static void VerifyHoldProjection(WorkerResult result, int issueNumber, bool requiredStatusEnabled)
    {
        var state = result.LastReconciliation?.State;
        if (result.Reason is not (WorkerStopReason.Settled or WorkerStopReason.Blocked) ||
            state is null || state.Outbox.Values.Any(effect => effect.Status == DeliveryStatus.Executing ||
                OrchestrationReconciler.IsExclusiveUncertain(effect)) ||
            !state.Issues.TryGetValue(issueNumber, out var issue) ||
            !OrchestrationContinuation.IsPublishableHold(issue))
            throw new InvalidOperationException("Continuation-limit hold projection was not confirmed; operator attention is required.");
        if (!OrchestrationContinuation.HoldProjectionConfirmed(state, issue,
                requiredStatusEnabled || issue.ContinuationHoldBarrier?.RequireRequiredStatus == true))
            throw new InvalidOperationException("Continuation-limit hold projection was not confirmed; operator attention is required.");
    }

    private static bool stateHasOutOfScopeData(RepositoryState state, int issueNumber) =>
        state.Issues.Keys.Any(number => number != issueNumber) ||
        state.Outbox.Values.Any(effect => effect.IssueNumber != issueNumber);

    private static void ValidateIsolatedState(RepositoryState state, int issueNumber, string manifestDigest)
    {
        if (state.IsolatedTestManifestDigest != manifestDigest ||
            state.Issues.Keys.Any(number => number != issueNumber) ||
            state.Outbox.Values.Any(effect => effect.IssueNumber != issueNumber) ||
            state.AdmittedIssue is { } admitted && admitted != issueNumber ||
            state.Issues.TryGetValue(issueNumber, out var issue) &&
            (!issue.SinglePassTest || issue.Tasks.Count > 1 || issue.Round > 1 ||
             issue.Tasks.Values.Any(task => task.Kind != TaskKind.Coding) ||
             issue.Ci is not null && state.Outbox.Values.Count(effect =>
                 effect.IssueNumber == issueNumber && effect.Kind == EffectKind.RunCi) > 1 ||
             state.Outbox.Values.Any(effect => effect.IssueNumber == issueNumber &&
                 effect.Kind == EffectKind.RequestReview)))
            throw new InvalidDataException("Canonical state exceeds the isolated status-test manifest limits.");
    }

    private static async Task PinIsolatedManifestAsync(IStateStore store, OrchestrationEvent message,
        string manifestDigest, CancellationToken token)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await store.LoadAsync(token);
            var saved = snapshot.State.IsolatedTestManifestDigest;
            if (saved is not null)
            {
                if (saved != manifestDigest) throw new InvalidDataException("Isolated test manifest changed after initialization.");
                return;
            }
            if (snapshot.State.Issues.Count != 0 || snapshot.State.Outbox.Count != 0 ||
                snapshot.State.Inbox.Count != 0 || snapshot.State.AdmittedIssue is not null ||
                message.Data is not Ready)
                throw new InvalidDataException("Only a fresh isolated state can pin the test manifest.");
            var next = StateJson.Clone(snapshot.State);
            next.IsolatedTestManifestDigest = manifestDigest;
            next.Version++;
            if (await store.TrySaveAsync(snapshot, next, token)) return;
        }
        throw new InvalidOperationException("Isolated test manifest publication conflict limit exceeded.");
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

    private static OrchestrationEvent? RestoreCommittedReviewAbandonmentReplay(RepositoryState state,
        OrchestrationEvent? message)
    {
        if (message?.Data is not AbandonUncertainReview abandonment ||
            state.Inbox.GetValueOrDefault(message.Id)?.Event is not
                { Data: AbandonUncertainReview previous } committed)
            return message;
        var incoming = message with { OccurredAt = committed.OccurredAt, Data = abandonment with { Evidence = null } };
        var saved = committed with { Data = previous with { Evidence = null } };
        if (incoming.Digest != saved.Digest)
            throw new InvalidDataException("Replayed review-abandonment command changed its authorization.");
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

    private sealed class Observations(GitHubRepositoryApi api, TimeProvider clock, int? issueNumber = null) : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken cancellationToken)
        {
            var observed = issueNumber is { } number
                ? new RepositoryState { Repository = state.Repository, Issues = new() { [number] = state.Issues[number] } }
                : state;
            return api.ObserveAsync(observed, clock.GetUtcNow(), cancellationToken);
        }
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
        VerifiedEvents authorization, long projectionActorId, int? excludedIssueNumber,
        TimeProvider clock, bool readReviewEvents = true, GitHubLiveDiagnostics? diagnostics = null) : IWorkerEventSource
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
                diagnostics?.Mark(GitHubLiveStage.TaskMapping);
                var mapping = await tasks.ReadMappingEventAsync(state, issue, token);
                if (mapping?.Data is TaskPrMapped mapped)
                    feedbackState.Issues[issue.Number].PullRequest = mapped.PullRequest;
                if (mapping is not null && !state.Inbox.ContainsKey(mapping.Id)) mappings.Add(mapping);
                diagnostics?.Mark(GitHubLiveStage.TaskCompletion);
                var completion = await tasks.ReadEventAsync(state, issue, token);
                if (completion?.Data is TaskCompleted completed)
                    feedbackState.Issues[issue.Number].PullRequest = completed.PullRequest;
                if (completion is not null && !state.Inbox.ContainsKey(completion.Id)) completions.Add(completion);
            }
            // Fetch newly verified PR feedback before applying completion or authorizing downstream effects.
            messages.AddRange(mappings);
            diagnostics?.Mark(GitHubLiveStage.Feedback);
            messages.AddRange(await new GitHubFeedbackSource(api, projectionActorId, clock)
                .ReadAsync(feedbackState, token, excludedIssueNumber));
            messages.AddRange(completions);
            if (ci is not null)
                foreach (var issue in state.Issues.Values)
                {
                    if (issue.Number == excludedIssueNumber) continue;
                    diagnostics?.Mark(GitHubLiveStage.CiObservation);
                    var result = await ci.ReadEventAsync(state, issue, token);
                    if (result is not null && !state.Inbox.ContainsKey(result.Id)) messages.Add(result);
                }
            if (readReviewEvents)
                foreach (var issue in state.Issues.Values)
                {
                    if (issue.Number == excludedIssueNumber) continue;
                    diagnostics?.Mark(GitHubLiveStage.ReviewObservation);
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
        GitHubCiAdapter? ci, GitHubRequiredStatusAdapter? statuses, GitHubProjectionAdapter projections,
        GitHubIssueLinkAdapter links, bool statusPublicationEnabled) :
        IVerifiedAgentEffectAdapter, IIndependentStatusRevocation
    {
        public bool AgentEffectsAuthorized => tasks.AgentEffectsAuthorized;
        public async Task<RepositoryState> RevokeRequiredStatusesAsync(IStateStore store, ReconcileOptions options, DateTimeOffset now,
            CancellationToken token)
        {
            var state = (await store.LoadAsync(token)).State;
            var held = state.Issues.Values.Where(issue =>
                (options.ProjectionIssueNumber is null || options.ProjectionIssueNumber == issue.Number) &&
                RequiredStatusPolicy.RequiresIssueLinkRevocation(state, issue)).Select(issue => issue.Number).ToList();
            foreach (var number in held)
            {
                if (statuses is null || !statusPublicationEnabled)
                {
                    if (state.Outbox.Values.Any(effect => effect.IssueNumber == number &&
                        effect.Kind == EffectKind.PublishRequiredStatus))
                        throw new InvalidOperationException("Issue-link failure requires status revocation, but publisher approval is unavailable.");
                    continue;
                }
                state = await new OrchestrationReconciler().RevokeRequiredStatusAsync(store, number, statuses, now, token);
            }
            return state;
        }
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token) => intent.Kind switch
        {
            EffectKind.LaunchTask => tasks.ExecuteAsync(intent, token),
            EffectKind.RequestReview => reviews.ExecuteAsync(intent, token),
            EffectKind.RunCi when ci is not null => ci.ExecuteAsync(intent, token),
            EffectKind.PublishRequiredStatus when statuses is not null => statuses.ExecuteAsync(intent, token),
            EffectKind.LinkIssue => links.ExecuteAsync(intent, token),
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
            EffectKind.LinkIssue => links.ReconcileAsync(intent, token),
            EffectKind.ProjectLabels or EffectKind.ProjectStatus => projections.ReconcileAsync(intent, token),
            _ => Task.FromResult(new EffectOutcome(DeliveryStatus.Unknown))
        };
    }

    private sealed class GuardedEffects(IVerifiedAgentEffectAdapter inner,
        Func<CancellationToken, Task> beforeEffect) : IVerifiedAgentEffectAdapter, IIndependentStatusRevocation
    {
        public bool AgentEffectsAuthorized => inner.AgentEffectsAuthorized;
        public async Task<RepositoryState> RevokeRequiredStatusesAsync(IStateStore store, ReconcileOptions options, DateTimeOffset now,
            CancellationToken token)
        {
            if (inner is not IIndependentStatusRevocation revocation)
                throw new InvalidOperationException("Independent status revocation is unavailable.");
            await beforeEffect(token);
            return await revocation.RevokeRequiredStatusesAsync(store, options, now, token);
        }

        public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
        {
            await beforeEffect(token);
            return await inner.ExecuteAsync(intent, token);
        }

        public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token)
        {
            await beforeEffect(token);
            return await inner.ReconcileAsync(intent, token);
        }
    }
}
