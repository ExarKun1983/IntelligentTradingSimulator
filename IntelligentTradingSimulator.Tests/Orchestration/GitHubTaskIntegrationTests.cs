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

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task CommandOnlyRecoveryCannotStartQueuedWork(string operation)
    {
        var fixture = new ApiFixture { RunEvent = "workflow_dispatch" };
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new IssueState { Number = 7, Ready = true, Phase = Phase.READY, ReadyAt = Now };
        var store = new Store(state);
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation, reason = "Operator pause." }
        });

        if (operation == "pause")
        {
            var result = await host.ReconcileAsync(payload, "workflow_dispatch", "44", 1, 1, Now,
                [], 99, "master", 55, ApprovedFixture, Bounded, commandOnly: true);
            Assert.Equal(WorkerStopReason.EffectsDisabled, result.Reason);
            Assert.True(store.State.Issues[7].Paused);
        }
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => host.ReconcileAsync(payload,
                "workflow_dispatch", "44", 1, 1, Now, [], 99, "master", 55,
                ApprovedFixture, Bounded, commandOnly: true));

        Assert.Empty(store.State.Outbox);
        Assert.Empty(fixture.Writes);
    }

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
        Assert.True(committedReady.SinglePassTest);
        Assert.True(store.State.Issues[7].SinglePassTest);

        await host.ReconcileAsync(notification, "issues", "44", 1, 1, Now, [], 99, "master", 55,
            ApprovedFixture, Bounded, new FinalHead());

        Assert.Equal(digest, store.State.Inbox["actions:1:44"].Digest);
        Assert.Equal(1, Assert.IsType<Ready>(store.State.Inbox["actions:1:44"].Event!.Data).MaxRepairTasks);
        Assert.True(store.State.Issues[7].SinglePassTest);
        Assert.True(Assert.IsType<Ready>(store.State.Inbox["actions:1:44"].Event!.Data).SinglePassTest);
        Assert.Single(store.State.Issues[7].Tasks);
        Assert.Single(store.State.Outbox.Values, effect => effect.Kind == EffectKind.LaunchTask);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));

        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReconcileAsync(
            notification, "issues", "44", 1, 1, Now.AddMinutes(1), [], 99, "master", 55,
            ApprovedFixture, Bounded, new FinalHead()));

        Assert.Equal(digest, store.State.Inbox["actions:1:44"].Digest);
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Fact]
    public async Task BoundedRetestHostIgnoresRetiredIssueDriftAndContinuesReadyIssue()
    {
        var fixture = new ApiFixture();
        var state = RetiredAttemptState();
        var retiredHistory = JsonSerializer.Serialize(state.Issues[227], StateJson.Options);
        var retiredReceipts = JsonSerializer.Serialize(state.Inbox, StateJson.Options);
        state.Issues[237] = new IssueState
        {
            Number = 237, Generation = 1, Ready = true, ReadyAt = Now, Phase = Phase.READY,
            MaxRepairTasks = 1, SinglePassTest = true
        };
        var store = new Store(state);
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        var notification = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            issue = new { id = 2370, number = 237 }, action = "labeled", label = new { name = "agent:ready" }
        });

        await host.ReconcileAsync(notification, "issues", "44", 1, 1, Now, [], 99, "master", 55,
            ApprovedFixture, Bounded, new FinalHead(), boundedScope: new(237, 1, 227));

        Assert.Equal(237, store.State.AdmittedIssue);
        Assert.Contains(store.State.Outbox.Values, effect =>
            effect.IssueNumber == 237 && effect.Kind == EffectKind.LaunchTask);
        Assert.DoesNotContain(store.State.Outbox.Values, effect =>
            effect.IssueNumber == 227 && effect.Kind is EffectKind.ProjectLabels or EffectKind.ProjectStatus);
        Assert.Null(store.State.Issues[227].ProjectionDigest);
        Assert.Null(store.State.Issues[227].ObservedProjectionDigest);
        Assert.Equal(retiredHistory, JsonSerializer.Serialize(store.State.Issues[227], StateJson.Options));
        Assert.Equal(retiredReceipts, JsonSerializer.Serialize(
            store.State.Inbox.Where(entry => entry.Value.Event?.IssueNumber == 227)
                .ToDictionary(entry => entry.Key, entry => entry.Value), StateJson.Options));
        Assert.DoesNotContain(fixture.Requests, request => request.Contains("/issues/227", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Requests, request =>
            request.Contains("/tasks/e4743d38-6b27-49cd-a272-83b34465a808", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Writes, write => write.Path.Contains("/issues/227", StringComparison.Ordinal));
    }

    private static RepositoryState RetiredAttemptState()
    {
        const string taskId = "task:227:1:1:1";
        const string externalTaskId = "e4743d38-6b27-49cd-a272-83b34465a808";
        const string launchId = "LaunchTask:227:1:task:227:1:1:1:1";
        const string terminalId = "terminal-for:resolve-task-227";
        const string resolutionId = "resolve-task-227";
        const string reason = "Retire the stopped single-pass mapping attempt before separately approved retest #237.";
        var state = RepositoryState.Create("owner/repo");
        var launchAt = Now.AddMinutes(-3);
        var observedAt = Now.AddMinutes(-2);
        var resolvedAt = Now.AddMinutes(-1);
        state.Outbox[launchId] = new EffectIntent
        {
            Id = launchId, Digest = "launch-digest", IssueNumber = 227, Generation = 1,
            Kind = EffectKind.LaunchTask, Status = DeliveryStatus.Succeeded, TaskId = taskId,
            Round = 1, ExternalId = externalTaskId, CreatedAt = launchAt, StartedAt = launchAt,
            CompletedAt = launchAt.AddSeconds(1)
        };
        var evidence = new TaskTerminalEvidence(launchId, externalTaskId, state.Repository, 1, 55,
            "completed", 1, [new("session", externalTaskId, 1, "completed", observedAt)],
            observedAt, observedAt, new string('a', 64));
        var taskActor = new Actor("github-task-api", ActorKind.TaskService);
        var human = new Actor("maintainer", ActorKind.Human, 55, Type: "User");
        var terminal = new OrchestrationEvent(terminalId, state.Repository, 227, 1, 1,
            taskActor, observedAt, new TaskTerminalObserved(taskId, evidence));
        var resolution = new OrchestrationEvent(resolutionId, state.Repository, 227, 1, 1,
            human, resolvedAt, new ResolveTaskTerminal(taskId, terminalId, reason));
        state.Inbox[terminalId] = RetiredInbox(terminal, observedAt);
        state.Inbox[resolutionId] = RetiredInbox(resolution, resolvedAt);
        state.Issues[227] = new IssueState
        {
            Number = 227, Generation = 1, Phase = Phase.BLOCKED, ReactivationRequired = true,
            HoldReason = "Task PR mapping is unsupported or contradictory; admission retained.",
            Round = 1, MaxRepairTasks = 1, SinglePassTest = true, ActiveTaskId = taskId,
            Tasks = new()
            {
                [taskId] = new TaskIdentity
                {
                    Id = taskId, Kind = TaskKind.Coding, Status = TaskStatus.Failed, Round = 1,
                    LaunchSource = TaskLaunchSource.UnmappedIssue, LaunchConfirmed = true,
                    ExternalId = externalTaskId, TerminalEvidence = new(terminalId, evidence),
                    OperatorResolution = new(resolutionId, terminalId, human, reason, resolvedAt)
                }
            }
        };
        return state;
    }

    private static InboxEntry RetiredInbox(OrchestrationEvent message, DateTimeOffset at) =>
        new(message.Id, message.Digest, DeliveryStatus.Succeeded, at, at, JsonSerializer.Serialize(message));

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
    [InlineData(5000000000L)]
    [InlineData(long.MaxValue)]
    public async Task EmptyGlobalIdWithBranchEvidenceSupportsCompletionAndCandidateRevalidation(long id)
    {
        using var fixture = new ApiFixture
        {
            PullArtifact = true, IncludeBranch = true, ArtifactGlobalId = "", ArtifactDatabaseId = id
        };
        var store = new Store();
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var host = new GitHubOrchestrationHost(api, store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        await Run(host, fixture.Notification());
        await Run(host, fixture.Notification());

        var issue = store.State.Issues[7];
        var task = Assert.Single(issue.Tasks.Values);
        Assert.Equal(17, issue.PullRequest);
        Assert.Equal(TaskStatus.Completed, task.Status);
        Assert.Equal(Head, task.CandidateShaEvidence!.ObservedHeadSha);
        Assert.Equal(Head, issue.Ci!.Sha);
        await new GitHubTaskAdapter(api, store, "master", 55)
            .ValidateCandidateShaAsync(store.State, issue, default);
        Assert.Empty(fixture.GraphQueries);
        Assert.DoesNotContain(fixture.Requests, path => path.Contains($"/pulls/{id}"));
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.DoesNotContain(fixture.Writes, write => write.Path.Contains("dispatches") ||
            write.Path.Contains("requested_reviewers"));
    }

    [Theory]
    [InlineData("rest-database")]
    [InlineData("database")]
    [InlineData("no-prs")]
    [InlineData("multiple-prs")]
    [InlineData("duplicate-pull")]
    [InlineData("duplicate-branch")]
    [InlineData("branch-base")]
    [InlineData("blank-head-ref")]
    [InlineData("head-ref")]
    [InlineData("base")]
    [InlineData("base-repository")]
    [InlineData("head-repository")]
    [InlineData("closed-pr")]
    [InlineData("sha")]
    [InlineData("missing-global")]
    [InlineData("absent-global")]
    [InlineData("global-type")]
    [InlineData("global-length")]
    public async Task EmptyGlobalIdCompletionRejectsContradictoryEvidence(string corruption)
    {
        using var fixture = new ApiFixture { PullArtifact = true, IncludeBranch = true, ArtifactGlobalId = "" };
        var store = new Store();
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var host = new GitHubOrchestrationHost(api, store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        fixture.Corruption = corruption;

        var message = await new GitHubTaskAdapter(api, store, "master", 55)
            .ReadEventAsync(store.State, store.State.Issues[7], default);
        Assert.IsType<TaskStopped>(message!.Data);
        Assert.Null(store.State.Issues[7].Ci);
        Assert.Null(store.State.Issues[7].Review);
        Assert.Empty(fixture.GraphQueries);
    }

    [Theory]
    [InlineData("rest-database")]
    [InlineData("multiple-prs")]
    [InlineData("head-ref")]
    [InlineData("later-push")]
    [InlineData("missing-global")]
    public async Task EmptyGlobalIdCandidateRevalidationRejectsChangedEvidence(string corruption)
    {
        using var fixture = new ApiFixture { PullArtifact = true, IncludeBranch = true, ArtifactGlobalId = "" };
        var store = new Store();
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var host = new GitHubOrchestrationHost(api, store, new Clock());
        await Run(host, fixture.Notification());
        fixture.RemoteState = "completed";
        await Run(host, fixture.Notification());
        Assert.NotNull(Assert.Single(store.State.Issues[7].Tasks.Values).CandidateShaEvidence);

        fixture.Corruption = corruption;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubTaskAdapter(api, store, "master", 55)
                .ValidateCandidateShaAsync(store.State, store.State.Issues[7], default));
        Assert.Empty(fixture.GraphQueries);
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
        var diagnostics = new GitHubTaskLaunchDiagnostics();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock(),
            taskLaunchDiagnostics: diagnostics);
        Assert.Equal(WorkerStopReason.Blocked, (await Run(host, fixture.Notification())).Reason);
        var launch = store.State.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask);
        Assert.Equal(DeliveryStatus.Unknown, launch.Status);
        Assert.Null(launch.ExternalId);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Equal("failed", diagnostics.Snapshot!.Outcome);
        Assert.Equal("transport", diagnostics.Snapshot.ErrorCategory);
        Assert.Equal(nameof(HttpRequestException), diagnostics.Snapshot.ErrorType);
        Assert.Null(diagnostics.Snapshot.HttpStatus);
        Assert.Null(diagnostics.Snapshot.GitHubRequestId);

        await Run(host, fixture.Notification());
        Assert.Single(fixture.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.Equal(7, store.State.AdmittedIssue);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "http_status", "request:502:abc", "HttpRequestException")]
    [InlineData(HttpStatusCode.Created, "response_validation", "request:invalid:task", "KeyNotFoundException")]
    public async Task FailedLaunchRecordsSanitizedHttpAndResponseDiagnostics(HttpStatusCode status,
        string expectedCategory, string requestId, string expectedErrorType)
    {
        var fixture = new ApiFixture
        {
            TaskPostStatus = status,
            TaskPostRequestId = requestId,
            Corruption = status == HttpStatusCode.Created ? "missing-creator" : null
        };
        var store = new Store();
        using var client = fixture.Client();
        var diagnostics = new GitHubTaskLaunchDiagnostics();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock(),
            taskLaunchDiagnostics: diagnostics);

        Assert.NotEqual(WorkerStopReason.Settled, (await Run(host, fixture.Notification())).Reason);

        var diagnostic = Assert.IsType<GitHubTaskLaunchDiagnostic>(diagnostics.Snapshot);
        Assert.Equal("failed", diagnostic.Outcome);
        Assert.Equal((int)status, diagnostic.HttpStatus);
        Assert.Equal(requestId, diagnostic.GitHubRequestId);
        Assert.Equal(expectedCategory, diagnostic.ErrorCategory);
        Assert.Equal(expectedErrorType, diagnostic.ErrorType);
        Assert.Equal(status == HttpStatusCode.Created ? "Object" : null, diagnostic.ResponseKind);
        Assert.Equal(status == HttpStatusCode.Created ? true : null, diagnostic.HasTaskId);
        Assert.Equal(status == HttpStatusCode.Created ? true : null, diagnostic.HasRepository);
        Assert.Equal(status == HttpStatusCode.Created ? false : null, diagnostic.HasCreator);
        Assert.Equal(status == HttpStatusCode.Created ? true : null, diagnostic.HasState);
        Assert.Equal(DeliveryStatus.Unknown,
            store.State.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask).Status);
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
        private readonly string sha;

        public FinalHead(string? sha = null) => this.sha = sha ?? Head;

        public Task<TaskFinalHeadEvidence?> ReadAsync(string externalTaskId, CancellationToken token) =>
            Task.FromResult<TaskFinalHeadEvidence?>(new(externalTaskId, ["session"], 17, sha));
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
        public string ArtifactGlobalId { get; init; } = "PR_node";
        private List<string> labels = ["agent:ready"];
        public string RemoteState { get; set; } = "in_progress";
        public string HeadSha { get; set; } = Head;
        public string BaseBranch { get; init; } = "master";
        public string WorkflowPath { get; init; } = ".github/workflows/agent-state-machine.yml";
        public bool Draft { get; set; }
        public string? Corruption { get; set; }
        public string RunEvent { get; set; } = "issues";
        public bool LoseLaunchResponse { get; init; }
        public HttpStatusCode TaskPostStatus { get; init; } = HttpStatusCode.Created;
        public string? TaskPostRequestId { get; init; } = "task-create-request";
        public bool PermissionMismatch { get; init; }
        public Action? AfterLaunch { get; set; }
        private int launchCount;
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
                    var taskId = ++launchCount == 1 ? "remote-task" : $"remote-task-{launchCount}";
                    AfterLaunch?.Invoke();
                    if (LoseLaunchResponse) throw new HttpRequestException("Lost launch response.");
                    var response = Response(TaskResponse(taskId), TaskPostStatus);
                    if (TaskPostRequestId is not null)
                        response.Headers.Add("X-GitHub-Request-Id", TaskPostRequestId);
                    return response;
                }
                if (path == prefix + "issues/7/labels" || path == prefix + "issues/237/labels")
                    labels.AddRange(json.RootElement.GetProperty("labels").EnumerateArray().Select(value => value.GetString()!));
                else if (request.Method == HttpMethod.Delete &&
                         (path.StartsWith(prefix + "issues/7/labels/") || path.StartsWith(prefix + "issues/237/labels/")))
                {
                    var labelPrefix = path.StartsWith(prefix + "issues/237/", StringComparison.Ordinal)
                        ? prefix + "issues/237/labels/" : prefix + "issues/7/labels/";
                    labels.Remove(Uri.UnescapeDataString(path[labelPrefix.Length..]));
                }
                else if (path == prefix + "issues/7/comments" || path == prefix + "issues/237/comments" ||
                         path == prefix + "issues/comments/101")
                {
                    statusBody = json.RootElement.GetProperty("body").GetString()!;
                    return Response(new { id = 101, body = statusBody, user = new { id = 99 } });
                }
                else throw new InvalidOperationException("Unexpected mutation.");
                return Response(labels.Distinct().Select(name => new { name }).ToArray());
            }
            if (path == "/repos/owner/repo") result = new { id = 1, full_name = "owner/repo" };
            else if (path == prefix + "actions/runs/44")
                result = new { @event = RunEvent, path = WorkflowPath, actor = new { id = 55 } };
            else if (path == "/users/maintainer") result = new { id = 55, login = "maintainer", type = "User" };
            else if (path.Contains("/permission"))
            {
                var login = path.Contains("/reader/") ? "reader" : "maintainer";
                result = new { permission = login == "reader" ? "read" : "write",
                    user = new { id = PermissionMismatch ? 66 : 55, login, type = "User" } };
            }
            else if (path == prefix + "issues/7")
                result = new { id = 70, number = 7, state = "open", labels = labels.Distinct().Select(name => new { name }) };
            else if (path == prefix + "issues/237")
                result = new { id = 2370, number = 237, state = "open", labels = labels.Distinct().Select(name => new { name }) };
            else if (path.EndsWith("/comments"))
                result = path.Contains("/17/") ? PrComments.ToArray() : Comments.Concat(statusBody.Length == 0 ? [] :
                    new[] { JsonSerializer.SerializeToElement(new { id = 101, body = statusBody, updated_at = Now,
                        user = new { id = 99, type = "Bot", login = "projection" } }) }).ToArray();
            else if (path == prefix + "issues/7/labels" || path == prefix + "issues/237/labels")
                result = labels.Distinct().Select(name => new { name }).ToArray();
            else if (path.StartsWith("/agents/repos/owner/repo/tasks/", StringComparison.Ordinal))
                result = TaskResponse(Uri.UnescapeDataString(path["/agents/repos/owner/repo/tasks/".Length..]));
            else if (path == prefix + "pulls") result = Corruption == "no-prs" ? Array.Empty<object>() :
                Corruption == "multiple-prs" ? new[] { Pull(17), Pull(18) } : new[] { Pull(17) };
            else if (path == prefix + "pulls/17") result = Pull(17);
            else if (path == prefix + "pulls/18") result = Pull(18);
            else throw new InvalidOperationException($"Unexpected route {path}.");
            return Response(result);
        }

        private object TaskResponse(string taskId)
        {
            if (Corruption == "missing-creator")
                return new Dictionary<string, object?>
                {
                    ["id"] = taskId,
                    ["repository"] = new { id = 1 },
                    ["state"] = RemoteState
                };
            return new
            {
                id = Corruption == "task" ? "other-task" : taskId,
                repository = new { id = Corruption == "repository" ? 2 : 1 },
                creator = new { id = Corruption == "creator" ? 66 : 55 },
                state = Corruption == "failed" ? "failed" : Corruption == "waiting" ? "waiting_for_user" : RemoteState,
                updated_at = Now.AddMinutes(1), session_count = Corruption == "session-count" ? 2 : 1,
                sessions = new[] { new { id = "session", task_id = taskId, state = Corruption == "session" ? "in_progress" : "completed",
                    repository = new { id = 1 }, completed_at = Now.AddMinutes(1) } },
                artifacts = Artifacts()
            };
        }
        private object[] Artifacts()
        {
            if (Corruption == "artifact") return [];
            var artifacts = new List<object>();
            if (!PullArtifact || IncludeBranch)
                artifacts.Add(new { provider = "github", type = "branch",
                    data = new { head_ref = Corruption == "blank-head-ref" ? "" : "coding",
                        base_ref = Corruption == "branch-base" ? "other" : BaseBranch } });
            if (PullArtifact)
            {
                var data = new Dictionary<string, object?>
                {
                    ["id"] = Corruption == "database" ? 0 : ArtifactDatabaseId,
                    ["global_id"] = Corruption == "missing-global" ? null :
                        Corruption == "global-type" ? 42 :
                        Corruption == "global-length" ? new string('x', 257) : ArtifactGlobalId
                };
                if (Corruption == "absent-global") data.Remove("global_id");
                artifacts.Add(new { provider = "github", type = "pull",
                    data });
                if (Corruption == "duplicate-branch" && IncludeBranch) artifacts.Add(artifacts[0]);
                if (Corruption == "duplicate-pull") artifacts.Add(artifacts[^1]);
            }
            return artifacts.ToArray();
        }
        private object Pull(int number) => new
        {
            id = Corruption == "rest-database" ? ArtifactDatabaseId + 1 : ArtifactDatabaseId, number,
            state = Corruption == "closed-pr" ? "closed" : "open", merged = false, draft = Draft,
            head = new { sha = Corruption == "sha" ? "invalid" : Corruption == "later-push" ? new string('b', 40) : HeadSha,
                @ref = Corruption == "head-ref" ? "other" : "coding",
                repo = new { id = Corruption == "head-repository" ? 2 : 1 } },
            @base = new { @ref = Corruption == "base" ? "other" : BaseBranch,
                repo = new { id = Corruption == "base-repository" ? 2 : 1 } }
        };
        private static HttpResponseMessage Response(object result, HttpStatusCode status = HttpStatusCode.OK) => new(status)
            { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
    }
}
