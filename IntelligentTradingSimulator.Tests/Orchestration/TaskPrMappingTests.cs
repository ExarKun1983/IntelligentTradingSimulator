using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class TaskPrMappingTests
{
    private static readonly string Head = new('a', 40);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly OrchestrationReducer Reducer = new(new Allow());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialTaskMappingUsesArtifactIdentityWithoutInferringCompletion(bool globalPull)
    {
        var state = State();
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture { PullArtifact = globalPull };
        using var client = fixture.Client();
        var adapter = new GitHubTaskAdapter(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store(state), "master", 55);
        var message = await adapter.ReadMappingEventAsync(state, state.Issues[7], default);
        var mapping = Assert.IsType<TaskPrMapped>(message!.Data);
        Assert.Equal("task", mapping.TaskId);
        Assert.Equal(17, mapping.PullRequest);
        Assert.Equal(Head, mapping.ObservedHead);
        var mapped = Reducer.Apply(state, message);
        var issue = mapped.Issues[7];
        Assert.Equal(17, issue.PullRequest);
        Assert.Equal(Head, issue.HeadSha);
        Assert.Equal(TaskStatus.Running, issue.Tasks["task"].Status);
        Assert.Null(issue.Tasks["task"].FinalSha);
        Assert.Null(issue.Tasks["task"].SourceSha);
        Assert.Equal(Phase.CODING, issue.Phase);
        Assert.Equal(7, mapped.AdmittedIssue);
        Assert.Null(issue.Ci);
        Assert.Null(issue.Review);
        Assert.Equal(TaskLaunchSource.UnmappedIssue, issue.Tasks["task"].LaunchSource);
        Assert.Single(mapped.Outbox);
        Assert.False(ManagedPushRouting.Decide(mapped, new("owner/repo", 17, Head, Head), true).RunFullCi);
        Assert.True(ManagedPushRouting.Decide(state, new("owner/repo", 17, Head, Head), true).RunFullCi);
        Assert.Null(await adapter.ReadMappingEventAsync(mapped, issue, default));
        Assert.Empty(fixture.Writes);
        if (globalPull) Assert.NotEmpty(fixture.GraphQueries);
    }

    [Theory]
    [InlineData(5000000000L)]
    [InlineData(long.MaxValue)]
    public async Task EmptyGlobalIdMapsOnlyUsingUniqueBranchAndMatchingRestDatabaseId(long id)
    {
        var state = State();
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture
        {
            PullArtifact = true, IncludeBranch = true, ArtifactGlobalId = "", ArtifactDatabaseId = id
        };
        using var client = fixture.Client();
        var message = await new GitHubTaskAdapter(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store(state), "master", 55)
            .ReadMappingEventAsync(state, state.Issues[7], default);
        var mapping = Assert.IsType<TaskPrMapped>(message!.Data);
        Assert.Equal(17, mapping.PullRequest);
        Assert.Equal(Head, mapping.ObservedHead);
        var mapped = Reducer.Apply(state, message);
        Assert.Equal(TaskStatus.Running, mapped.Issues[7].Tasks["task"].Status);
        Assert.Null(mapped.Issues[7].Tasks["task"].CandidateShaEvidence);
        Assert.Null(mapped.Issues[7].Ci);
        Assert.Null(mapped.Issues[7].Review);
        Assert.Single(mapped.Outbox);
        Assert.Empty(fixture.GraphQueries);
        Assert.Empty(fixture.Writes);
        Assert.DoesNotContain(fixture.Requests, path => path.Contains($"/pulls/{id}"));
    }

    [Theory]
    [InlineData(false, "", null)]
    [InlineData(true, " ", null)]
    [InlineData(true, "", "missing-global")]
    [InlineData(true, "", "absent-global")]
    [InlineData(true, "", "global-type")]
    [InlineData(true, "", "global-length")]
    [InlineData(true, "", "database")]
    [InlineData(true, "", "rest-database")]
    [InlineData(true, "", "no-prs")]
    [InlineData(true, "", "multiple-prs")]
    [InlineData(true, "", "duplicate-pull")]
    [InlineData(true, "", "duplicate-branch")]
    [InlineData(true, "", "branch-base")]
    [InlineData(true, "", "blank-head-ref")]
    [InlineData(true, "", "head-ref")]
    [InlineData(true, "", "base")]
    [InlineData(true, "", "base-repository")]
    [InlineData(true, "", "head-repository")]
    [InlineData(true, "", "closed-pr")]
    [InlineData(true, "", "sha")]
    [InlineData(true, "PR_node", "node-null")]
    [InlineData(true, "PR_node", "node-database")]
    [InlineData(true, "PR_node", "branch-contradiction")]
    [InlineData(true, "PR_node", "graphql-errors")]
    public async Task BranchEvidenceNeverBypassesMissingOrContradictoryIdentities(
        bool includeBranch, string globalId, string? corruption)
    {
        var state = State();
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture
        {
            PullArtifact = true, IncludeBranch = includeBranch, ArtifactGlobalId = globalId,
            Corruption = corruption
        };
        using var client = fixture.Client();
        var message = await new GitHubTaskAdapter(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store(state), "master", 55)
            .ReadMappingEventAsync(state, state.Issues[7], default);
        Assert.IsType<TaskStopped>(message!.Data);
        var held = Reducer.Apply(state, message);
        Assert.Equal(Phase.BLOCKED, held.Issues[7].Phase);
        Assert.True(held.Issues[7].ReactivationRequired);
        Assert.Equal(7, held.AdmittedIssue);
        Assert.Null(held.Issues[7].PullRequest);
        Assert.Null(held.Issues[7].Ci);
        Assert.Null(held.Issues[7].Review);
        Assert.Empty(fixture.Writes);
        if (globalId == "") Assert.Empty(fixture.GraphQueries);
        else if (globalId == "PR_node") Assert.NotEmpty(fixture.GraphQueries);
    }

    [Fact]
    public async Task AbsentArtifactsDuringProgressRemainRunningWithoutHold()
    {
        var state = State();
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture { Corruption = "artifact" };
        using var client = fixture.Client();
        var adapter = new GitHubTaskAdapter(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store(state), "master", 55);
        Assert.Null(await adapter.ReadMappingEventAsync(state, state.Issues[7], default));
        Assert.Equal(Phase.CODING, state.Issues[7].Phase);
        Assert.False(state.Issues[7].ReactivationRequired);
        Assert.Equal(TaskStatus.Running, state.Issues[7].Tasks["task"].Status);
        Assert.Null(state.Issues[7].PullRequest);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("creator")]
    [InlineData("task")]
    [InlineData("multiple-prs")]
    [InlineData("base")]
    [InlineData("head-repository")]
    [InlineData("closed-pr")]
    [InlineData("sha")]
    [InlineData("missing-global")]
    [InlineData("node-repository")]
    [InlineData("node-number")]
    [InlineData("rest-database")]
    [InlineData("duplicate-pull")]
    [InlineData("branch-contradiction")]
    public async Task ContradictoryMappingEvidenceHoldsWithoutReleaseOrCompletion(string corruption)
    {
        var state = State();
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture
        {
            PullArtifact = corruption is "missing-global" or "node-repository" or "node-number" or
                "rest-database" or "duplicate-pull" or "branch-contradiction",
            IncludeBranch = corruption == "branch-contradiction",
            Corruption = corruption
        };
        using var client = fixture.Client();
        var message = await new GitHubTaskAdapter(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store(state), "master", 55)
            .ReadMappingEventAsync(state, state.Issues[7], default);
        Assert.IsType<TaskStopped>(message!.Data);
        var held = Reducer.Apply(state, message);
        Assert.Equal(Phase.BLOCKED, held.Issues[7].Phase);
        Assert.Equal(7, held.AdmittedIssue);
        Assert.True(held.Issues[7].ReactivationRequired);
        Assert.Equal(TaskStatus.Running, held.Issues[7].Tasks["task"].Status);
        Assert.Null(held.Issues[7].PullRequest);
        Assert.Null(held.Issues[7].Tasks["task"].FinalSha);
        Assert.Null(held.Issues[7].Ci);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("repository", false, "task-identity")]
    [InlineData("multiple-prs", false, "branch-pr-lookup")]
    [InlineData("base", false, "live-pr-validation")]
    [InlineData("missing-global", true, "pull-artifact-resolution")]
    [InlineData("duplicate-pull", true, "artifact-types")]
    public async Task MappingHoldReportsSafeStageWithoutLosingAdmission(string corruption, bool pullArtifact, string stage)
    {
        var state = State();
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture
        {
            Corruption = corruption, PullArtifact = pullArtifact
        };
        using var client = fixture.Client();
        var message = await new GitHubTaskAdapter(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store(state), "master", 55)
            .ReadMappingEventAsync(state, state.Issues[7], default);
        var stopped = Assert.IsType<TaskStopped>(message!.Data);
        Assert.StartsWith("task-pr-mapping-stopped:v2:", message.Id);
        Assert.Contains($":mapping/{stage}/InvalidDataException:", message.Id);
        Assert.Equal($"Task PR mapping is unsupported or contradictory; code=mapping/{stage}/InvalidDataException; admission retained.",
            stopped.Reason);
        var held = Reducer.Apply(state, message);
        Assert.Equal(Phase.BLOCKED, held.Issues[7].Phase);
        Assert.True(held.Issues[7].ReactivationRequired);
        Assert.Equal(7, held.AdmittedIssue);
        Assert.Null(held.Issues[7].Ci);
        Assert.Null(held.Issues[7].Review);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task SameTaskResponseWithDifferentMappingFailuresHasDistinctStableEventIds()
    {
        var state = State();
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture { Corruption = "multiple-prs" };
        using var client = fixture.Client();
        var adapter = new GitHubTaskAdapter(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store(state), "master", 55);

        var lookupFailure = await adapter.ReadMappingEventAsync(state, state.Issues[7], default);
        fixture.Corruption = "base";
        var validationFailure = await adapter.ReadMappingEventAsync(state, state.Issues[7], default);
        var replay = await adapter.ReadMappingEventAsync(state, state.Issues[7], default);

        Assert.NotNull(lookupFailure);
        Assert.NotNull(validationFailure);
        Assert.NotNull(replay);
        Assert.Equal(lookupFailure.Id.Split(':')[^1], validationFailure.Id.Split(':')[^1]);
        Assert.NotEqual(lookupFailure.Id, validationFailure.Id);
        Assert.Equal(validationFailure.Id, replay.Id);
        Assert.Equal(validationFailure, replay);
        Assert.Contains(":mapping/branch-pr-lookup/InvalidDataException:", lookupFailure.Id);
        Assert.Contains(":mapping/live-pr-validation/InvalidDataException:", validationFailure.Id);
        var held = Reducer.Apply(Reducer.Apply(state, lookupFailure), validationFailure);
        Assert.Equal(Phase.BLOCKED, held.Issues[7].Phase);
        Assert.True(held.Issues[7].ReactivationRequired);
        Assert.Equal(7, held.AdmittedIssue);
        Assert.Null(held.Issues[7].Ci);
        Assert.Null(held.Issues[7].Review);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(TaskStatus.Unknown)]
    [InlineData(TaskStatus.Executing)]
    [InlineData(TaskStatus.Completed)]
    [InlineData(TaskStatus.AwaitingHeadConfirmation)]
    public async Task OnlyConfirmedRunningTaskCanBeMapped(TaskStatus status)
    {
        var state = State();
        state.Issues[7].Tasks["task"].Status = status;
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture();
        using var client = fixture.Client();
        Assert.Null(await new GitHubTaskAdapter(new(client, "owner/repo", 1),
            new GitHubTaskIntegrationTests.Store(state), "master", 55)
            .ReadMappingEventAsync(state, state.Issues[7], default));
        Assert.Empty(fixture.Requests);
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state, Message()));
    }

    [Fact]
    public void MappingEventCannotClearHumanHoldCreateCiOrReplaceExistingPr()
    {
        var state = State();
        var issue = state.Issues[7];
        issue.Phase = Phase.BLOCKED;
        issue.Paused = true;
        issue.ReactivationRequired = true;
        issue.HoldReason = "Human stop";
        issue.PendingFeedbackEventIds.Add("pending");
        issue.Tasks["task"].ReviewBlockers.Add(new("old", "Existing blocker", "Prior evidence"));
        var mapped = Reducer.Apply(state, Message());
        Assert.Equal(Phase.BLOCKED, mapped.Issues[7].Phase);
        Assert.True(mapped.Issues[7].Paused);
        Assert.True(mapped.Issues[7].ReactivationRequired);
        Assert.Equal("Human stop", mapped.Issues[7].HoldReason);
        Assert.Single(mapped.Issues[7].PendingFeedbackEventIds);
        Assert.Single(mapped.Issues[7].Tasks["task"].ReviewBlockers);
        Assert.Null(mapped.Issues[7].Ci);
        Assert.Null(mapped.Issues[7].Review);
        Assert.Equal(7, mapped.AdmittedIssue);
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(mapped,
            Message() with { Id = "different-pr", Data = new TaskPrMapped("task", 18, Head) }));
        Assert.Throws<UnauthorizedAccessException>(() => Reducer.Apply(state,
            Message() with { Actor = new("human", ActorKind.Human, 55, Type: "User") }));
    }

    private static OrchestrationEvent Message() => new("mapping", "owner/repo", 7, 1, 1,
        new("github-task-api", ActorKind.TaskService), Now, new TaskPrMapped("task", 17, Head));
    private static RepositoryState State()
    {
        var state = RepositoryState.Create("owner/repo");
        state.AdmittedIssue = 7;
        state.Issues[7] = new()
        {
            Number = 7, Phase = Phase.CODING, ActiveAuthorized = true, ActiveTaskId = "task", Round = 1,
            Tasks = new()
            {
                ["task"] = new()
                {
                    Id = "task", Kind = TaskKind.Coding, Status = TaskStatus.Running, Round = 1,
                    LaunchConfirmed = true, ExternalId = "remote-task", LaunchSource = TaskLaunchSource.UnmappedIssue
                }
            }
        };
        state.Outbox["launch"] = new()
        {
            Id = "launch", Digest = "saved-receipt", IssueNumber = 7, Generation = 1,
            Kind = EffectKind.LaunchTask, Status = DeliveryStatus.Succeeded, TaskId = "task",
            ExternalId = "remote-task", Round = 1, CreatedAt = Now
        };
        return state;
    }
    private sealed class Allow : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => true;
    }
}
