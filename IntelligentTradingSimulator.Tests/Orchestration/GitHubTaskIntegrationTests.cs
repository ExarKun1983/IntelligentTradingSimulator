using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubTaskIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly string Head = new('a', 40);
    private static readonly GitHubExecutionApproval ApprovedFixture = new(true, true, true, true);
    private static readonly WorkerOptions Bounded = new() { MaximumIterations = 3, PollInterval = TimeSpan.Zero };

    [Fact]
    public async Task BoundedReadyReplayWithoutScopePreservesCommittedEventAndRejectsChangedContent()
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        var notification = fixture.Notification();
        var scope = new BoundedOrchestrationScope(7, 1);

        await host.ReconcileAsync(notification, "issues", "44", 1, 1, Now, [], 99, "master", 55,
            ApprovedFixture, Bounded, new FinalHead(), boundedScope: scope);

        var committed = store.State.Inbox["actions:1:44"];
        var digest = committed.Digest;
        var committedReady = Assert.IsType<Ready>(committed.Event!.Data);
        Assert.Equal(1, committedReady.MaxRepairTasks);

        await host.ReconcileAsync(notification, "issues", "44", 1, 1, Now, [], 99, "master", 55,
            ApprovedFixture, Bounded, new FinalHead());

        Assert.Equal(digest, store.State.Inbox["actions:1:44"].Digest);
        Assert.Equal(1, Assert.IsType<Ready>(store.State.Inbox["actions:1:44"].Event!.Data).MaxRepairTasks);
        Assert.Single(store.State.Issues[7].Tasks);
        Assert.Single(store.State.Outbox.Values, effect => effect.Kind == EffectKind.LaunchTask);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));

        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReconcileAsync(
            notification, "issues", "44", 1, 1, Now.AddMinutes(1), [], 99, "master", 55,
            ApprovedFixture, Bounded, new FinalHead()));

        Assert.Equal(digest, store.State.Inbox["actions:1:44"].Digest);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Theory]
    [InlineData(5000000000L, false)]
    [InlineData(5000000000L, true)]
    [InlineData(long.MaxValue, true)]
    public async Task PullNodeFullDatabaseIdPreservesInt64Identity(long id, bool asString)
    {
        var fixture = new ApiFixture { PullArtifact = true, ArtifactDatabaseId = id, NodeIdAsString = asString };
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        await Run(host, fixture.Notification());
        Assert.Equal(17, store.State.Issues[7].PullRequest);
        Assert.NotNull(store.State.Issues[7].Ci);
        Assert.All(fixture.GraphQueries, query => Assert.Contains("fullDatabaseId", query));
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PullGlobalNodeResolvesDatabaseIdentityWithoutUsingItAsPrNumber(bool includeBranch)
    {
        var fixture = new ApiFixture { PullArtifact = true, IncludeBranch = includeBranch };
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        await Run(host, fixture.Notification());
        await Run(host, fixture.Notification());
        Assert.Equal(17, store.State.Issues[7].PullRequest);
        Assert.Equal(Head, store.State.Issues[7].Ci!.Sha);
        Assert.Equal(TaskStatus.Completed, Assert.Single(store.State.Issues[7].Tasks.Values).Status);
        Assert.NotEmpty(fixture.GraphQueries);
        Assert.All(fixture.GraphQueries, body =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("PR_node", json.RootElement.GetProperty("variables").GetProperty("id").GetString());
            Assert.StartsWith("query(", json.RootElement.GetProperty("query").GetString());
        });
        Assert.DoesNotContain(fixture.Requests, path => path.Contains("/pulls/700"));
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.DoesNotContain(fixture.Writes, write => write.Path.Contains("dispatches") || write.Path.Contains("requested_reviewers"));
    }

    [Theory]
    [InlineData("missing-global")]
    [InlineData("global-length")]
    [InlineData("database")]
    [InlineData("node-null")]
    [InlineData("node-type")]
    [InlineData("node-database")]
    [InlineData("node-bigint")]
    [InlineData("node-overflow")]
    [InlineData("graphql-data-null")]
    [InlineData("node-repository")]
    [InlineData("node-name")]
    [InlineData("node-number")]
    [InlineData("graphql-errors")]
    [InlineData("rest-database")]
    [InlineData("base")]
    [InlineData("head-repository")]
    [InlineData("later-push")]
    [InlineData("closed-pr")]
    [InlineData("duplicate-pull")]
    [InlineData("branch-contradiction")]
    public async Task PullArtifactLookupContradictionsRetainHoldAndAdmission(string corruption)
    {
        var fixture = new ApiFixture { PullArtifact = true, IncludeBranch = corruption == "branch-contradiction" };
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        fixture.Corruption = corruption;
        await Run(host, fixture.Notification());
        await Run(host, fixture.Notification());
        Assert.Equal(corruption == "closed-pr" ? Phase.CLOSED : Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Null(store.State.Issues[7].Ci);
        Assert.Null(store.State.Issues[7].Review);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.DoesNotContain(fixture.Requests, path => path.Contains("/pulls/700"));
    }

    [Fact]
    public async Task CompletedTaskSnapshotsCurrentHeadWithExplicitBoundedPolicy()
    {
        var fixture = new ApiFixture { PullArtifact = true };
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        await Run(host, fixture.Notification());
        var task = Assert.Single(store.State.Issues[7].Tasks.Values);
        Assert.Equal(Head, task.CandidateShaEvidence!.ObservedHeadSha);
        Assert.Equal(TaskTerminalRecovery.CandidateShaPolicy, task.CandidateShaEvidence.Policy);
        Assert.Equal(task.Id, task.CandidateShaEvidence.TaskId);
        Assert.Equal(17, task.CandidateShaEvidence.PullRequest);
        Assert.Equal(new[] { "session" }, task.CandidateShaEvidence.SessionIds);
        Assert.NotNull(store.State.Issues[7].Ci);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("session")]
    [InlineData("task")]
    [InlineData("multiple-prs")]
    [InlineData("later-push")]
    public async Task CandidateEvidenceIsRevalidatedAgainstTaskMappingAndHead(string corruption)
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        await Run(host, fixture.Notification());
        await Run(host, fixture.Notification());
        Assert.NotNull(store.State.Issues[7].Tasks.Values.Single().CandidateShaEvidence);

        fixture.Corruption = corruption;
        var validator = new GitHubTaskAdapter(new(client, "owner/repo", 1), store, "master", 55);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            validator.ValidateCandidateShaAsync(store.State, store.State.Issues[7], default));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("creator")]
    [InlineData("task")]
    public async Task UncertainSavedLaunchLookupMismatchNeverClearsHoldOrRepeatsLaunch(string corruption)
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        var launch = store.State.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask);
        launch.Status = DeliveryStatus.Unknown;
        var issue = store.State.Issues[7];
        issue.Phase = Phase.BLOCKED;
        issue.ReactivationRequired = true;
        issue.HoldReason = "Operator hold retained";
        issue.Tasks[launch.TaskId!].Status = TaskStatus.Unknown;
        fixture.Corruption = corruption;
        await Run(host, fixture.Notification());
        Assert.Equal(DeliveryStatus.Unknown, store.State.Outbox[launch.Id].Status);
        Assert.True(store.State.Issues[7].ReactivationRequired);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Fact]
    public async Task SavedLaunchLookupConfirmsIdentityOnlyAndPreservesHumanHoldOnReplay()
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        var launch = store.State.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask);
        launch.Status = DeliveryStatus.Unknown;
        var issue = store.State.Issues[7];
        issue.Phase = Phase.BLOCKED;
        issue.ReactivationRequired = true;
        issue.HoldReason = "Operator hold retained";
        issue.Tasks[launch.TaskId!].Status = TaskStatus.Unknown;
        await Run(host, fixture.Notification());
        await Run(host, fixture.Notification());
        Assert.Equal(DeliveryStatus.Succeeded, store.State.Outbox[launch.Id].Status);
        Assert.Equal(TaskStatus.Running, store.State.Issues[7].Tasks[launch.TaskId!].Status);
        Assert.Equal("Operator hold retained", store.State.Issues[7].HoldReason);
        Assert.True(store.State.Issues[7].ReactivationRequired);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Null(store.State.Issues[7].Ci);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("digest")]
    [InlineData("external")]
    [InlineData("generation")]
    [InlineData("admission")]
    [InlineData("active-task")]
    [InlineData("round")]
    [InlineData("terminal-task")]
    public async Task RecoveryRejectsNoncanonicalIdentityBeforeAnyApiLookup(string corruption)
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var host = new GitHubOrchestrationHost(api, store, new Clock());
        await Run(host, fixture.Notification());
        var launch = store.State.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask);
        launch.Status = DeliveryStatus.Unknown;
        var candidate = JsonSerializer.Deserialize<EffectIntent>(JsonSerializer.Serialize(launch))!;
        switch (corruption)
        {
            case "intent": candidate.Id = "absent"; break;
            case "digest": candidate.Digest = "altered"; break;
            case "external": candidate.ExternalId = "another-task"; break;
            case "generation": candidate.Generation++; break;
            case "admission": store.State.AdmittedIssue = null; break;
            case "active-task": store.State.Issues[7].ActiveTaskId = "absent"; break;
            case "round": store.State.Issues[7].Tasks[launch.TaskId!].Round++; break;
            case "terminal-task": store.State.Issues[7].Tasks[launch.TaskId!].Status = TaskStatus.Completed; break;
        }
        fixture.Requests.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubTaskAdapter(api, store, "master", 55).ReconcileAsync(candidate, default));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task CompletedRestTaskUsesBoundedCandidateHeadWithoutClaimingProvenance()
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        await host.ReconcileAsync(fixture.Notification(), "issues", "44", 1, 1, Now,
            [], 99, "master", 55, ApprovedFixture, Bounded);
        var task = Assert.Single(store.State.Issues[7].Tasks.Values);
        Assert.Equal(Head, task.CandidateShaEvidence!.ObservedHeadSha);
        Assert.Equal(Head, store.State.Issues[7].Ci!.Sha);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Fact]
    public async Task NewlyMappedPrFeedbackIsDrainedBeforeCompletionCreatesAnyCiIntent()
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        fixture.PrComments.Add(Comment(3, "PR feedback before completion", Now.AddMinutes(1)));
        fixture.RemoteState = "completed";
        await Run(host, fixture.Notification(), options: Bounded with { MaximumIterations = 1 });
        var issue = store.State.Issues[7];
        Assert.Null(issue.Ci);
        Assert.Null(issue.Review);
        Assert.Equal(2, issue.Tasks.Count);
        var repair = issue.Tasks[issue.ActiveTaskId!];
        Assert.Equal(TaskKind.FeedbackRepair, repair.Kind);
        Assert.Contains("PR feedback before completion", fixture.Writes.Last(write => write.Path.StartsWith("/agents/")).Body);
        Assert.Single(repair.FeedbackEventIds);
    }

    [Fact]
    public async Task UserTokenStatusCommentsCannotLoopIntoHumanFeedback()
    {
        var fixture = new ApiFixture();
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new IssueState { Number = 7, FeedbackSince = Now, Phase = Phase.CODING };
        fixture.Comments.Add(Comment(1, StatusProjection.Render(state.Issues[7]), Now));
        fixture.Comments.Add(Comment(2, "ordinary instruction from same user", Now));
        using var client = fixture.Client();
        var events = await new GitHubFeedbackSource(new(client, "owner/repo", 1), 55).ReadAsync(state, default);
        Assert.Equal("ordinary instruction from same user", Assert.IsType<HumanFeedback>(Assert.Single(events).Data).Text);
    }

    [Fact]
    public async Task ReactivationCommitsNewGenerationBeforeFetchingFeedbackInTheSameInvocation()
    {
        var fixture = new ApiFixture();
        fixture.Comments.Add(Comment(1, "new generation feedback", Now));
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new IssueState { Number = 7, Phase = Phase.BLOCKED, ReactivationRequired = true, ReadyAt = Now };
        var store = new Store(state);
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        var notification = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation = "reactivate", reason = "Verified recovery" }
        });
        fixture.RunEvent = "workflow_dispatch";
        await host.ReconcileAsync(notification, "workflow_dispatch", "44", 1, 1, Now,
            [], 99, "master", 55, ApprovedFixture, Bounded, new FinalHead());
        Assert.Equal(2, store.State.Issues[7].Generation);
        Assert.Equal(2, Assert.Single(store.State.Issues[7].Feedback.Values).Generation);
        Assert.Equal("new generation feedback", Assert.Single(store.State.Issues[7].Feedback.Values).Text);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Fact]
    public async Task ReactivationReplayAfterPublicationResumesWithoutReactivatingAgain()
    {
        var fixture = new ApiFixture { RunEvent = "workflow_dispatch" };
        fixture.Comments.Add(Comment(1, "feedback after interrupted reactivation", Now));
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new IssueState { Number = 7, Phase = Phase.BLOCKED, ReactivationRequired = true };
        var store = new Store(state);
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        var notification = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation = "reactivate", reason = "Verified recovery" }
        });
        var yielded = await host.ReconcileAsync(notification, "workflow_dispatch", "44", 1, 1, Now,
            [], 99, "master", 55, ApprovedFixture, Bounded with { MaximumIterations = 1 }, new FinalHead());
        Assert.Equal(WorkerStopReason.IterationLimit, yielded.Reason);
        Assert.Equal(2, store.State.Issues[7].Generation);
        Assert.Empty(fixture.Writes);
        await host.ReconcileAsync(notification, "workflow_dispatch", "44", 1, 1, Now,
            [], 99, "master", 55, ApprovedFixture, Bounded, new FinalHead());
        await host.ReconcileAsync(notification, "workflow_dispatch", "44", 1, 1, Now,
            [], 99, "master", 55, ApprovedFixture, Bounded, new FinalHead());
        Assert.Equal(2, store.State.Issues[7].Generation);
        Assert.Single(store.State.Issues[7].Feedback);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReconcileAsync(notification,
            "workflow_dispatch", "44", 1, 1, Now.AddSeconds(1), [], 99, "master", 55,
            ApprovedFixture, Bounded, new FinalHead()));
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Fact]
    public async Task WriteCapableHostQueuesLaunchOnceAndOnlyCreatesCiIntentAfterVerifiedTaskCompletion()
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var host = new GitHubOrchestrationHost(api, store, new Clock());

        await Run(host, fixture.Notification(), options: Bounded);
        var task = store.State.Issues[7].Tasks.Values.Single();
        Assert.True(task.LaunchConfirmed);
        Assert.Equal("remote-task", task.ExternalId);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Equal(Phase.CODING, store.State.Issues[7].Phase);
        Assert.DoesNotContain(store.State.Outbox.Values, effect => effect.Kind is EffectKind.RunCi or EffectKind.RequestReview);
        var launch = Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.Contains("issues/7", launch.Body);
        using (var body = JsonDocument.Parse(launch.Body))
        {
            Assert.True(body.RootElement.GetProperty("create_pull_request").GetBoolean());
            Assert.Equal("master", body.RootElement.GetProperty("base_ref").GetString());
        }
        await Run(host, fixture.Notification(), options: Bounded);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));

        fixture.RemoteState = "completed";
        var result = await Run(host, fixture.Notification(), options: Bounded);
        Assert.Equal(WorkerStopReason.Blocked, result.Reason); // CI adapter/routing is deliberately not implemented.
        var issue = store.State.Issues[7];
        Assert.Equal(17, issue.PullRequest);
        Assert.Equal(Head, issue.HeadSha);
        Assert.Equal(TaskStatus.Completed, issue.Tasks[task.Id].Status);
        Assert.Equal(Head, issue.Ci!.Sha);
        Assert.Equal(task.Id, issue.Ci.TaskId);
        Assert.Equal(DeliveryStatus.Unknown, store.State.Outbox[issue.Ci.IntentId].Status);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.DoesNotContain(fixture.Writes, write => write.Path.Contains("dispatches") || write.Path.Contains("requested_reviewers"));
        Assert.True(issue.ReactivationRequired);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task HostRejectsUnverifiedActivationCredentialEntitlementAndCostBeforeIo(
        bool activation, bool credential, bool entitlement, bool cost)
    {
        var fixture = new ApiFixture();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), new Store());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReconcileAsync(fixture.Notification(),
            "issues", "44", 1, 1, Now, [], 99, "master", 55,
            new(activation, credential, entitlement, cost), Bounded));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task LostLaunchResponseRetainsUnknownIntentAndNeverRetriesCreationOnResume()
    {
        var fixture = new ApiFixture { LoseLaunchResponse = true };
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        Assert.Equal(WorkerStopReason.Blocked, (await Run(host, fixture.Notification())).Reason);
        var launch = store.State.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask);
        Assert.Equal(DeliveryStatus.Unknown, launch.Status);
        Assert.Null(launch.ExternalId);
        Assert.Equal(7, store.State.AdmittedIssue);

        await Run(host, fixture.Notification());
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.Equal(7, store.State.AdmittedIssue);
    }

    [Fact]
    public async Task CrashAfterLaunchBeforeOutcomeSaveLeavesExecutingIntentForLookupOnlyRecovery()
    {
        var fixture = new ApiFixture();
        var store = new Store();
        fixture.AfterLaunch = () => store.FailWrites = true;
        using var client = fixture.Client();
        var clock = new Clock();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, clock);
        await Assert.ThrowsAsync<IOException>(() => Run(host, fixture.Notification()));
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.Equal(DeliveryStatus.Executing,
            store.State.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask).Status);
        store.FailWrites = false;
        clock.UtcNow = Now.AddMinutes(31);
        await Run(host, fixture.Notification());
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("creator")]
    [InlineData("task")]
    [InlineData("session")]
    [InlineData("session-count")]
    [InlineData("artifact")]
    [InlineData("multiple-prs")]
    [InlineData("base")]
    [InlineData("head-repository")]
    [InlineData("sha")]
    [InlineData("later-push")]
    [InlineData("failed")]
    [InlineData("waiting")]
    public async Task ContradictoryTaskIdentitySessionOrArtifactStopsWithoutCi(string corruption)
    {
        var fixture = new ApiFixture();
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        fixture.Corruption = corruption;
        if (corruption == "sha")
            await Assert.ThrowsAsync<InvalidDataException>(() => Run(host, fixture.Notification()));
        else
            Assert.Equal(WorkerStopReason.Blocked, (await Run(host, fixture.Notification())).Reason);
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Null(store.State.Issues[7].Ci);
        Assert.Null(store.State.Issues[7].Review);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Fact]
    public async Task ConfirmedRepairTargetsSamePrAndPersistsBlockersAndDistinctFeedbackInPrompt()
    {
        var fixture = new ApiFixture();
        var state = RepositoryState.Create("owner/repo");
        var reducer = new OrchestrationReducer(new Allow());
        state = reducer.Apply(state, Message(new Ready(), "ready"));
        var live = new LiveObservation { ObservedAt = Now, Issues = new() { [7] = new() { Ready = true, PullRequest = 17, HeadSha = Head } } };
        state = reducer.Reconcile(state, live).State;
        var first = state.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask);
        state = reducer.MarkExecuting(state, first.Id, Now);
        state = reducer.RecordOutcome(state, first.Id, new(DeliveryStatus.Succeeded, "initial-task"), Now);
        state = reducer.Apply(state, Message(new TaskCompleted(first.TaskId!, 17, Head,
            new(TaskTerminalRecovery.CandidateShaEvidenceVersion, TaskTerminalRecovery.CandidateShaPolicy,
                first.TaskId!, "initial-task", ["session"], 17, Head)), "done", ActorKind.TaskService));
        live.Issues[7].CompletedTaskId = first.TaskId;
        state = reducer.Reconcile(state, live).State;
        var ci = state.Issues[7].Ci!;
        state = reducer.MarkExecuting(state, ci.IntentId, Now);
        state = reducer.RecordOutcome(state, ci.IntentId, new(DeliveryStatus.Succeeded, "run"), Now);
        state = reducer.Apply(state, Message(new CiResult("run", first.TaskId!, 1, 17, Head, false,
            [new("compiler", "Fix compilation", "CS0001 diagnostic")]), "ci", ActorKind.CiService));
        state = reducer.Apply(state, Message(new HumanFeedback("Same feedback"), "feedback-one"));
        state = reducer.Apply(state, Message(new HumanFeedback("Same feedback"), "feedback-two"));
        state = reducer.Reconcile(state, live).State;
        var repair = state.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask && effect.Status == DeliveryStatus.Pending);
        state = reducer.MarkExecuting(state, repair.Id, Now);
        var store = new Store(state);
        using var client = fixture.Client();
        var adapter = new GitHubTaskAdapter(new(client, "owner/repo", 1), store, "master", 55, ApprovedFixture);
        var outcome = await adapter.ExecuteAsync(store.State.Outbox[repair.Id], default);
        Assert.Equal(DeliveryStatus.Succeeded, outcome.Status);
        var request = Assert.Single(fixture.Writes);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("coding", body.RootElement.GetProperty("head_ref").GetString());
        Assert.False(body.RootElement.GetProperty("create_pull_request").GetBoolean());
        Assert.Contains("CS0001", body.RootElement.GetProperty("prompt").GetString()!);
        Assert.Contains("feedback-one", body.RootElement.GetProperty("prompt").GetString()!);
        Assert.Contains("feedback-two", body.RootElement.GetProperty("prompt").GetString()!);
    }

    [Fact]
    public async Task FeedbackApiAuthorizesCurrentHumanRevisionsAndPreservesDistinctIdenticalEvents()
    {
        var fixture = new ApiFixture();
        fixture.Comments.Add(Comment(1, "Same text", Now));
        fixture.Comments.Add(Comment(2, "Same text", Now));
        fixture.Comments.Add(Comment(3, "old", Now.AddSeconds(-1)));
        fixture.Comments.Add(Comment(4, "bot", Now, type: "Bot"));
        fixture.Comments.Add(Comment(5, "unauthorized", Now, login: "reader"));
        var state = RepositoryState.Create("owner/repo");
        var reducer = new OrchestrationReducer(new Allow());
        state = reducer.Apply(state, Message(new Ready(), "ready"));
        using var client = fixture.Client();
        var source = new GitHubFeedbackSource(new(client, "owner/repo", 1));
        var first = await source.ReadAsync(state, default);
        Assert.Equal(2, first.Count);
        Assert.NotEqual(first[0].Id, first[1].Id);
        foreach (var message in first) state = reducer.Apply(state, message);
        Assert.Empty(await source.ReadAsync(state, default));
        fixture.Comments[0] = Comment(1, "edited", Now.AddMinutes(1));
        var edit = Assert.Single(await source.ReadAsync(state, default));
        Assert.NotEqual(first[0].Id, edit.Id);
        Assert.Equal("edited", Assert.IsType<HumanFeedback>(edit.Data).Text);
        Assert.Equal(2, state.Issues[7].PendingFeedbackEventIds.Count);
    }

    [Fact]
    public async Task ReadyCommentFeedbackIsReadBeforeInitialLaunchAndDuringTaskCannotBeLostOnResume()
    {
        var fixture = new ApiFixture();
        fixture.Comments.Add(Comment(1, "initial-feedback", Now));
        var store = new Store();
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await Run(host, fixture.Notification());
        Assert.Contains("initial-feedback", fixture.Writes.Single(write => write.Path.StartsWith("/agents/")).Body);
        Assert.Empty(store.State.Issues[7].PendingFeedbackEventIds);
        fixture.Comments.Add(Comment(2, "after-launch", Now.AddMinutes(1)));
        await Run(host, fixture.Notification());
        var issue = store.State.Issues[7];
        Assert.Single(issue.PendingFeedbackEventIds);
        Assert.Equal(Phase.FIX_PENDING, issue.Phase);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        await Run(host, fixture.Notification());
        Assert.Single(store.State.Issues[7].PendingFeedbackEventIds);
        Assert.Null(store.State.Issues[7].Review);
        Assert.Null(store.State.Issues[7].Ci);
    }

    [Fact]
    public async Task UnauthorizedFeedbackIdentityMismatchFailsClosedWithoutAnyWrites()
    {
        var fixture = new ApiFixture { PermissionMismatch = true };
        fixture.Comments.Add(Comment(1, "new instruction", Now));
        using var client = fixture.Client();
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new IssueState { Number = 7, FeedbackSince = Now };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubFeedbackSource(new(client, "owner/repo", 1)).ReadAsync(state, default));
        Assert.Empty(fixture.Writes);
    }

    private static Task<WorkerResult> Run(GitHubOrchestrationHost host, JsonElement notification, WorkerOptions? options = null) =>
        host.ReconcileAsync(notification, "issues", "44", 1, 1, Now, [], 99, "master", 55,
            ApprovedFixture, options ?? Bounded, new FinalHead());

    internal sealed class FinalHead : ITaskFinalHeadSource
    {
        public Task<TaskFinalHeadEvidence?> ReadAsync(string externalTaskId, CancellationToken token) =>
            Task.FromResult<TaskFinalHeadEvidence?>(new(externalTaskId, ["session"], 17, Head));
    }

    private static OrchestrationEvent Message(EventData data, string id, ActorKind kind = ActorKind.Human) =>
        new(id, "owner/repo", 7, 1, 1, new("fixture", kind), Now, data);
    private static JsonElement Comment(int id, string body, DateTimeOffset updated, string type = "User", string login = "maintainer") =>
        JsonSerializer.SerializeToElement(new { id, body, updated_at = updated, user = new { id = 55, login, type } });

    private sealed class Allow : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => true;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    internal sealed class Store(RepositoryState? initial = null) : IStateStore
    {
        public RepositoryState State { get; private set; } = initial ?? RepositoryState.Create("owner/repo");
        public bool FailWrites { get; set; }
        public Task<StateSnapshot> LoadAsync(CancellationToken token) =>
            Task.FromResult(new StateSnapshot(State.Version.ToString(), StateJson.Deserialize(StateJson.Serialize(State))));
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState state, CancellationToken token)
        {
            if (FailWrites) throw new IOException("Injected write failure.");
            if (expected.Revision != State.Version.ToString()) return Task.FromResult(false);
            State = StateJson.Deserialize(StateJson.Serialize(state));
            return Task.FromResult(true);
        }
    }

    internal sealed class ApiFixture : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<(string Path, string Body)> Writes { get; } = [];
        public List<JsonElement> Comments { get; } = [];
        public List<JsonElement> PrComments { get; } = [];
        public List<string> GraphQueries { get; } = [];
        public bool PullArtifact { get; init; }
        public bool IncludeBranch { get; init; }
        public long ArtifactDatabaseId { get; init; } = 700;
        public bool NodeIdAsString { get; init; }
        private List<string> labels = ["agent:ready"];
        public string RemoteState { get; set; } = "in_progress";
        public string? Corruption { get; set; }
        public string RunEvent { get; set; } = "issues";
        public bool LoseLaunchResponse { get; init; }
        public bool PermissionMismatch { get; init; }
        public Action? AfterLaunch { get; set; }
        private string statusBody = "";
        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://api.github.com/") };
        public JsonElement Notification() => JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            issue = new { id = 70, number = 7 }, action = "labeled", label = new { name = "agent:ready" }
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path);
            const string prefix = "/repos/owner/repo/";
            object result;
            if (request.Method != HttpMethod.Get)
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                if (path == "/graphql")
                {
                    GraphQueries.Add(body);
                    if (Corruption == "graphql-data-null") return Response(new { data = (object?)null });
                    object? node = Corruption == "node-null" ? null : new
                    {
                        __typename = Corruption == "node-type" ? "Issue" : "PullRequest",
                        fullDatabaseId = Corruption == "node-bigint" ? (object)"not-an-id" :
                            Corruption == "node-overflow" ? (object)((decimal)long.MaxValue + 1) : NodeIdAsString
                            ? (object)ArtifactDatabaseId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : Corruption == "node-database" ? ArtifactDatabaseId + 1 : ArtifactDatabaseId,
                        number = Corruption == "node-number" ? 0 : Corruption == "branch-contradiction" ? 18 : 17,
                        repository = new { nameWithOwner = Corruption is "node-name" or "node-repository" ? "other/repo" : "owner/repo" }
                    };
                    return Response(new { data = new { node },
                        errors = Corruption == "graphql-errors" ? new[] { new { message = "Injected partial response" } } : [] });
                }
                Writes.Add((path, body));
                using var json = JsonDocument.Parse(body);
                if (path == "/agents/repos/owner/repo/tasks")
                {
                    AfterLaunch?.Invoke();
                    if (LoseLaunchResponse) throw new HttpRequestException("Lost launch response.");
                    return Response(TaskResponse());
                }
                if (path == prefix + "issues/7/labels")
                    labels.AddRange(json.RootElement.GetProperty("labels").EnumerateArray().Select(value => value.GetString()!));
                else if (request.Method == HttpMethod.Delete && path.StartsWith(prefix + "issues/7/labels/"))
                    labels.Remove(Uri.UnescapeDataString(path[(prefix + "issues/7/labels/").Length..]));
                else if (path == prefix + "issues/7/comments" || path == prefix + "issues/comments/101")
                {
                    statusBody = json.RootElement.GetProperty("body").GetString()!;
                    return Response(new { id = 101, body = statusBody, user = new { id = 99 } });
                }
                else throw new InvalidOperationException("Unexpected mutation.");
                return Response(labels.Distinct().Select(name => new { name }).ToArray());
            }
            if (path == "/repos/owner/repo") result = new { id = 1, full_name = "owner/repo" };
            else if (path == prefix + "actions/runs/44")
                result = new { @event = RunEvent, path = ".github/workflows/agent-state-machine.yml", actor = new { id = 55 } };
            else if (path == "/users/maintainer") result = new { id = 55, login = "maintainer", type = "User" };
            else if (path.Contains("/permission"))
            {
                var login = path.Contains("/reader/") ? "reader" : "maintainer";
                result = new { permission = login == "reader" ? "read" : "write",
                    user = new { id = PermissionMismatch ? 66 : 55, login, type = "User" } };
            }
            else if (path == prefix + "issues/7")
                result = new { id = 70, number = 7, state = "open", labels = labels.Distinct().Select(name => new { name }) };
            else if (path.EndsWith("/comments"))
                result = path.Contains("/17/") ? PrComments.ToArray() : Comments.Concat(statusBody.Length == 0 ? [] :
                    new[] { JsonSerializer.SerializeToElement(new { id = 101, body = statusBody, updated_at = Now,
                        user = new { id = 99, type = "Bot", login = "projection" } }) }).ToArray();
            else if (path == prefix + "issues/7/labels") result = labels.Distinct().Select(name => new { name }).ToArray();
            else if (path == "/agents/repos/owner/repo/tasks/remote-task") result = TaskResponse();
            else if (path == prefix + "pulls") result = Corruption == "multiple-prs" ? new[] { Pull(17), Pull(18) } : new[] { Pull(17) };
            else if (path == prefix + "pulls/17") result = Pull(17);
            else if (path == prefix + "pulls/18") result = Pull(18);
            else throw new InvalidOperationException($"Unexpected route {path}.");
            return Response(result);
        }

        private object TaskResponse() => new
        {
            id = Corruption == "task" ? "other-task" : "remote-task",
            repository = new { id = Corruption == "repository" ? 2 : 1 },
            creator = new { id = Corruption == "creator" ? 66 : 55 },
            state = Corruption == "failed" ? "failed" : Corruption == "waiting" ? "waiting_for_user" : RemoteState,
            updated_at = Now.AddMinutes(1), session_count = Corruption == "session-count" ? 2 : 1,
            sessions = new[] { new { id = "session", task_id = "remote-task", state = Corruption == "session" ? "in_progress" : "completed",
                repository = new { id = 1 }, completed_at = Now.AddMinutes(1) } },
            artifacts = Artifacts()
        };
        private object[] Artifacts()
        {
            if (Corruption == "artifact") return [];
            var artifacts = new List<object>();
            if (!PullArtifact || IncludeBranch)
                artifacts.Add(new { provider = "github", type = "branch", data = new { head_ref = "coding", base_ref = "master" } });
            if (PullArtifact)
            {
                artifacts.Add(new { provider = "github", type = "pull",
                    data = new { id = Corruption == "database" ? 0 : ArtifactDatabaseId,
                        global_id = Corruption == "missing-global" ? null : Corruption == "global-length" ? new string('x', 257) : "PR_node" } });
                if (Corruption == "duplicate-pull") artifacts.Add(artifacts[^1]);
            }
            return artifacts.ToArray();
        }
        private object Pull(int number) => new
        {
            id = Corruption == "rest-database" ? ArtifactDatabaseId + 1 : ArtifactDatabaseId, number,
            state = Corruption == "closed-pr" ? "closed" : "open", merged = false,
            head = new { sha = Corruption == "sha" ? "invalid" : Corruption == "later-push" ? new string('b', 40) : Head, @ref = "coding",
                repo = new { id = Corruption == "head-repository" ? 2 : 1 } },
            @base = new { @ref = Corruption == "base" ? "other" : "master", repo = new { id = 1 } }
        };
        private static HttpResponseMessage Response(object result) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
    }
}
