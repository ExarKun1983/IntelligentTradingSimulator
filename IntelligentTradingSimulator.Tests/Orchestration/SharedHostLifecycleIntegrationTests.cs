using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class SharedHostLifecycleIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly string Head = new('a', 40);
    private static readonly string Definition = new('d', 40);
    private static readonly GitHubExecutionApproval TaskApproval = new(true, true, true, true);
    private static readonly GitHubCiApproval CiApproval = new(true, true, true, true);
    private static readonly GitHubReviewApproval ReviewApproval = new(true, true, true, true);
    private static readonly GitHubRequiredStatusApproval StatusApproval = new(true, true, true, true, true);
    private static readonly GitHubStatusPublisher Publisher = new(99, "status-publisher", "publisher-node", "Bot");
    private static readonly GitHubCiRoute CiRoute = new(88, "refs/tags/trusted-ci", Definition);
    private static readonly WorkerOptions Bounded = new() { MaximumIterations = 3, PollInterval = TimeSpan.Zero };

    [Theory]
    [InlineData("unchanged")]
    [InlineData("feedback")]
    [InlineData("scheduled-feedback-contention")]
    [InlineData("head-changed")]
    [InlineData("request-changes")]
    [InlineData("request-changes-stale")]
    public async Task SharedHostWakeDrainsTaskCiReviewHumanClassificationAndPublishesOnlyExactSafeStatus(string afterApproval)
    {
        using var taskApi = new GitHubTaskIntegrationTests.ApiFixture();
        using var fixture = new PipelineApi(taskApi) { VerifyCredentials = true };
        using var client = new HttpClient(fixture, false) { BaseAddress = new("https://api.github.com/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "offline-workflow");
        using var userClient = new HttpClient(fixture, false) { BaseAddress = new("https://api.github.com/") };
        userClient.DefaultRequestHeaders.Authorization = new("Bearer", "offline-user");
        var store = new GitHubTaskIntegrationTests.Store();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock(),
            new(userClient, "owner/repo", 1));
        await host.ReconcileAsync(taskApi.Notification(), "issues", "44", 1, 1, Now, [],
            99, "master", 55, TaskApproval, Bounded, new GitHubTaskIntegrationTests.FinalHead(),
            reviewApproval: ReviewApproval, ciRoute: CiRoute, ciApproval: CiApproval,
            statusPublisher: Publisher, statusApproval: StatusApproval);
        await host.ReconcileAsync(taskApi.Notification(), "issues", "44", 1, 1, Now, [],
            99, "master", 55, TaskApproval, Bounded, new GitHubTaskIntegrationTests.FinalHead(),
            reviewApproval: ReviewApproval, ciRoute: CiRoute, ciApproval: CiApproval,
            statusPublisher: Publisher, statusApproval: StatusApproval);
        Assert.Equal(TaskStatus.Running, Assert.Single(store.State.Issues[7].Tasks.Values).Status);
        Assert.Single(taskApi.Writes, write => write.Path.StartsWith("/agents/"));

        taskApi.RemoteState = "completed";
        await Wake(host);
        Assert.Equal(TaskStatus.Completed, Assert.Single(store.State.Issues[7].Tasks.Values).Status);
        Assert.Equal(Head, store.State.Issues[7].Ci!.Sha);
        Assert.Null(store.State.Issues[7].Ci!.Green);
        Assert.Equal(1, fixture.CiDispatches);
        Assert.Equal(0, fixture.ReviewRequests);
        Assert.DoesNotContain(fixture.Statuses, status => status.GetProperty("state").GetString() == "success");

        fixture.CiCompleted = true;
        await Wake(host);
        Assert.True(store.State.Issues[7].Ci!.Green);
        Assert.Equal(1, fixture.ReviewRequests);
        Assert.Null(store.State.Issues[7].Review!.Classification);
        Assert.Equal("success", fixture.Statuses.Last().GetProperty("state").GetString());

        fixture.ReviewSubmitted = true;
        await Wake(host);
        var held = store.State.Issues[7];
        Assert.Equal(ReviewClassification.NeedsHuman, held.Review!.Classification);
        Assert.Equal(Phase.BLOCKED, held.Phase);
        Assert.True(held.ReactivationRequired);
        Assert.Null(store.State.AdmittedIssue);
        Assert.Equal("error", fixture.Statuses.Last().GetProperty("state").GetString());
        Assert.NotNull(held.Review.SourceEvidence);

        taskApi.RunEvent = "workflow_dispatch";
        var classification = Classification(held);
        await host.ReconcileAsync(classification, "workflow_dispatch", "91", 1, 1, Now.AddMinutes(3), [],
            99, "master", 55, TaskApproval, Bounded, new GitHubTaskIntegrationTests.FinalHead(),
            reviewApproval: ReviewApproval, ciRoute: CiRoute, ciApproval: CiApproval,
            statusPublisher: Publisher, statusApproval: StatusApproval);
        var accepted = store.State.Issues[7];
        Assert.Equal(Phase.HUMAN_REVIEW, accepted.Phase);
        Assert.Equal(ReviewClassification.Accepted, accepted.Review!.Classification);
        Assert.True(accepted.ReactivationRequired);
        Assert.Null(store.State.AdmittedIssue);
        var success = fixture.Statuses.Last();
        Assert.Equal("success", success.GetProperty("state").GetString());
        Assert.Equal(RequiredStatusPolicy.Context, success.GetProperty("context").GetString());
        Assert.All(fixture.StatusWrites, write => Assert.Equal("/repos/owner/repo/statuses/" + Head, write));
        Assert.DoesNotContain(fixture.StatusWrites, write => write.EndsWith(Definition));

        if (afterApproval.StartsWith("request-changes", StringComparison.Ordinal))
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                repository = new { id = 1, full_name = "owner/repo" },
                sender = new { id = 55, login = "maintainer", type = "User" },
                inputs = new { issue = "7", operation = "request-changes", reason = "Fix the human-reported edge case",
                    handoff = accepted.CurrentHandoffId, round = accepted.Round.ToString(), pull = "17", sha = Head }
            });
            Task<WorkerResult> Request() => host.ReconcileAsync(payload, "workflow_dispatch", "92", 1, 1,
                Now.AddMinutes(4), [], 99, "master", 55, TaskApproval, Bounded,
                new GitHubTaskIntegrationTests.FinalHead(), reviewApproval: ReviewApproval, ciRoute: CiRoute,
                ciApproval: CiApproval, statusPublisher: Publisher, statusApproval: StatusApproval);
            if (afterApproval == "request-changes-stale")
            {
                taskApi.Corruption = "later-push";
                var unchanged = StateJson.Serialize(store.State);
                await Assert.ThrowsAsync<InvalidDataException>(Request);
                Assert.Equal(unchanged, StateJson.Serialize(store.State));
                Assert.Single(taskApi.Writes, write => write.Path.StartsWith("/agents/"));
                return;
            }
            taskApi.AfterLaunch = () => taskApi.RemoteState = "in_progress";
            await Request();
            await Request();
            Assert.Equal(Phase.FIX_PENDING, store.State.Issues[7].Phase);
            Assert.Equal(2, taskApi.Writes.Count(write => write.Path.StartsWith("/agents/")));
            var repair = store.State.Issues[7].Tasks[store.State.Issues[7].ActiveTaskId!];
            Assert.Equal(TaskKind.FeedbackRepair, repair.Kind);
            Assert.Equal(TaskStatus.Running, repair.Status);
            Assert.Equal(17, store.State.Issues[7].PullRequest);
            Assert.Single(repair.FeedbackEventIds);
            Assert.Equal(1, fixture.CiDispatches);
            Assert.Equal(1, fixture.ReviewRequests);
            Assert.NotEqual("success", fixture.Statuses.Last().GetProperty("state").GetString());
            return;
        }

        if (afterApproval is "feedback" or "scheduled-feedback-contention")
        {
            fixture.WakeEvent = afterApproval == "feedback" ? "issue_comment" : "schedule";
            taskApi.Comments.Add(JsonSerializer.SerializeToElement(new
            {
                id = 200, body = "Human follow-up must revoke eligibility", updated_at = Now.AddMinutes(2),
                user = new { id = 55, login = "maintainer", type = "User" }
            }));
        }
        else if (afterApproval == "head-changed")
        {
            fixture.WakeEvent = "pull_request_target";
            taskApi.Corruption = "later-push";
        }
        if (afterApproval == "scheduled-feedback-contention")
        {
            store.State.WorkerLease = new("another-worker", Now.AddHours(1));
            Assert.Equal(WorkerStopReason.LeaseHeld, (await Wake(host))!.Reason);
            Assert.Empty(store.State.Issues[7].PendingFeedbackEventIds);
            Assert.Equal("success", fixture.Statuses.Last().GetProperty("state").GetString());
            store.State.WorkerLease = null;
        }
        await Wake(host, fixture.WakeEvent);
        var final = store.State.Issues[7];
        Assert.Equal(1, fixture.CiDispatches);
        Assert.Equal(1, fixture.ReviewRequests);
        Assert.Single(taskApi.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.Single(final.Tasks);
        if (afterApproval == "unchanged")
        {
            Assert.Equal(Phase.HUMAN_REVIEW, final.Phase);
            Assert.Equal("success", fixture.Statuses.Last().GetProperty("state").GetString());
        }
        else
        {
            Assert.Equal("error", fixture.Statuses.Last().GetProperty("state").GetString());
            Assert.True(final.ReactivationRequired);
            if (afterApproval is "feedback" or "scheduled-feedback-contention") Assert.Single(final.PendingFeedbackEventIds);
            else
            {
                Assert.Equal(Phase.BLOCKED, final.Phase);
                Assert.Equal(new string('b', 40), final.HeadSha);
                Assert.Equal(final.HeadSha, fixture.Statuses.Last().GetProperty("sha").GetString());
                Assert.Equal(Head, Assert.Single(final.Tasks.Values).FinalSha);
                Assert.Equal(Head, final.Ci!.Sha);
                Assert.Equal(Head, final.Review!.Sha);
            }
        }
    }

    [Fact]
    public async Task HumanClassificationCannotPublishSuccessAfterLiveHeadChanged()
    {
        using var taskApi = new GitHubTaskIntegrationTests.ApiFixture();
        using var fixture = new PipelineApi(taskApi);
        using var client = new HttpClient(fixture) { BaseAddress = new("https://api.github.com/") };
        var store = new GitHubTaskIntegrationTests.Store();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await host.ReconcileAsync(taskApi.Notification(), "issues", "44", 1, 1, Now, [],
            99, "master", 55, TaskApproval, Bounded, new GitHubTaskIntegrationTests.FinalHead(),
            reviewApproval: ReviewApproval, ciRoute: CiRoute, ciApproval: CiApproval,
            statusPublisher: Publisher, statusApproval: StatusApproval);
        taskApi.RemoteState = "completed";
        await Wake(host);
        fixture.CiCompleted = true;
        await Wake(host);
        fixture.ReviewSubmitted = true;
        await Wake(host);
        var classification = Classification(store.State.Issues[7]);
        taskApi.Corruption = "later-push";
        taskApi.RunEvent = "workflow_dispatch";
        var statusCount = fixture.Statuses.Count;
        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReconcileAsync(classification,
            "workflow_dispatch", "91", 1, 1, Now.AddMinutes(3), [], 99, "master", 55, TaskApproval, Bounded,
            new GitHubTaskIntegrationTests.FinalHead(), reviewApproval: ReviewApproval, ciRoute: CiRoute,
            ciApproval: CiApproval, statusPublisher: Publisher, statusApproval: StatusApproval));
        Assert.Equal(ReviewClassification.NeedsHuman, store.State.Issues[7].Review!.Classification);
        Assert.Equal(statusCount, fixture.Statuses.Count);
        Assert.Equal("error", fixture.Statuses.Last().GetProperty("state").GetString());
    }

    [Fact]
    public async Task PocDoesNotRequestReviewWhenDefinitionMovesAfterCiDispatch()
    {
        using var taskApi = new GitHubTaskIntegrationTests.ApiFixture
        {
            BaseBranch = GitHubPocSettings.BaseBranch, WorkflowPath = GitHubPocSettings.DriverWorkflowPath,
            Draft = true, RunEvent = "workflow_dispatch"
        };
        using var fixture = new PipelineApi(taskApi)
        {
            VerifyCredentials = true,
            Route = CiRoute with { Ref = GitHubPocSettings.DefinitionReference,
                WorkflowPath = GitHubPocSettings.CiWorkflowPath },
            WakeEvent = "workflow_dispatch"
        };
        using var client = new HttpClient(fixture, false) { BaseAddress = new("https://api.github.com/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "offline-workflow");
        using var userClient = new HttpClient(fixture, false) { BaseAddress = new("https://api.github.com/") };
        userClient.DefaultRequestHeaders.Authorization = new("Bearer", "offline-user");
        var initial = RepositoryState.Create("owner/repo");
        initial.IsolatedTestManifestDigest = "poc-manifest";
        var innerStore = new GitHubTaskIntegrationTests.Store(initial);
        IStateStore store = new PocStateStore(innerStore, "owner/repo", 7, "poc-manifest");
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock(),
            new(userClient, "owner/repo", 1));
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        Task VerifyDefinition(CancellationToken token) =>
            GitHubPocCommand.VerifyDefinitionAsync(api, Definition, token);
        JsonElement Command(string operation) => JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation }
        });
        Task<WorkerResult> Reconcile(string operation, string runId) =>
            host.ReconcileAsync(Command(operation), "workflow_dispatch", runId, 1, 1, Now, [], 99,
                GitHubPocSettings.BaseBranch, 55, TaskApproval, Bounded, token: default,
                reviewApproval: ReviewApproval, ciRoute: fixture.Route, ciApproval: CiApproval,
                boundedScope: new(7, 1, SinglePassTest: false),
                trustedWorkflowPath: GitHubPocSettings.DriverWorkflowPath, requireDraftPullRequest: true,
                beforeEffect: VerifyDefinition);

        await Reconcile("poc-start", "44");
        taskApi.RemoteState = "completed";
        await Reconcile("resume", "90");
        Assert.Equal(1, fixture.CiDispatches);

        fixture.DefinitionSha = new('e', 40);
        fixture.CiCompleted = true;
        await Reconcile("resume", "91");

        Assert.True(innerStore.State.Issues[7].Ci!.Green);
        Assert.Equal(0, fixture.ReviewRequests);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task OneRepairPoCCompletesCodingCiReviewRepairCiReviewAndStopsForHumanApproval(
        bool isolatedPoc, bool rejectNonDraftAcceptance, bool autoRepair)
    {
        var repairHead = new string('b', 40);
        var baseBranch = isolatedPoc ? GitHubPocSettings.BaseBranch : "master";
        var workflowPath = isolatedPoc ? GitHubPocSettings.DriverWorkflowPath : ".github/workflows/agent-state-machine.yml";
        var route = isolatedPoc ? CiRoute with
        {
            Ref = GitHubPocSettings.DefinitionReference, WorkflowPath = GitHubPocSettings.CiWorkflowPath
        } : CiRoute;
        BoundedOrchestrationScope? scope = isolatedPoc ? new(7, 1, SinglePassTest: false) : null;
        using var taskApi = new GitHubTaskIntegrationTests.ApiFixture
        {
            BaseBranch = baseBranch, WorkflowPath = workflowPath, Draft = isolatedPoc,
            RunEvent = isolatedPoc ? "workflow_dispatch" : "issues"
        };
        using var fixture = new PipelineApi(taskApi)
        {
            VerifyCredentials = true, Route = route, WakeEvent = isolatedPoc ? "workflow_dispatch" : "schedule",
            InlineFinding = autoRepair
        };
        using var client = new HttpClient(fixture, false) { BaseAddress = new("https://api.github.com/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "offline-workflow");
        using var userClient = new HttpClient(fixture, false) { BaseAddress = new("https://api.github.com/") };
        userClient.DefaultRequestHeaders.Authorization = new("Bearer", "offline-user");
        var initial = RepositoryState.Create("owner/repo");
        initial.IsolatedTestManifestDigest = isolatedPoc ? "poc-manifest" : null;
        var store = new GitHubTaskIntegrationTests.Store(initial);
        IStateStore guarded = isolatedPoc ? new PocStateStore(store, "owner/repo", 7, "poc-manifest") : store;
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), guarded, new Clock(),
            new(userClient, "owner/repo", 1));

        JsonElement Command(string operation) => JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation }
        });
        Task<WorkerResult> Reconcile(JsonElement payload, string eventName, string runId, string? head = null) =>
            host.ReconcileAsync(payload, eventName, runId, 1, 1, Now, [], 99, baseBranch, 55,
                TaskApproval, Bounded, new GitHubTaskIntegrationTests.FinalHead(head ?? taskApi.HeadSha),
                reviewApproval: ReviewApproval with { AutoRepairFindings = autoRepair }, ciRoute: route, ciApproval: CiApproval,
                statusPublisher: isolatedPoc ? null : Publisher, statusApproval: isolatedPoc ? null : StatusApproval,
                maxRepairTasks: 1, boundedScope: scope, trustedWorkflowPath: workflowPath,
                requireDraftPullRequest: isolatedPoc);
        async Task Advance(string? head = null)
        {
            if (isolatedPoc) await Reconcile(Command("resume"), "workflow_dispatch", "90", head);
            else await Wake(host, finalHeadSha: head ?? taskApi.HeadSha, autoRepair: autoRepair);
        }

        await Reconcile(isolatedPoc ? Command("poc-start") : taskApi.Notification(),
            isolatedPoc ? "workflow_dispatch" : "issues", "44");
        Assert.Equal(Phase.CODING, store.State.Issues[7].Phase);
        Assert.Equal(TaskStatus.Running, Assert.Single(store.State.Issues[7].Tasks.Values).Status);
        Assert.Single(taskApi.Writes, write => write.Path == "/agents/repos/owner/repo/tasks");

        taskApi.RemoteState = "completed";
        await Advance();
        Assert.Equal(1, fixture.CiDispatches);
        Assert.Equal(0, fixture.ReviewRequests);
        fixture.CiCompleted = true;
        await Advance();
        Assert.True(store.State.Issues[7].Ci!.Green);
        Assert.Equal(1, fixture.ReviewRequests);

        fixture.ReviewSubmitted = true;
        fixture.ReviewSubmittedCount = 1;
        taskApi.AfterLaunch = () => taskApi.RemoteState = "in_progress";
        await Advance();
        var firstReview = store.State.Issues[7];
        if (autoRepair)
        {
            Assert.Equal(ReviewClassification.Blocking, firstReview.Review!.Classification);
            Assert.Empty(firstReview.Classifications);
            Assert.Single(firstReview.Review.Findings);
        }
        else
        {
            Assert.Equal(ReviewClassification.NeedsHuman, firstReview.Review!.Classification);
            Assert.True(firstReview.ReactivationRequired);
            if (!isolatedPoc) Assert.Equal("error", fixture.Statuses.Last().GetProperty("state").GetString());
            taskApi.RunEvent = "workflow_dispatch";
            var blocking = Classification(firstReview, classification: "blocking",
                findings: """[{"Id":"poc-finding","Description":"A bounded regression needs correction","Evidence":"The test fixture records this review finding."}]""");
            await Reconcile(blocking, "workflow_dispatch", "91");
        }
        var repair = store.State.Issues[7];
        Assert.Equal(Phase.FIX_PENDING, repair.Phase);
        Assert.Equal(TaskKind.ReviewRepair, repair.Tasks[repair.ActiveTaskId!].Kind);
        Assert.Equal(TaskStatus.Running, repair.Tasks[repair.ActiveTaskId!].Status);
        Assert.Equal(1, repair.Tasks.Values.Count(task => task.Kind != TaskKind.Coding));
        Assert.Equal(1, fixture.CiDispatches);
        Assert.Equal(1, fixture.ReviewRequests);

        taskApi.HeadSha = repairHead;
        taskApi.RemoteState = "completed";
        fixture.CiCompleted = false;
        fixture.ReviewSubmittedCount = 1;
        await Advance(repairHead);
        Assert.Equal(2, fixture.CiDispatches);
        Assert.Equal(repairHead, store.State.Issues[7].Ci!.Sha);
        Assert.Null(store.State.Issues[7].Ci!.Green);
        Assert.Equal(1, fixture.ReviewRequests);

        fixture.CiCompleted = true;
        fixture.ReviewSubmitted = false;
        await Advance(repairHead);
        Assert.True(store.State.Issues[7].Ci!.Green);
        Assert.Equal(2, fixture.ReviewRequests);
        fixture.ReviewSubmittedCount = 2;
        await Advance(repairHead);

        var finalReview = store.State.Issues[7];
        Assert.Equal(ReviewClassification.NeedsHuman, finalReview.Review!.Classification);
        Assert.Equal(repairHead, finalReview.Review.Sha);
        if (!isolatedPoc) Assert.Equal("error", fixture.Statuses.Last().GetProperty("state").GetString());

        var accepted = Classification(finalReview,
            responses: JsonSerializer.Serialize(new[] { new
            {
                FindingId = autoRepair ? "copilot-review-comment:601" : "poc-finding",
                Evidence = "Human verified the repaired behavior on the new exact head.", Resolved = true
            } }),
            sha: repairHead);
        taskApi.RunEvent = "workflow_dispatch";
        if (rejectNonDraftAcceptance)
        {
            var before = StateJson.Serialize(store.State);
            taskApi.Draft = false;
            await Assert.ThrowsAsync<InvalidDataException>(() => Reconcile(accepted, "workflow_dispatch", "92", repairHead));
            Assert.Equal(before, StateJson.Serialize(store.State));
            Assert.Equal(2, fixture.CiDispatches);
            Assert.Equal(2, fixture.ReviewRequests);
            return;
        }
        await Reconcile(accepted, "workflow_dispatch", "92", repairHead);

        var handoff = store.State.Issues[7];
        Assert.Equal(Phase.HUMAN_REVIEW, handoff.Phase);
        Assert.Equal(ReviewClassification.Accepted, handoff.Review!.Classification);
        Assert.True(handoff.ReactivationRequired);
        Assert.Null(store.State.AdmittedIssue);
        Assert.Equal(2, handoff.Tasks.Count);
        Assert.Equal(2, fixture.CiDispatches);
        Assert.Equal(2, fixture.ReviewRequests);
        if (isolatedPoc)
        {
            Assert.Empty(fixture.StatusWrites);
            Assert.False(handoff.SinglePassTest);
            Assert.Equal(1, handoff.MaxRepairTasks);
            Assert.All(taskApi.Writes.Where(write => write.Path == "/agents/repos/owner/repo/tasks"), write =>
            {
                using var body = JsonDocument.Parse(write.Body);
                Assert.Equal(baseBranch, body.RootElement.GetProperty("base_ref").GetString());
            });
            var candidate = new GitHubTaskAdapter(new(client, "owner/repo", 1), guarded, baseBranch, 55,
                TaskApproval, taskApi: new(userClient, "owner/repo", 1), requireDraftPullRequest: true);
            Assert.True(await GitHubPocCommand.IsCompleteAsync(store.State, handoff, candidate, default));
            taskApi.Draft = false;
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                GitHubPocCommand.IsCompleteAsync(store.State, handoff, candidate, default));
            taskApi.Draft = true;
            taskApi.Corruption = "base";
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                GitHubPocCommand.IsCompleteAsync(store.State, handoff, candidate, default));
            taskApi.Corruption = null;
            taskApi.Comments.Add(JsonSerializer.SerializeToElement(new
            {
                id = 200, body = "Please address this follow-up before completion.",
                updated_at = Now.AddMinutes(1),
                user = new { id = 55, login = "maintainer", type = "User" }
            }));
            await Reconcile(Command("resume"), "workflow_dispatch", "93", repairHead);
            Assert.Equal(Phase.HUMAN_REVIEW, store.State.Issues[7].Phase);
            Assert.Single(store.State.Issues[7].PendingFeedbackEventIds);
            Assert.False(await GitHubPocCommand.IsCompleteAsync(store.State,
                store.State.Issues[7], candidate, default));
        }
        else
        {
            Assert.Equal(new[] { Head, repairHead }, fixture.StatusWrites
                .Where(path => path.StartsWith("/repos/owner/repo/statuses/", StringComparison.Ordinal))
                .Select(path => path["/repos/owner/repo/statuses/".Length..])
                .Distinct().OrderBy(sha => sha, StringComparer.Ordinal));
            Assert.Equal("success", fixture.Statuses.Last().GetProperty("state").GetString());
        }
        Assert.DoesNotContain(taskApi.Writes, write => write.Path.EndsWith("/merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledWakeDispatchesOnlyThePersistedBoundedCandidateSha()
    {
        using var taskApi = new GitHubTaskIntegrationTests.ApiFixture();
        using var fixture = new PipelineApi(taskApi);
        using var client = new HttpClient(fixture) { BaseAddress = new("https://api.github.com/") };
        var store = new GitHubTaskIntegrationTests.Store();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await host.ReconcileAsync(taskApi.Notification(), "issues", "44", 1, 1, Now, [],
            99, "master", 55, TaskApproval, Bounded, reviewApproval: ReviewApproval,
            ciRoute: CiRoute, ciApproval: CiApproval, statusPublisher: Publisher, statusApproval: StatusApproval);
        taskApi.RemoteState = "completed";
        await host.ReconcileWakeAsync(JsonSerializer.SerializeToElement(new
            { repository = new { id = 1, full_name = "owner/repo" } }), "schedule", "90",
            new(10, Definition), new(true, true, true, true), 99, "master", 55, TaskApproval, Bounded,
            reviewApproval: ReviewApproval, ciRoute: CiRoute, ciApproval: CiApproval,
            statusPublisher: Publisher, statusApproval: StatusApproval);
        var issue = store.State.Issues[7];
        var task = Assert.Single(issue.Tasks.Values);
        Assert.Equal(Phase.CI_PENDING, issue.Phase);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Equal(Head, task.CandidateShaEvidence!.ObservedHeadSha);
        Assert.Equal(task.CandidateShaEvidence.ObservedHeadSha, issue.Ci!.Sha);
        Assert.Equal(1, fixture.CiDispatches);
        Assert.Equal(0, fixture.ReviewRequests);
        Assert.DoesNotContain(fixture.Statuses, status => status.GetProperty("state").GetString() == "success");
        Assert.Single(taskApi.Writes, write => write.Path.StartsWith("/agents/"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialRunningPrMappingDefersIntermediatePushAndReadsPrFeedbackWithoutFinalSha(bool feedbackDuringMapping)
    {
        using var taskApi = new GitHubTaskIntegrationTests.ApiFixture();
        using var fixture = new PipelineApi(taskApi);
        using var client = new HttpClient(fixture) { BaseAddress = new("https://api.github.com/") };
        var store = new GitHubTaskIntegrationTests.Store();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        var comment = JsonSerializer.SerializeToElement(new
        {
            id = 201, body = "Feedback on the new initial task PR", updated_at = Now.AddMinutes(2),
            user = new { id = 55, login = "maintainer", type = "User" }
        });
        if (feedbackDuringMapping) taskApi.PrComments.Add(comment);
        await host.ReconcileAsync(taskApi.Notification(), "issues", "44", 1, 1, Now, [],
            99, "master", 55, TaskApproval, Bounded, reviewApproval: ReviewApproval,
            ciRoute: CiRoute, ciApproval: CiApproval, statusPublisher: Publisher, statusApproval: StatusApproval);
        var issue = store.State.Issues[7];
        Assert.Equal(17, issue.PullRequest);
        Assert.Equal(Head, issue.HeadSha);
        Assert.Equal(TaskStatus.Running, Assert.Single(issue.Tasks.Values).Status);
        Assert.Null(Assert.Single(issue.Tasks.Values).FinalSha);
        Assert.Equal(feedbackDuringMapping ? Phase.FIX_PENDING : Phase.CODING, issue.Phase);
        Assert.Single(store.State.Inbox.Values, entry => entry.Event?.Data is TaskPrMapped);
        Assert.False(ManagedPushRouting.Decide(store.State, new("owner/repo", 17, Head, Head), true).RunFullCi);
        if (!feedbackDuringMapping) taskApi.PrComments.Add(comment);
        await host.ReconcileWakeAsync(JsonSerializer.SerializeToElement(new
            { repository = new { id = 1, full_name = "owner/repo" } }), "schedule", "90",
            new(10, Definition), new(true, true, true, true), 99, "master", 55, TaskApproval, Bounded,
            reviewApproval: ReviewApproval, ciRoute: CiRoute, ciApproval: CiApproval,
            statusPublisher: Publisher, statusApproval: StatusApproval);
        issue = store.State.Issues[7];
        var feedback = Assert.Single(issue.Feedback.Values);
        Assert.Equal(Head, feedback.Sha);
        Assert.Single(issue.PendingFeedbackEventIds);
        Assert.Equal(TaskStatus.Running, Assert.Single(issue.Tasks.Values).Status);
        Assert.Null(Assert.Single(issue.Tasks.Values).FinalSha);
        Assert.Null(issue.Ci);
        Assert.Null(issue.Review);
        Assert.Equal(0, fixture.CiDispatches);
        Assert.Equal(0, fixture.ReviewRequests);
        Assert.Single(taskApi.Writes, write => write.Path.StartsWith("/agents/"));
        Assert.Equal(7, store.State.AdmittedIssue);
    }

    [Fact]
    public async Task HumanClassificationRequiresFindingSpecificResolutionOfRecordedBlockers()
    {
        using var taskApi = new GitHubTaskIntegrationTests.ApiFixture();
        using var fixture = new PipelineApi(taskApi);
        using var client = new HttpClient(fixture) { BaseAddress = new("https://api.github.com/") };
        var store = new GitHubTaskIntegrationTests.Store();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock());
        await host.ReconcileAsync(taskApi.Notification(), "issues", "44", 1, 1, Now, [],
            99, "master", 55, TaskApproval, Bounded, new GitHubTaskIntegrationTests.FinalHead(),
            reviewApproval: ReviewApproval, ciRoute: CiRoute, ciApproval: CiApproval,
            statusPublisher: Publisher, statusApproval: StatusApproval);
        taskApi.RemoteState = "completed";
        await Wake(host);
        fixture.CiCompleted = true;
        await Wake(host);
        fixture.ReviewSubmitted = true;
        await Wake(host);
        var issue = store.State.Issues[7];
        issue.Review!.Findings.Add(new("retained", "Prior unresolved finding", "Prior round evidence"));
        taskApi.RunEvent = "workflow_dispatch";
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReconcileAsync(Classification(issue),
            "workflow_dispatch", "91", 1, 1, Now.AddMinutes(3), [], 99, "master", 55, TaskApproval, Bounded,
            new GitHubTaskIntegrationTests.FinalHead(), reviewApproval: ReviewApproval, ciRoute: CiRoute,
            ciApproval: CiApproval, statusPublisher: Publisher, statusApproval: StatusApproval));
        Assert.Single(store.State.Issues[7].Review!.Findings);
        Assert.Equal(ReviewClassification.NeedsHuman, store.State.Issues[7].Review!.Classification);
        Assert.Equal("error", fixture.Statuses.Last().GetProperty("state").GetString());
        await host.ReconcileAsync(Classification(issue,
                """[{"FindingId":"retained","Evidence":"Human checked the corrected behavior against the finding","Resolved":true}]"""),
            "workflow_dispatch", "91", 1, 1, Now.AddMinutes(3), [], 99, "master", 55, TaskApproval, Bounded,
            new GitHubTaskIntegrationTests.FinalHead(), reviewApproval: ReviewApproval, ciRoute: CiRoute,
            ciApproval: CiApproval, statusPublisher: Publisher, statusApproval: StatusApproval);
        Assert.Equal(Phase.HUMAN_REVIEW, store.State.Issues[7].Phase);
        Assert.Single(store.State.Issues[7].Review!.Responses);
        Assert.Empty(store.State.Issues[7].Review!.Findings);
        Assert.Equal("success", fixture.Statuses.Last().GetProperty("state").GetString());
    }

    private static Task<WorkerResult?> Wake(GitHubOrchestrationHost host, string eventName = "schedule",
        string? finalHeadSha = null, bool autoRepair = false) =>
        host.ReconcileWakeAsync(JsonSerializer.SerializeToElement(new
            { repository = new { id = 1, full_name = "owner/repo" } }), eventName, "90",
            new(10, Definition), new(true, true, true, true), 99, "master", 55, TaskApproval, Bounded,
            new GitHubTaskIntegrationTests.FinalHead(finalHeadSha),
            reviewApproval: ReviewApproval with { AutoRepairFindings = autoRepair }, ciRoute: CiRoute,
            ciApproval: CiApproval, statusPublisher: Publisher, statusApproval: StatusApproval);
    private static JsonElement Classification(IssueState issue, string responses = "[]",
        string classification = "accepted", string findings = "[]", string? sha = null) => JsonSerializer.SerializeToElement(new
    {
        repository = new { id = 1, full_name = "owner/repo" },
        sender = new { id = 55, login = "maintainer", type = "User" },
        inputs = new { issue = "7", operation = "classify-review", reason = "Human checked exact captured round",
            handoff = issue.CurrentHandoffId, round = issue.Round.ToString(), pull = "17",
            sha = sha ?? issue.HeadSha ?? Head, classification, findings, responses }
    });
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddMinutes(3); }

    private sealed class PipelineApi : HttpMessageHandler
    {
        private const string Reviewer = "copilot-pull-request-reviewer[bot]";
        private readonly HttpMessageInvoker tasks;
        private readonly GitHubTaskIntegrationTests.ApiFixture taskApi;
        public bool CiCompleted { get; set; }
        public bool ReviewSubmitted { get; set; }
        public bool InlineFinding { get; init; }
        public int ReviewSubmittedCount { get; set; }
        public bool VerifyCredentials { get; init; }
        public GitHubCiRoute Route { get; init; } = CiRoute;
        public string WakeEvent { get; set; } = "schedule";
        public string DefinitionSha { get; set; } = Definition;
        public int CiDispatches { get; private set; }
        public int ReviewRequests { get; private set; }
        public List<JsonElement> Statuses { get; } = [];
        public List<string> StatusWrites { get; } = [];
        private readonly Dictionary<int, (string Correlation, string Sha)> ciRuns = [];
        private readonly List<string> reviewShas = [];
        private static object ReviewActor => new { id = 175728472, node_id = "BOT_kgDOCnlnWA", login = "Copilot", type = "Bot" };
        private static object StatusActor => new { id = 99, node_id = "publisher-node", login = "status-publisher", type = "Bot" };
        public PipelineApi(GitHubTaskIntegrationTests.ApiFixture tasks)
        {
            taskApi = tasks;
            this.tasks = new(tasks, false);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (VerifyCredentials)
                Assert.Equal(path.StartsWith("/agents/") ? "offline-user" : "offline-workflow",
                    request.Headers.Authorization?.Parameter);
            const string prefix = "/repos/owner/repo/";
            object response;
            if (path == prefix + "actions/runs/90")
                response = new { id = 90, workflow_id = 10, path = taskApi.WorkflowPath,
                    head_sha = Definition, @event = WakeEvent, repository = new { id = 1 }, actor = new { id = 55 } };
            else if (path == prefix + "actions/runs/91" || path == prefix + "actions/runs/92" ||
                     path == prefix + "actions/runs/93")
                response = new { @event = "workflow_dispatch", path = taskApi.WorkflowPath,
                    actor = new { id = 55 } };
            else if (path == prefix + "actions/workflows/88")
                response = new { id = 88, path = Route.WorkflowPath, state = "active" };
            else if (path.StartsWith(prefix + "commits/refs"))
                response = new { sha = DefinitionSha };
            else if (path == prefix + "actions/workflows/88/dispatches")
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal(Route.Ref, payload.RootElement.GetProperty("ref").GetString());
                var sha = payload.RootElement.GetProperty("inputs").GetProperty("sha").GetString()!;
                Assert.Equal(taskApi.HeadSha, sha);
                CiDispatches++;
                var runId = 776 + CiDispatches;
                ciRuns.Add(runId, (payload.RootElement.GetProperty("inputs").GetProperty("correlation").GetString()!, sha));
                response = new { workflow_run_id = runId, run_url = $"https://api.github.com/repos/owner/repo/actions/runs/{runId}" };
            }
            else if (path.StartsWith(prefix + "actions/runs/", StringComparison.Ordinal) &&
                int.TryParse(path[(prefix + "actions/runs/").Length..], out var ciRunId) &&
                ciRuns.TryGetValue(ciRunId, out var ciRun))
                response = new { id = ciRunId, workflow_id = 88, path = Route.WorkflowPath,
                    @event = "workflow_dispatch", head_sha = Definition, run_attempt = 1,
                    display_title = ciRun.Correlation, repository = new { id = 1 },
                    status = CiCompleted ? "completed" : "in_progress", conclusion = CiCompleted ? "success" : null,
                    updated_at = Now.AddMinutes(3) };
            else if (path.StartsWith(prefix + "actions/runs/", StringComparison.Ordinal) &&
                path.EndsWith("/jobs", StringComparison.Ordinal) &&
                int.TryParse(path[(prefix + "actions/runs/").Length..^"/jobs".Length], out var ciJobsRunId) &&
                ciRuns.TryGetValue(ciJobsRunId, out var jobsRun))
                response = new { total_count = 1, jobs = new[] { new { id = ciJobsRunId + 1,
                    run_id = ciJobsRunId, run_attempt = 1, head_sha = Definition, name = "Test " + jobsRun.Sha,
                    status = "completed", conclusion = "success",
                    steps = new[] { "Verify tested SHA", "Restore dependencies", "Build solution", "Run tests" }
                        .Select(name => new { name, status = "completed", conclusion = "success" }) } } };
            else if (path == "/users/" + Uri.EscapeDataString(Reviewer) || path == "/users/" + Reviewer)
                response = ReviewActor;
            else if (path == "/users/status-publisher") response = StatusActor;
            else if (path == prefix + "pulls/17/copilot-code-review")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                ReviewRequests++;
                reviewShas.Add(taskApi.HeadSha);
                response = new { node_id = $"RRR_{ReviewRequests}", state = "PENDING" };
            }
            else if (path == prefix + "pulls/17/requested_reviewers")
                response = new { users = Array.Empty<object>() };
            else if (path == prefix + "pulls/17/reviews")
                response = Enumerable.Range(0, Math.Min(reviewShas.Count,
                        ReviewSubmittedCount > 0 ? ReviewSubmittedCount : ReviewSubmitted ? reviewShas.Count : 0))
                    .Select(index => new { id = 501 + index, user = ReviewActor, commit_id = reviewShas[index],
                        state = InlineFinding && index == 0 ? "COMMENTED" : "APPROVED",
                        is_copilot_code_review = true,
                        body = "approved green done (untrusted prose)", submitted_at = Now.AddMinutes(3),
                        pull_request_url = "https://api.github.com/repos/owner/repo/pulls/17" }).ToArray();
            else if (path.StartsWith(prefix + "pulls/17/reviews/", StringComparison.Ordinal) &&
                path.EndsWith("/comments", StringComparison.Ordinal))
                response = InlineFinding && path == prefix + "pulls/17/reviews/501/comments" ? new object[] { new
                {
                    id = 601, pull_request_review_id = 501, user = ReviewActor,
                    pull_request_url = "https://api.github.com/repos/owner/repo/pulls/17",
                    commit_id = reviewShas[0], original_commit_id = reviewShas[0],
                    path = "fixture.txt", line = 3, side = "RIGHT", body = "The total must be 11, not 10.",
                    created_at = Now.AddMinutes(3), updated_at = Now.AddMinutes(3)
                } } : Array.Empty<object>();
            else if (path.StartsWith(prefix + "statuses/"))
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var sha = path[(prefix + "statuses/").Length..];
                StatusWrites.Add(path);
                var status = JsonSerializer.SerializeToElement(new
                {
                    id = 1000 + Statuses.Count, url = $"https://api.github.com/repos/owner/repo/statuses/{1000 + Statuses.Count}",
                    state = payload.RootElement.GetProperty("state").GetString(),
                    context = payload.RootElement.GetProperty("context").GetString(),
                    description = payload.RootElement.GetProperty("description").GetString(),
                    target_url = payload.RootElement.GetProperty("target_url").GetString(),
                    created_at = Now.AddMinutes(3), creator = StatusActor, sha
                });
                Statuses.Add(status);
                response = status;
            }
            else if (path.StartsWith(prefix + "commits/") && path.EndsWith("/status"))
            {
                var sha = path[(prefix + "commits/").Length..^"/status".Length];
                response = new { sha, repository = new { id = 1, full_name = "owner/repo" } };
            }
            else if (path.StartsWith(prefix + "commits/") && path.EndsWith("/statuses"))
            {
                var sha = path[(prefix + "commits/").Length..^"/statuses".Length];
                response = Statuses.Where(status => status.GetProperty("sha").GetString() == sha).Reverse().ToArray();
            }
            else return await tasks.SendAsync(request, token);
            var responseStatus = request.Method == HttpMethod.Post && path == prefix + "pulls/17/copilot-code-review"
                ? HttpStatusCode.Created : HttpStatusCode.OK;
            return new(responseStatus) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) tasks.Dispose();
            base.Dispose(disposing);
        }
    }
}
