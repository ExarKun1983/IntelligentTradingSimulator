using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class OrchestrationPersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly string Head = new('a', 40);
    private static readonly ReconcileOptions Enabled = new(false, true, true, true);
    private static readonly ReconcileOptions Simulation = Enabled with { AllowOfflineAgentSimulation = true };
    private static OrchestrationEvent Message(EventData data, string id) =>
        new(id, "owner/repo", 7, 1, 1, data switch
        {
            TaskCompleted => new Actor("task-service", ActorKind.TaskService),
            CiResult => new Actor("ci-service", ActorKind.CiService),
            ReviewResult => new Actor("review-service", ActorKind.ReviewService),
            _ => new Actor("maintainer", ActorKind.Human)
        }, Now, data);

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    public async Task DisabledOrUnapprovedExecutionNeverCallsAdapter(bool dryRun, bool enabled, bool capabilities, bool cost)
    {
        var store = new MemoryStore(CiPending());
        var adapter = new Effects(store);
        var result = await new OrchestrationReconciler().RunAsync(store, new Observations(), adapter,
            new(dryRun, enabled, capabilities, cost));
        Assert.True(result.EffectsDisabled);
        Assert.Equal(0, adapter.Executions);
        Assert.Equal(0, adapter.Recoveries);
        if (dryRun) Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(EffectKind.LaunchTask)]
    [InlineData(EffectKind.RequestReview)]
    public async Task TaskLaunchIsHardDisabledEvenWithAllPrerequisiteFlags(EffectKind kind)
    {
        var state = CiPending();
        if (kind == EffectKind.LaunchTask)
        {
            state.Issues[7].Ci = null;
            state.Issues[7].Phase = Phase.CODING;
            state.Issues[7].Tasks["task"].Status = TaskStatus.Intended;
            state.Issues[7].Tasks["task"].LaunchConfirmed = false;
        }
        else
        {
            state.Issues[7].Ci!.Green = true;
            state.Issues[7].Phase = Phase.REVIEW_PENDING;
            state.Issues[7].Review = new ReviewIdentity { IntentId = "launch", TaskId = "task", Round = 1, Sha = Head };
        }
        state.Outbox.Clear();
        state.Outbox.Add("launch", new EffectIntent
        {
            Id = "launch", IssueNumber = 7, Generation = 1, Kind = kind,
            TaskId = "task", Round = 1, Sha = Head, Status = DeliveryStatus.Pending
        });
        var store = new MemoryStore(state);
        var adapter = new Effects(store);
        await new OrchestrationReconciler().RunAsync(store, new Observations(), adapter, Enabled);
        Assert.DoesNotContain(kind, adapter.Kinds);
        Assert.All(adapter.Kinds, executed => Assert.Equal(EffectKind.ProjectStatus, executed));
        Assert.Equal(DeliveryStatus.Pending, store.Current.Outbox["launch"].Status);
    }

    [Fact]
    public async Task InitialMappedCodingAndRepairLaunchesStillRequireTheirExactSourceHead()
    {
        foreach (var kind in Enum.GetValues<TaskKind>())
        {
            foreach (var sourceSha in new string?[] { null, "", new('b', 40) })
            {
                var state = LaunchPending();
                state.Issues[7].Tasks["task"].Kind = kind;
                state.Issues[7].Tasks["task"].SourceSha = sourceSha;
                var store = new MemoryStore(state);
                var adapter = new Effects(store);

                await new OrchestrationReconciler().RunAsync(store, new Observations(), adapter, Simulation);

                Assert.DoesNotContain(EffectKind.LaunchTask, adapter.Kinds);
                Assert.Equal(DeliveryStatus.Pending, store.Current.Outbox["launch"].Status);
            }
        }
    }

    [Theory]
    [InlineData(TaskKind.CiRepair)]
    [InlineData(TaskKind.ReviewRepair)]
    [InlineData(TaskKind.FeedbackRepair)]
    public async Task RepairCannotUseUnmappedInitialLaunchContract(TaskKind kind)
    {
        var state = LaunchPending();
        state.Issues[7].Tasks["task"].Kind = kind;
        state.Issues[7].Tasks["task"].LaunchSource = TaskLaunchSource.UnmappedIssue;
        var store = new MemoryStore(state);
        var adapter = new Effects(store);

        await new OrchestrationReconciler().RunAsync(store, new Observations(), adapter, Simulation);

        Assert.DoesNotContain(EffectKind.LaunchTask, adapter.Kinds);
        Assert.Equal(DeliveryStatus.Pending, store.Current.Outbox["launch"].Status);
    }

    [Fact]
    public async Task ExplicitOfflineSimulationExercisesLaunchCompletionCiAndReviewWithoutReadyProjection()
    {
        var state = RepositoryState.Create("owner/repo");
        var reducer = new OrchestrationReducer(new Authorized());
        state = reducer.Apply(state, Message(new Ready(), "ready"));
        var store = new MemoryStore(state);
        var adapter = new Effects(store);
        var observations = new Observations();
        var reconciler = new OrchestrationReconciler(reducer);
        var launched = await reconciler.RunAsync(store, observations, adapter, Simulation);
        var issue = launched.State.Issues[7];
        var taskId = issue.ActiveTaskId!;
        Assert.True(issue.Tasks[taskId].LaunchConfirmed);
        Assert.True(issue.ActiveAuthorized);
        Assert.DoesNotContain("agent:ready", OrchestrationReducer.ProjectLabels(issue));
        observations.Ready = false;
        observations.HeadOverride = new string('b', 40);
        await reconciler.DispatchAsync(store, Message(new TaskCompleted(taskId, 11, observations.HeadOverride), "completed"));
        var ci = await reconciler.RunAsync(store, observations, adapter, Simulation);
        Assert.Equal(Phase.CI_PENDING, ci.State.Issues[7].Phase);
        Assert.Equal("external", ci.State.Issues[7].Ci!.RunId);
        await reconciler.DispatchAsync(store, Message(
            new CiResult("external", taskId, 1, 11, observations.HeadOverride, true), "ci-green"));
        var review = await reconciler.RunAsync(store, observations, adapter, Simulation);
        Assert.Equal("external", review.State.Issues[7].Review!.ReviewId);
        await reconciler.DispatchAsync(store, Message(new ReviewResult("external", taskId, 1, 11,
            observations.HeadOverride, ReviewClassification.Accepted, [], []), "review"));
        Assert.Equal(Phase.HUMAN_REVIEW, store.Current.Issues[7].Phase);
        Assert.Null(store.Current.AdmittedIssue);
        Assert.Equal(new[] { EffectKind.LaunchTask, EffectKind.RunCi, EffectKind.RequestReview }, adapter.Kinds);
    }

    [Theory]
    [InlineData(EffectKind.LaunchTask)]
    [InlineData(EffectKind.RequestReview)]
    public async Task AgentSimulationCrashRecoversExecutingIdentityWithoutSecondLaunch(EffectKind kind)
    {
        var state = CiPending();
        state.Outbox.Clear();
        if (kind == EffectKind.LaunchTask)
        {
            state.Issues[7].Ci = null;
            state.Issues[7].Phase = Phase.CODING;
            state.Issues[7].Tasks["task"].Status = TaskStatus.Intended;
            state.Issues[7].Tasks["task"].LaunchConfirmed = false;
            state.Outbox["launch"] = new EffectIntent
            {
                Id = "launch", IssueNumber = 7, Generation = 1, Kind = EffectKind.LaunchTask,
                TaskId = "task", Round = 1, Sha = Head, Status = DeliveryStatus.Pending
            };
        }
        else
        {
            state.Issues[7].Ci!.Green = true;
            state.Issues[7].Phase = Phase.REVIEW_PENDING;
        }
        var store = new MemoryStore(state);
        var adapter = new Effects(store) { AfterExecute = () => store.FailWrites = true };
        var reconciler = new OrchestrationReconciler();
        await Assert.ThrowsAsync<IOException>(() => reconciler.RunAsync(store, new Observations(), adapter, Simulation));
        Assert.Single(store.Current.Outbox.Values, effect => effect.Status == DeliveryStatus.Executing && effect.Kind == kind);
        store.FailWrites = false;
        var recovered = await reconciler.RunAsync(store, new Observations { Ready = false }, adapter, Simulation);
        Assert.Equal(1, adapter.Executions);
        Assert.Equal(1, adapter.Recoveries);
        Assert.Single(recovered.State.Outbox.Values, effect => effect.Kind == kind && effect.Status == DeliveryStatus.Succeeded);
        Assert.True(recovered.State.Issues[7].ActiveAuthorized);
    }

    [Fact]
    public async Task SimulationFlagCannotEnableAnAdapterWithoutExplicitOfflineMarker()
    {
        var state = CiPending();
        state.Issues[7].Ci!.Green = true;
        state.Issues[7].Phase = Phase.REVIEW_PENDING;
        state.Outbox.Clear();
        var store = new MemoryStore(state);
        var fake = new Effects(store);
        await new OrchestrationReconciler().RunAsync(store, new Observations(), new UnmarkedAdapter(fake), Simulation);
        Assert.DoesNotContain(EffectKind.RequestReview, fake.Kinds);
        Assert.All(fake.Kinds, executed => Assert.Equal(EffectKind.ProjectStatus, executed));
        Assert.Contains(store.Current.Outbox.Values, effect => effect.Kind == EffectKind.RequestReview &&
            effect.Status == DeliveryStatus.Pending);
    }

    [Fact]
    public async Task ActiveCiIgnoresAbsenceOfProjectedReadyLabel()
    {
        var store = new MemoryStore(CiPending());
        var adapter = new Effects(store);
        var result = await new OrchestrationReconciler().RunAsync(store,
            new Observations { Ready = false }, adapter, Enabled);
        Assert.Equal(1, adapter.Executions);
        Assert.True(result.State.Issues[7].ActiveAuthorized);
        Assert.Equal(Phase.CI_PENDING, result.State.Issues[7].Phase);
        Assert.Equal(DeliveryStatus.Succeeded, result.State.Outbox["ci"].Status);
    }

    [Fact]
    public async Task EffectExecutingIsDurableBeforeCallAndResultDurableAfterward()
    {
        var store = new MemoryStore(CiPending());
        var adapter = new Effects(store);
        var result = await new OrchestrationReconciler().RunAsync(store, new Observations(), adapter, Enabled);
        Assert.Equal(1, adapter.Executions);
        Assert.Equal(DeliveryStatus.Succeeded, result.State.Outbox["ci"].Status);
        Assert.Equal("external", result.State.Issues[7].Ci!.RunId);
        Assert.True(store.Writes >= 2);
    }

    [Fact]
    public async Task CrashAfterExternalCallRecoversExecutingIntentWithoutSecondExecution()
    {
        var store = new MemoryStore(CiPending());
        var adapter = new Effects(store) { AfterExecute = () => store.FailWrites = true };
        var reconciler = new OrchestrationReconciler();
        await Assert.ThrowsAsync<IOException>(() => reconciler.RunAsync(store, new Observations(), adapter, Enabled));
        Assert.Equal(DeliveryStatus.Executing, store.Current.Outbox["ci"].Status);
        store.FailWrites = false;
        adapter.AfterExecute = null;
        var recovered = await reconciler.RunAsync(store, new Observations(), adapter, Enabled);
        Assert.Equal(1, adapter.Executions);
        Assert.Equal(1, adapter.Recoveries);
        Assert.Equal(DeliveryStatus.Succeeded, recovered.State.Outbox["ci"].Status);
    }

    [Fact]
    public async Task UnknownExecutingOutcomeIsHeldAndLookedUpWithoutExecutingAgain()
    {
        var state = CiPending();
        state.Outbox["ci"].Status = DeliveryStatus.Executing;
        var store = new MemoryStore(state);
        var adapter = new Effects(store) { RecoveryOutcome = new(DeliveryStatus.Unknown) };
        var reconciler = new OrchestrationReconciler();
        var result = await reconciler.RunAsync(store, new Observations(), adapter, Enabled);
        Assert.Equal(Phase.BLOCKED, result.State.Issues[7].Phase);
        Assert.Equal(7, result.State.AdmittedIssue);
        Assert.Equal(DeliveryStatus.Unknown, result.State.Outbox["ci"].Status);
        await reconciler.RunAsync(store, new Observations(), adapter, Enabled);
        Assert.Equal(0, adapter.Executions);
        Assert.Equal(2, adapter.Recoveries);
        adapter.RecoveryOutcome = new(DeliveryStatus.Succeeded, "resolved-run");
        var resolved = await reconciler.RunAsync(store, new Observations(), adapter, Enabled);
        Assert.Equal(0, adapter.Executions);
        Assert.Equal(3, adapter.Recoveries);
        Assert.Equal(DeliveryStatus.Succeeded, resolved.State.Outbox["ci"].Status);
        Assert.Equal("resolved-run", resolved.State.Issues[7].Ci!.RunId);
        Assert.Equal(Phase.BLOCKED, resolved.State.Issues[7].Phase);
    }

    [Fact]
    public async Task ThrownExecutionIsAmbiguousNotSafeToRetry()
    {
        var store = new MemoryStore(CiPending());
        var adapter = new Effects(store) { ThrowOnExecute = true, RecoveryOutcome = new(DeliveryStatus.Unknown) };
        var reconciler = new OrchestrationReconciler();
        await reconciler.RunAsync(store, new Observations(), adapter, Enabled);
        await reconciler.RunAsync(store, new Observations(), adapter, Enabled);
        Assert.Equal(1, adapter.Executions);
        Assert.Equal(DeliveryStatus.Unknown, store.Current.Outbox["ci"].Status);
        Assert.Equal(Phase.BLOCKED, store.Current.Issues[7].Phase);
    }

    [Fact]
    public async Task AuthorizedWithdrawalIsPersistedBeforeAnyPendingEffectCanRun()
    {
        var store = new MemoryStore(CiPending());
        var adapter = new Effects(store);
        var reconciler = new OrchestrationReconciler(new OrchestrationReducer(new Authorized()));
        await reconciler.DispatchAsync(store, Message(new Withdraw(), "withdraw"));
        await reconciler.RunAsync(store, new Observations { Ready = false }, adapter, Enabled);
        Assert.Equal(DeliveryStatus.Cancelled, store.Current.Outbox["ci"].Status);
        Assert.DoesNotContain(adapter.Kinds, kind => kind == EffectKind.RunCi);
    }

    [Fact]
    public async Task PublicationConflictReloadsBeforeEffectsAndResultConflictsDoNotRepeatExecution()
    {
        var store = new MemoryStore(CiPending()) { Conflicts = 1 };
        var adapter = new Effects(store) { AfterExecute = () => store.Conflicts = 2 };
        var result = await new OrchestrationReconciler().RunAsync(store, new Observations(), adapter, Enabled);
        Assert.Equal(1, adapter.Executions);
        Assert.Equal(DeliveryStatus.Succeeded, result.State.Outbox["ci"].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalLaunchSuccessSupersedesConcurrentUnknownWithoutDuplicateExecution(bool conflict)
    {
        var store = new MemoryStore(LaunchPending());
        var adapter = new ControlledEffects(store);
        var reconciler = new OrchestrationReconciler();
        var original = reconciler.RunAsync(store, new Observations(), adapter, Simulation);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var uncertain = await reconciler.RunAsync(store, new Observations { Ready = false }, adapter, Simulation);
        Assert.Equal(DeliveryStatus.Unknown, uncertain.State.Outbox["launch"].Status);
        Assert.Equal(TaskStatus.Unknown, uncertain.State.Issues[7].Tasks["task"].Status);
        if (conflict) store.Conflicts = 1;
        adapter.Completion.SetResult(new EffectOutcome(DeliveryStatus.Succeeded, "actual-task-id"));
        var result = await original;
        Assert.Equal(1, adapter.Executions);
        Assert.Equal(1, adapter.Recoveries);
        Assert.Equal(DeliveryStatus.Succeeded, result.State.Outbox["launch"].Status);
        Assert.Equal("actual-task-id", result.State.Outbox["launch"].ExternalId);
        Assert.Equal("actual-task-id", result.State.Issues[7].Tasks["task"].ExternalId);
        Assert.True(result.State.Issues[7].Tasks["task"].LaunchConfirmed);
        Assert.Equal(TaskStatus.Running, result.State.Issues[7].Tasks["task"].Status);
        Assert.Equal(7, result.State.AdmittedIssue);
        Assert.Equal(0, store.Conflicts);
        await reconciler.RunAsync(store, new Observations { Ready = false }, adapter, Simulation);
        Assert.Equal(1, adapter.Executions);
    }

    [Fact]
    public async Task RecoveredSuccessSurvivesLateOriginalUnknownAndRejectsConflictingIdentity()
    {
        var store = new MemoryStore(LaunchPending());
        var adapter = new ControlledEffects(store)
        {
            LookupOutcome = new(DeliveryStatus.Succeeded, "looked-up-task"),
            OnLookup = () => store.Conflicts = 1
        };
        var reconciler = new OrchestrationReconciler();
        var original = reconciler.RunAsync(store, new Observations(), adapter, Simulation);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var recovered = await reconciler.RunAsync(store, new Observations { Ready = false }, adapter, Simulation);
        Assert.Equal(DeliveryStatus.Succeeded, recovered.State.Outbox["launch"].Status);
        adapter.Completion.SetResult(new EffectOutcome(DeliveryStatus.Unknown));
        var result = await original;
        Assert.Equal(1, adapter.Executions);
        Assert.Equal(1, adapter.Recoveries);
        Assert.Equal(0, store.Conflicts);
        Assert.Equal(DeliveryStatus.Succeeded, result.State.Outbox["launch"].Status);
        Assert.Equal("looked-up-task", result.State.Issues[7].Tasks["task"].ExternalId);
        Assert.True(result.State.Issues[7].Tasks["task"].LaunchConfirmed);
        Assert.Throws<InvalidOperationException>(() => new OrchestrationReducer().RecordOutcome(result.State,
            "launch", new(DeliveryStatus.Succeeded, "different-task"), Now));
    }

    [Fact]
    public async Task DuplicateResumeAndConcurrentReadinessHaveOneRepositoryOwner()
    {
        var store = new MemoryStore(new RepositoryState { Repository = "owner/repo" })
        {
            SynchronizeFirstTwoLoads = true
        };
        var reducer = new OrchestrationReducer(new Authorized());
        var reconciler = new OrchestrationReconciler(reducer);
        OrchestrationEvent Message(int number, string id, EventData data) =>
            new(id, "owner/repo", number, 1, 1, new Actor("maintainer", ActorKind.Human), Now, data);
        await Task.WhenAll(reconciler.DispatchAsync(store, Message(7, "ready7", new Ready())),
            reconciler.DispatchAsync(store, Message(8, "ready8", new Ready())));
        var resume = Message(7, "resume", new Resume());
        await Task.WhenAll(reconciler.DispatchAsync(store, resume), reconciler.DispatchAsync(store, resume));
        var adapter = new Effects(store);
        await Task.WhenAll(reconciler.RunAsync(store, new Observations(), adapter, new(false)),
            reconciler.RunAsync(store, new Observations(), adapter, new(false)));
        Assert.Equal(7, store.Current.AdmittedIssue);
        Assert.Equal(Phase.READY, store.Current.Issues[8].Phase);
        Assert.Single(store.Current.Outbox.Values, e => e.Kind == EffectKind.LaunchTask);
        Assert.Equal(3, store.Current.Inbox.Count);
    }

    [Fact]
    public async Task GitStoreWritesOneCommitWithObservedTipParentAndNonforceCas()
    {
        var transport = new GitTransport();
        var store = new GitStateStore(transport, "owner/repo", allowInitialization: true);
        var initial = await store.LoadAsync(default);
        Assert.Null(initial.Revision);
        initial.State.Version++;
        Assert.True(await store.TrySaveAsync(initial, initial.State, default));
        var first = transport.Tip!;
        var next = await store.LoadAsync(default);
        next.State.Version++;
        Assert.True(await store.TrySaveAsync(next, next.State, default));
        Assert.Equal(first, transport.Parents[transport.Tip!]);
        Assert.Equal(2, transport.Commits.Count);
        Assert.Equal(2, transport.Advances);
        Assert.Equal("state.json", transport.LastTreePath);
        Assert.False(await store.TrySaveAsync(initial, initial.State, default));
        Assert.Equal(2, transport.Commits.Count);
    }

    [Fact]
    public async Task GitConflictDoesNotPublishOrPretendSuccess()
    {
        var transport = new GitTransport { RejectAdvance = true };
        var store = new GitStateStore(transport, "owner/repo", allowInitialization: true);
        var initial = await store.LoadAsync(default);
        Assert.False(await store.TrySaveAsync(initial, initial.State, default));
        Assert.Null(transport.Tip);
        Assert.Single(transport.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostGitPublishResponseUsesExactCommitOrAncestry(bool descendant)
    {
        var transport = new GitTransport { LosePublishResponse = true, AddDescendant = descendant };
        var store = new GitStateStore(transport, "owner/repo", allowInitialization: true);
        var initial = await store.LoadAsync(default);
        initial.State.Version = 42;
        Assert.True(await store.TrySaveAsync(initial, initial.State, default));
        Assert.Equal(1, transport.Advances);
        Assert.Equal(descendant ? 1 : 0, transport.AncestryChecks);
        Assert.Equal(42, (await store.LoadAsync(default)).State.Version);
    }

    [Fact]
    public async Task LostUnpublishedBootstrapResponseFailsClosed()
    {
        var transport = new GitTransport { LoseBeforePublish = true };
        var store = new GitStateStore(transport, "owner/repo", allowInitialization: true);
        var initial = await store.LoadAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrySaveAsync(initial, initial.State, default));
        Assert.Null(transport.Tip);
    }

    [Fact]
    public async Task GitStoreRejectsCrossRepositoryOrUnsupportedSchema()
    {
        var transport = new GitTransport();
        var store = new GitStateStore(transport, "owner/repo", allowInitialization: true);
        var initial = await store.LoadAsync(default);
        initial.State.Repository = "other/repo";
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrySaveAsync(initial, initial.State, default));
        initial.State.Repository = "owner/repo";
        initial.State.SchemaVersion = 2;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrySaveAsync(initial, initial.State, default));
        Assert.Empty(transport.Commits);
    }

    [Fact]
    public async Task MissingCanonicalRefFailsClosedWithoutExplicitInitialization()
    {
        var transport = new GitTransport();
        var store = new GitStateStore(transport, "owner/repo");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        var empty = RepositoryState.Create("owner/repo");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrySaveAsync(new(null, empty), empty, default));
        Assert.Empty(transport.Commits);
        Assert.Equal(0, transport.Advances);
    }

    [Fact]
    public async Task DeletedRefCannotEraseAdmissionOrHoldsEvenForOriginalBootstrapStore()
    {
        var transport = new GitTransport();
        var bootstrap = new GitStateStore(transport, "owner/repo", allowInitialization: true);
        var initial = await bootstrap.LoadAsync(default);
        var admitted = CiPending();
        admitted.Issues[7].Phase = Phase.BLOCKED;
        admitted.Issues[7].ReactivationRequired = true;
        admitted.Outbox["ci"].Status = DeliveryStatus.Unknown;
        Assert.True(await bootstrap.TrySaveAsync(initial, admitted, default));
        var store = new GitStateStore(transport, "owner/repo");
        var loaded = await store.LoadAsync(default);
        Assert.Equal(7, loaded.State.AdmittedIssue);
        Assert.Equal(Phase.BLOCKED, loaded.State.Issues[7].Phase);
        transport.DeleteRef();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => bootstrap.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrySaveAsync(loaded, loaded.State, default));
        Assert.Single(transport.Commits);
        Assert.Equal(1, transport.Advances);
    }

    private static RepositoryState LaunchPending()
    {
        var state = CiPending();
        state.Issues[7].Ci = null;
        state.Issues[7].Phase = Phase.CODING;
        state.Issues[7].Tasks["task"].Status = TaskStatus.Intended;
        state.Issues[7].Tasks["task"].LaunchConfirmed = false;
        state.Outbox.Clear();
        state.Outbox["launch"] = new EffectIntent
        {
            Id = "launch", IssueNumber = 7, Generation = 1, Kind = EffectKind.LaunchTask,
            TaskId = "task", Round = 1, Sha = Head, Status = DeliveryStatus.Pending
        };
        return state;
    }

    private static RepositoryState CiPending()
    {
        var state = new RepositoryState { Repository = "owner/repo", AdmittedIssue = 7 };
        state.Issues[7] = new IssueState
        {
            Number = 7, ActiveAuthorized = true, ReadyAt = Now, Phase = Phase.CI_PENDING, PullRequest = 11, HeadSha = Head,
            Round = 1, ActiveTaskId = "task",
            Tasks = new()
            {
                ["task"] = new TaskIdentity
                {
                    Id = "task", Status = TaskStatus.Completed, FinalSha = Head, SourceSha = Head,
                    LaunchConfirmed = true, Round = 1
                }
            },
            Ci = new CiIdentity { IntentId = "ci", TaskId = "task", Round = 1, Sha = Head }
        };
        state.Outbox["ci"] = new EffectIntent
        {
            Id = "ci", IssueNumber = 7, Generation = 1, Kind = EffectKind.RunCi, Status = DeliveryStatus.Pending,
            TaskId = "task", Round = 1, Sha = Head, CreatedAt = Now
        };
        return state;
    }

    private sealed class Authorized : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => message.Actor.Login is
            "maintainer" or "task-service" or "ci-service" or "review-service";
    }

    private sealed class Observations : IObservationSource
    {
        public bool Ready { get; set; } = true;
        public string? HeadOverride { get; set; }
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken cancellationToken) =>
            Task.FromResult(new LiveObservation
            {
                ObservedAt = Now,
                Issues = state.Issues.ToDictionary(p => p.Key, p => new LiveIssueObservation
                {
                    Ready = Ready, PullRequest = p.Value.PullRequest ?? 11, HeadSha = HeadOverride ?? p.Value.HeadSha ?? Head,
                    CompletedTaskId = p.Value.ActiveTaskId, Labels = OrchestrationReducer.ProjectLabels(p.Value).ToList()
                })
            });
    }

    private sealed class Effects(MemoryStore store) : IOfflineEffectAdapter
    {
        public int Executions { get; private set; }
        public int Recoveries { get; private set; }
        public List<EffectKind> Kinds { get; } = [];
        public Action? AfterExecute { get; set; }
        public bool ThrowOnExecute { get; set; }
        public EffectOutcome RecoveryOutcome { get; set; } = new(DeliveryStatus.Succeeded, "external");
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken cancellationToken)
        {
            Assert.Equal(DeliveryStatus.Executing, store.Current.Outbox[intent.Id].Status);
            Executions++;
            Kinds.Add(intent.Kind);
            AfterExecute?.Invoke();
            if (ThrowOnExecute) throw new IOException("lost result");
            return Task.FromResult(new EffectOutcome(DeliveryStatus.Succeeded, "external"));
        }

        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken cancellationToken)
        {
            Assert.Contains(intent.Status, new[] { DeliveryStatus.Executing, DeliveryStatus.Unknown });
            Recoveries++;
            return Task.FromResult(RecoveryOutcome);
        }
    }

    private sealed class UnmarkedAdapter(Effects inner) : IEffectAdapter
    {
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(intent, cancellationToken);
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken cancellationToken) =>
            inner.ReconcileAsync(intent, cancellationToken);
    }

    private sealed class ControlledEffects(MemoryStore store) : IOfflineEffectAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EffectOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EffectOutcome LookupOutcome { get; set; } = new(DeliveryStatus.Unknown);
        public Action? OnLookup { get; set; }
        public int Executions { get; private set; }
        public int Recoveries { get; private set; }
        public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken cancellationToken)
        {
            Assert.Equal(DeliveryStatus.Executing, store.Current.Outbox[intent.Id].Status);
            if (intent.Kind is EffectKind.ProjectStatus or EffectKind.ProjectLabels)
                return new(DeliveryStatus.Succeeded, "projection");
            Executions++;
            Started.SetResult();
            return await Completion.Task.WaitAsync(cancellationToken);
        }
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken cancellationToken)
        {
            Assert.Contains(intent.Status, new[] { DeliveryStatus.Executing, DeliveryStatus.Unknown });
            Recoveries++;
            OnLookup?.Invoke();
            return Task.FromResult(LookupOutcome);
        }
    }

    private sealed class MemoryStore(RepositoryState state) : IStateStore
    {
        private readonly object gate = new();
        private RepositoryState state = Clone(state);
        private int revision;
        private int synchronizedReads;
        private readonly TaskCompletionSource readBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public int Conflicts { get; set; }
        public bool FailWrites { get; set; }
        public bool SynchronizeFirstTwoLoads { get; set; }
        public RepositoryState Current { get { lock (gate) return Clone(state); } }
        public async Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            StateSnapshot snapshot;
            var wait = false;
            lock (gate)
            {
                snapshot = new StateSnapshot(revision.ToString(), Clone(state));
                if (SynchronizeFirstTwoLoads && synchronizedReads < 2)
                {
                    wait = true;
                    if (++synchronizedReads == 2) readBarrier.SetResult();
                }
            }
            if (wait) await readBarrier.Task.WaitAsync(cancellationToken);
            return snapshot;
        }
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (FailWrites) throw new IOException("simulated crash");
                if (Conflicts > 0) { Conflicts--; revision++; return Task.FromResult(false); }
                if (expected.Revision != revision.ToString()) return Task.FromResult(false);
                state = Clone(next);
                revision++;
                Writes++;
                return Task.FromResult(true);
            }
        }
        private static RepositoryState Clone(RepositoryState source) => StateJson.Deserialize(StateJson.Serialize(source));
    }

    private sealed class GitTransport : IGitObjectRefTransport
    {
        public string? Tip { get; private set; }
        public Dictionary<string, GitCommitObject> Commits { get; } = [];
        public Dictionary<string, string?> Parents { get; } = [];
        private readonly Dictionary<string, string> blobs = [];
        private readonly Dictionary<string, string> trees = [];
        public string? LastTreePath { get; private set; }
        public int Advances { get; private set; }
        public int AncestryChecks { get; private set; }
        public bool RejectAdvance { get; set; }
        public bool LosePublishResponse { get; set; }
        public bool LoseBeforePublish { get; set; }
        public bool AddDescendant { get; set; }
        public void DeleteRef() => Tip = null;
        public Task<string?> ReadRefAsync(string reference, CancellationToken cancellationToken) => Task.FromResult(Tip);
        public Task<GitCommitObject> ReadCommitAsync(string commit, CancellationToken cancellationToken) => Task.FromResult(Commits[commit]);
        public Task<string> CreateBlobAsync(string content, CancellationToken cancellationToken)
        {
            var id = $"blob{blobs.Count}";
            blobs[id] = content;
            return Task.FromResult(id);
        }
        public Task<string> CreateTreeAsync(string path, string blob, CancellationToken cancellationToken)
        {
            LastTreePath = path;
            var id = $"tree{trees.Count}";
            trees[id] = blobs[blob];
            return Task.FromResult(id);
        }
        public Task<string> CreateCommitAsync(string tree, string? parent, string message, CancellationToken cancellationToken)
        {
            var id = $"commit{Commits.Count}";
            Commits[id] = new(id, trees[tree]);
            Parents[id] = parent;
            return Task.FromResult(id);
        }
        public Task<RefUpdateResult> TryAdvanceRefAsync(string reference, string? expectedTip, string commit, CancellationToken cancellationToken)
        {
            Advances++;
            if (LoseBeforePublish) throw new PublicationUncertainException("not published");
            if (RejectAdvance || Tip != expectedTip || Parents[commit] != expectedTip)
                return Task.FromResult(new RefUpdateResult(false));
            Tip = commit;
            if (AddDescendant)
            {
                var descendant = "descendant";
                Parents[descendant] = commit;
                Commits[descendant] = new(descendant, Commits[commit].StateJson);
                Tip = descendant;
            }
            if (LosePublishResponse) throw new PublicationUncertainException("published but response lost");
            return Task.FromResult(new RefUpdateResult(true));
        }
        public Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken cancellationToken)
        {
            AncestryChecks++;
            return Task.FromResult(Parents.GetValueOrDefault(descendant) == ancestor);
        }
    }
}
