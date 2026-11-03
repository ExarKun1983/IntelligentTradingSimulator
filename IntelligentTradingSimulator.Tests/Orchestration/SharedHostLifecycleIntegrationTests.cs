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
    public async Task SharedHostWakeDrainsTaskCiReviewHumanClassificationAndPublishesOnlyExactSafeStatus(string afterApproval)
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
    public async Task ScheduledWakeWithoutIndependentFinalShaRemainsBlockedAndCannotDispatchCi()
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
        var held = store.State.Issues[7];
        Assert.Equal(Phase.BLOCKED, held.Phase);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Contains("authoritative task-bound evidence", held.HoldReason!);
        Assert.Null(held.Ci);
        Assert.Null(Assert.Single(held.Tasks.Values).FinalSha);
        Assert.Equal(0, fixture.CiDispatches);
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

    private static Task<WorkerResult?> Wake(GitHubOrchestrationHost host, string eventName = "schedule") =>
        host.ReconcileWakeAsync(JsonSerializer.SerializeToElement(new
            { repository = new { id = 1, full_name = "owner/repo" } }), eventName, "90",
            new(10, Definition), new(true, true, true, true), 99, "master", 55, TaskApproval, Bounded,
            new GitHubTaskIntegrationTests.FinalHead(), reviewApproval: ReviewApproval, ciRoute: CiRoute,
            ciApproval: CiApproval, statusPublisher: Publisher, statusApproval: StatusApproval);
    private static JsonElement Classification(IssueState issue, string responses = "[]") => JsonSerializer.SerializeToElement(new
    {
        repository = new { id = 1, full_name = "owner/repo" },
        sender = new { id = 55, login = "maintainer", type = "User" },
        inputs = new { issue = "7", operation = "classify-review", reason = "Human checked exact captured round",
            handoff = issue.CurrentHandoffId, round = issue.Round.ToString(), pull = "17", sha = Head,
            classification = "accepted", findings = "[]", responses }
    });
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddMinutes(3); }

    private sealed class PipelineApi : HttpMessageHandler
    {
        private const string Reviewer = "copilot-pull-request-reviewer[bot]";
        private readonly HttpMessageInvoker tasks;
        public bool CiCompleted { get; set; }
        public bool ReviewSubmitted { get; set; }
        public string WakeEvent { get; set; } = "schedule";
        public int CiDispatches { get; private set; }
        public int ReviewRequests { get; private set; }
        public List<JsonElement> Statuses { get; } = [];
        public List<string> StatusWrites { get; } = [];
        private string correlation = "";
        private static object ReviewActor => new { id = 77, node_id = "reviewer-node", login = Reviewer, type = "Bot" };
        private static object StatusActor => new { id = 99, node_id = "publisher-node", login = "status-publisher", type = "Bot" };
        public PipelineApi(HttpMessageHandler tasks) => this.tasks = new(tasks, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            const string prefix = "/repos/owner/repo/";
            object response;
            if (path == prefix + "actions/runs/90")
                response = new { id = 90, workflow_id = 10, path = ".github/workflows/agent-state-machine.yml",
                    head_sha = Definition, @event = WakeEvent, repository = new { id = 1 } };
            else if (path == prefix + "actions/runs/91")
                response = new { @event = "workflow_dispatch", path = ".github/workflows/agent-state-machine.yml",
                    actor = new { id = 55 } };
            else if (path == prefix + "actions/workflows/88")
                response = new { id = 88, path = GitHubCiAdapter.WorkflowPath, state = "active" };
            else if (path.StartsWith(prefix + "commits/refs"))
                response = new { sha = Definition };
            else if (path == prefix + "actions/workflows/88/dispatches")
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal(CiRoute.Ref, payload.RootElement.GetProperty("ref").GetString());
                Assert.Equal(Head, payload.RootElement.GetProperty("inputs").GetProperty("sha").GetString());
                correlation = payload.RootElement.GetProperty("inputs").GetProperty("correlation").GetString()!;
                CiDispatches++;
                response = new { workflow_run_id = 777, run_url = "https://api.github.com/repos/owner/repo/actions/runs/777" };
            }
            else if (path == prefix + "actions/runs/777")
                response = new { id = 777, workflow_id = 88, path = GitHubCiAdapter.WorkflowPath,
                    @event = "workflow_dispatch", head_sha = Definition, run_attempt = 1,
                    display_title = correlation, repository = new { id = 1 },
                    status = CiCompleted ? "completed" : "in_progress", conclusion = CiCompleted ? "success" : null,
                    updated_at = Now.AddMinutes(3) };
            else if (path == prefix + "actions/runs/777/jobs")
                response = new { total_count = 1, jobs = new[] { new { id = 778, run_id = 777, run_attempt = 1,
                    head_sha = Definition, name = "Test " + Head, status = "completed", conclusion = "success",
                    steps = new[] { "Verify tested SHA", "Restore dependencies", "Build solution", "Run tests" }
                        .Select(name => new { name, status = "completed", conclusion = "success" }) } } };
            else if (path == "/users/" + Uri.EscapeDataString(Reviewer) || path == "/users/" + Reviewer)
                response = ReviewActor;
            else if (path == "/users/status-publisher") response = StatusActor;
            else if (path == prefix + "pulls/17/requested_reviewers")
            {
                if (request.Method == HttpMethod.Post)
                {
                    ReviewRequests++;
                    response = new { number = 17, state = "open", head = new { sha = Head, repo = new { id = 1 } },
                        @base = new { repo = new { id = 1 } }, requested_reviewers = new[] { ReviewActor } };
                }
                else response = new { users = Array.Empty<object>() };
            }
            else if (path == prefix + "pulls/17/reviews")
                response = ReviewSubmitted ? new[] { new { id = 501, user = ReviewActor, commit_id = Head,
                    state = "APPROVED", body = "approved green done (untrusted prose)", submitted_at = Now.AddMinutes(3),
                    pull_request_url = "https://api.github.com/repos/owner/repo/pulls/17" } } : Array.Empty<object>();
            else if (path == prefix + "pulls/17/reviews/501/comments") response = Array.Empty<object>();
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
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) tasks.Dispose();
            base.Dispose(disposing);
        }
    }
}
