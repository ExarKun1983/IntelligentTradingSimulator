using System.Globalization;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubRequiredStatusApproval(bool ActivationApproved = false, bool CredentialActorVerified = false,
    bool TrustedPublisherVerified = false, bool RequiredContextVerified = false, bool NoAdditionalChargesVerified = false)
{
    public bool ManualMergeGateApproved { get; init; }
    public bool AllowsPublication => ActivationApproved && CredentialActorVerified && TrustedPublisherVerified &&
        (RequiredContextVerified || ManualMergeGateApproved) && NoAdditionalChargesVerified;
}

public sealed record GitHubStatusPublisher(long ActorId, string Login, string NodeId, string Type);

/// <summary>REST statuses target the tested PR SHA, not the isolated workflow's definition SHA.
/// A durable prepared boundary makes all interrupted creates lookup-only. Receipt recovery
/// confirms delivery, never lifecycle eligibility; late writes cannot be atomically revoked.</summary>
public sealed class GitHubRequiredStatusAdapter(GitHubRepositoryApi api, IStateStore store,
    GitHubStatusPublisher publisher, GitHubRequiredStatusApproval? approval = null,
    TimeProvider? clock = null, GitHubLiveDiagnostics? diagnostics = null) : IEffectAdapter
{
    public const string RecoveryLimitation =
        "GitHub commit-status creation is not transactional with canonical state or human feedback. " +
        "A late uncertain success may overwrite a newer error. Preserve the hold and require human " +
        "reconciliation; status receipt recovery never approves work or guarantees atomic revocation. " +
        "Before explicit reactivation, inspect the exact repository/SHA/context, verify the publisher and " +
        "latest corrective status, and acknowledge late-write risk. A missing lookup result is not proof of non-creation.";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
    {
        var callerStage = diagnostics?.Stage;
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusApproval);
        if (approval?.AllowsPublication != true) throw new InvalidOperationException("Required-status prerequisites are not verified.");
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusInitialStateLoad);
        var state = (await store.LoadAsync(token)).State;
        var issue = ValidateIntent(state, intent);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusPreparedCheck);
        if (intent.RequiredStatusBoundary is not null || state.Outbox[intent.Id].RequiredStatusBoundary is not null)
            throw InvalidIntent(state, state.Outbox[intent.Id], intent, RequiredStatusIntentCheck.Unprepared,
                "Prepared status publication is lookup-only.");
        try
        {
            await PreflightAsync(state, issue, intent, token);
        }
        catch (InvalidDataException exception) when (intent.RequiredStatus?.State == "pending")
        {
            // This invocation has not prepared or sent a POST. Never apply this retry path after the boundary.
            diagnostics?.ReportEffectFailure(intent.Kind, EffectFailureCode.RequiredStatusPreflightRejected, exception);
            var rejected = state.Outbox.Values.Count(previous => previous.Kind == EffectKind.PublishRequiredStatus &&
                previous.IssueNumber == intent.IssueNumber && previous.Generation == intent.Generation &&
                previous.TaskId == intent.TaskId && previous.Sha == intent.Sha &&
                previous.FailureCode == EffectFailureCode.RequiredStatusPreflightRejected);
            return new(rejected < 2 ? DeliveryStatus.Cancelled : DeliveryStatus.Failed,
                FailureCode: EffectFailureCode.RequiredStatusPreflightRejected);
        }
        var statuses = await ReadStatusesAsync(intent, token);
        var boundary = new RequiredStatusBoundary(publisher.ActorId, publisher.Login, publisher.NodeId,
            publisher.Type, clock.GetUtcNow(), statuses.Select(item => item.GetProperty("id").GetInt64()).ToList());
        await SaveBoundaryAsync(intent, boundary, token);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusPreparedStateLoad);
        state = (await store.LoadAsync(token)).State;
        issue = ValidateIntent(state, intent);
        await PreflightAsync(state, issue, intent, token);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusPreparedStateLoad);
        state = (await store.LoadAsync(token)).State;
        ValidateCurrent(state, ValidateIntent(state, intent), intent);
        var payload = intent.RequiredStatus!;
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusPublication);
        using var receipt = await api.WriteAsync(HttpMethod.Post, $"statuses/{intent.Sha}",
            new { state = payload.State, context = payload.Context, description = payload.Description, target_url = payload.TargetUrl }, token);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusConfirmation);
        var id = ValidateStatus(receipt.RootElement, intent, boundary);
        if (boundary.ExistingStatusIds.Contains(id)) throw new InvalidDataException("Status receipt predates the creation boundary.");
        await SaveBoundaryAsync(intent, boundary with { StatusId = id }, token);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusConfirmationStateLoad);
        state = (await store.LoadAsync(token)).State;
        issue = ValidateIntent(state, intent);
        await PreflightAsync(state, issue, intent, token);
        var confirmed = (await ReadStatusesAsync(intent, token)).Where(item => item.GetProperty("id").GetInt64() == id).ToList();
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusConfirmation);
        if (confirmed.Count != 1) throw new InvalidDataException("Created status is not visible on the exact tested SHA.");
        ValidateStatus(confirmed[0], intent, boundary);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusConfirmationStateLoad);
        state = (await store.LoadAsync(token)).State;
        ValidateCurrent(state, ValidateIntent(state, intent), intent);
        if (callerStage is { } previousStage) diagnostics?.Mark(previousStage);
        return new(DeliveryStatus.Succeeded, id.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token)
    {
        var callerStage = diagnostics?.Stage;
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusRecoveryStateLoad);
        var state = (await store.LoadAsync(token)).State;
        ValidateIntent(state, intent);
        if (state.Outbox[intent.Id].RequiredStatusBoundary is not { } boundary)
        {
            if (callerStage is { } unpreparedStage) diagnostics?.Mark(unpreparedStage);
            return new(DeliveryStatus.Unknown);
        }
        ValidatePublisher(boundary);
        await api.VerifyRepositoryAsync(token);
        var statuses = await ReadStatusesAsync(intent, token);
        var candidates = statuses.Where(item => boundary.StatusId is { } known
            ? item.GetProperty("id").GetInt64() == known
            : !boundary.ExistingStatusIds.Contains(item.GetProperty("id").GetInt64()) &&
              item.GetProperty("context").GetString() == intent.RequiredStatus!.Context &&
              item.GetProperty("target_url").GetString() == intent.RequiredStatus.TargetUrl).ToList();
        if (candidates.Count != 1)
        {
            if (callerStage is { } stage) diagnostics?.Mark(stage);
            return new(DeliveryStatus.Unknown);
        }
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusConfirmation);
        var id = ValidateStatus(candidates[0], intent, boundary);
        if (callerStage is { } previousStage) diagnostics?.Mark(previousStage);
        return new(DeliveryStatus.Succeeded, id.ToString(CultureInfo.InvariantCulture));
    }

    internal async Task<RestartStatusVerification> ValidateRestartStatusAcknowledgementsAsync(
        RepositoryState state, IssueState issue, PrepareRestart restart, CancellationToken token)
    {
        var acknowledgements = RequiredStatusPolicy.RestartStatusAcknowledgements(state, issue, restart);
        if (acknowledgements.Count == 0)
            throw new InvalidDataException("Restart status risk verification requires explicit uncertain intent identities.");
        if (approval?.AllowsPublication != true || state.Repository != api.Repository ||
            issue.Phase != Phase.CLOSED || issue.TerminalOutcome != TerminalOutcome.Unmerged ||
            issue.Ready || issue.ActiveAuthorized || issue.PullRequest != restart.PullRequest ||
            issue.HeadSha != restart.Sha ||
            issue.RequiredStatusIntentId is not { } currentId || !state.Outbox.TryGetValue(currentId, out var current) ||
            current.Status != DeliveryStatus.Succeeded || current.RequiredStatus?.State != "error" ||
            !RequiredStatusPolicy.Matches(state, issue, current))
            throw new InvalidDataException("Restart status risk validation requires a stopped attempt and confirmed closed-head revocation.");
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusPublisher);
        ValidatePublisher(new(publisher.ActorId, publisher.Login, publisher.NodeId, publisher.Type, clock.GetUtcNow(), []));
        await api.VerifyRepositoryAsync(token);
        using var actor = await api.GetGlobalAsync("users/" + Uri.EscapeDataString(publisher.Login), token);
        ValidateActor(actor.RootElement, publisher.ActorId, publisher.Login, publisher.NodeId, publisher.Type);
        foreach (var correction in acknowledgements.Select(item => state.Outbox[item.CorrectiveIntentId]).Append(current))
        {
            if (correction.RequiredStatusBoundary is not { StatusId: > 0 } boundary ||
                correction.ExternalId != boundary.StatusId.Value.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Restart requires an exact saved corrective status receipt.");
            ValidatePublisher(boundary);
            var statuses = await ReadStatusesAsync(correction, token);
            var latest = statuses.Where(status => status.GetProperty("context").GetString() == RequiredStatusPolicy.Context)
                .OrderByDescending(status => status.GetProperty("created_at").GetDateTimeOffset())
                .ThenByDescending(status => status.GetProperty("id").GetInt64()).FirstOrDefault();
            if (latest.ValueKind != JsonValueKind.Object || ValidateStatus(latest, correction, boundary) != boundary.StatusId)
                throw new InvalidDataException("The latest live required status is not the acknowledged corrective error receipt.");
        }
        return new(acknowledgements, current.Id, current.RequiredStatusBoundary!.StatusId!.Value);
    }

    private IssueState ValidateIntent(RepositoryState state, EffectIntent intent)
    {
        var callerStage = diagnostics?.Stage;
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusCanonicalIntent);
        EffectIntent? saved = null;
        InvalidDataException Rejected(RequiredStatusIntentCheck check) =>
            InvalidIntent(state, saved ?? (state.Outbox.TryGetValue(intent.Id, out var observed) ? observed : null),
                intent, check, "Required status is not a canonical executing intent.");
        if (state.Repository != api.Repository) throw Rejected(RequiredStatusIntentCheck.Repository);
        if (intent.Kind != EffectKind.PublishRequiredStatus) throw Rejected(RequiredStatusIntentCheck.IntentKind);
        if (!state.Outbox.TryGetValue(intent.Id, out saved))
            throw Rejected(RequiredStatusIntentCheck.IntentExists);
        if (saved.Digest != intent.Digest) throw Rejected(RequiredStatusIntentCheck.Digest);
        if (saved.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown))
            throw Rejected(RequiredStatusIntentCheck.DeliveryState);
        if (saved.Kind != intent.Kind) throw Rejected(RequiredStatusIntentCheck.SavedKind);
        if (saved.IssueNumber != intent.IssueNumber) throw Rejected(RequiredStatusIntentCheck.IssueNumber);
        if (saved.Generation != intent.Generation) throw Rejected(RequiredStatusIntentCheck.Generation);
        if (saved.TaskId != intent.TaskId) throw Rejected(RequiredStatusIntentCheck.Task);
        if (saved.Round != intent.Round) throw Rejected(RequiredStatusIntentCheck.Round);
        if (saved.Sha != intent.Sha) throw Rejected(RequiredStatusIntentCheck.ShaMatches);
        if (saved.RequiredStatus != intent.RequiredStatus) throw Rejected(RequiredStatusIntentCheck.Payload);
        if (intent.RequiredStatus?.Context != RequiredStatusPolicy.Context)
            throw Rejected(RequiredStatusIntentCheck.Context);
        if (intent.Sha is not { Length: 40 } || !intent.Sha.All(Uri.IsHexDigit))
            throw Rejected(RequiredStatusIntentCheck.ShaFormat);
        if (!state.Issues.TryGetValue(intent.IssueNumber, out var issue))
            throw Rejected(RequiredStatusIntentCheck.IssueExists);
        if (callerStage is { } previousStage) diagnostics?.Mark(previousStage);
        return issue;
    }

    private InvalidDataException InvalidIntent(RepositoryState state, EffectIntent? saved, EffectIntent supplied,
        RequiredStatusIntentCheck check, string message)
    {
        var exception = new InvalidDataException(message);
        diagnostics?.AttachRequiredStatusValidation(exception, check, state, saved, supplied);
        return exception;
    }

    private static void ValidateCurrent(RepositoryState state, IssueState issue, EffectIntent intent)
    {
        if (!RequiredStatusPolicy.Matches(state, issue, intent))
            throw new InvalidDataException("Required-status eligibility changed.");
    }

    private async Task PreflightAsync(RepositoryState state, IssueState issue, EffectIntent intent, CancellationToken token)
    {
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusEligibility);
        ValidateCurrent(state, issue, intent);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusPublisher);
        ValidatePublisher(new(publisher.ActorId, publisher.Login, publisher.NodeId, publisher.Type, clock.GetUtcNow(), []));
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusRepository);
        await api.VerifyRepositoryAsync(token);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusPublisher);
        using var actor = await api.GetGlobalAsync("users/" + Uri.EscapeDataString(publisher.Login), token);
        ValidateActor(actor.RootElement, publisher.ActorId, publisher.Login, publisher.NodeId, publisher.Type);
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusLiveHead);
        using var liveIssue = await api.GetAsync($"issues/{issue.Number}", token);
        using var pull = await api.GetAsync($"pulls/{issue.PullRequest}", token);
        var pr = pull.RootElement;
        var revokingPublishedHead = intent.RequiredStatus!.State == "error" &&
            (state.Outbox.Values.Any(previous => previous.Kind == EffectKind.PublishRequiredStatus &&
                previous.IssueNumber == intent.IssueNumber && previous.Generation == intent.Generation &&
                previous.Sha == intent.Sha && previous.RequiredStatus?.State == "success" &&
                (previous.Status == DeliveryStatus.Succeeded || previous.RequiredStatusBoundary is not null)) ||
             // An Issue-link revocation error targets the link's own recorded tested head, which is no
             // longer required to be the live head once a later push or reactivation has moved it on.
             state.Outbox.Values.Any(link => link.Kind == EffectKind.LinkIssue &&
                link.IssueNumber == intent.IssueNumber && link.Generation == intent.Generation &&
                link.Sha == intent.Sha &&
                (RequiredStatusPolicy.RequiresIssueLinkRevocation(state, link) ||
                 link.IssueLinkRevocationDischargedBy == intent.Id)));
        if (liveIssue.RootElement.GetProperty("number").GetInt32() != issue.Number ||
            pr.GetProperty("number").GetInt32() != issue.PullRequest ||
            pr.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pr.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            state.IsolatedTestManifestDigest is not null &&
                (!pr.GetProperty("draft").GetBoolean() ||
                 pr.GetProperty("base").GetProperty("ref").GetString() != GitHubIsolatedStatusTestSettings.BaseBranch) ||
            !revokingPublishedHead && pr.GetProperty("head").GetProperty("sha").GetString() != intent.Sha)
            throw new InvalidDataException("Required-status live head/repository mapping changed.");
        if (intent.RequiredStatus!.State == "success")
        {
            var merged = RequiredStatusPolicy.IsRecordedMerge(issue);
            if (merged
                ? pr.GetProperty("state").GetString() != "closed" || !pr.GetProperty("merged").GetBoolean() ||
                  pr.GetProperty("merged_at").GetDateTimeOffset() != issue.TerminalHistory.Last().MergedAt
                : liveIssue.RootElement.GetProperty("state").GetString() != "open" ||
                  pr.GetProperty("state").GetString() != "open")
                throw new InvalidDataException("Closed work cannot publish success.");
            await VerifyGreenRunAsync(api, issue, token);
        }
    }

    internal static async Task VerifyGreenRunAsync(GitHubRepositoryApi api, IssueState issue, CancellationToken token)
    {
        var ci = issue.Ci!;
        var boundary = ci.DispatchBoundary!;
        using var definition = await api.GetAsync("commits/" + Uri.EscapeDataString(boundary.Ref), token);
        if (!boundary.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) ||
            definition.RootElement.GetProperty("sha").GetString() != boundary.WorkflowSha)
            throw new InvalidDataException("Required-status trusted CI definition changed.");
        using var run = await api.GetAsync($"actions/runs/{ci.RunId}", token);
        var root = run.RootElement;
        if (root.GetProperty("id").GetInt64() != boundary.RunId ||
            root.GetProperty("workflow_id").GetInt64() != boundary.WorkflowId ||
            root.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId ||
            root.GetProperty("path").GetString() != boundary.WorkflowPath ||
            root.GetProperty("event").GetString() != "workflow_dispatch" ||
            root.GetProperty("head_sha").GetString() != boundary.WorkflowSha ||
            root.GetProperty("run_attempt").GetInt32() != 1 ||
            root.GetProperty("display_title").GetString() != boundary.Correlation ||
            root.GetProperty("status").GetString() != "completed" || root.GetProperty("conclusion").GetString() != "success")
            throw new InvalidDataException("Required-status trusted CI run does not prove success.");
        using var jobs = await api.GetAsync($"actions/runs/{ci.RunId}/jobs?filter=latest&per_page=100&page=1", token);
        var items = jobs.RootElement.GetProperty("jobs");
        if (jobs.RootElement.GetProperty("total_count").GetInt32() != 1 || items.GetArrayLength() != 1)
            throw new InvalidDataException("Required-status CI job evidence is ambiguous.");
        var job = items[0];
        if (job.GetProperty("id").GetInt64() <= 0 || job.GetProperty("run_id").GetInt64() != boundary.RunId ||
            job.GetProperty("run_attempt").GetInt32() != 1 || job.GetProperty("head_sha").GetString() != boundary.WorkflowSha ||
            job.GetProperty("name").GetString() != "Test " + ci.Sha ||
            job.GetProperty("status").GetString() != "completed" || job.GetProperty("conclusion").GetString() != "success")
            throw new InvalidDataException("Required-status CI job did not test the captured head.");
        foreach (var name in new[] { "Verify tested SHA", "Restore dependencies", "Build solution", "Run tests" })
        {
            var steps = job.GetProperty("steps").EnumerateArray().Where(item => item.GetProperty("name").GetString() == name).ToList();
            if (steps.Count != 1 || steps[0].GetProperty("status").GetString() != "completed" ||
                steps[0].GetProperty("conclusion").GetString() != "success")
                throw new InvalidDataException("Required-status CI steps are not verified green.");
        }
    }

    private async Task<List<JsonElement>> ReadStatusesAsync(EffectIntent intent, CancellationToken token)
    {
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusLookup);
        using var combined = await api.GetAsync($"commits/{intent.Sha}/status", token);
        if (combined.RootElement.GetProperty("sha").GetString() != intent.Sha ||
            combined.RootElement.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId ||
            combined.RootElement.GetProperty("repository").GetProperty("full_name").GetString() != api.Repository)
            throw new InvalidDataException("Status lookup is not for the exact repository/SHA.");
        var items = await api.ListAsync($"commits/{intent.Sha}/statuses", token);
        if (items.Any(item => item.GetProperty("id").GetInt64() <= 0) ||
            items.Select(item => item.GetProperty("id").GetInt64()).Distinct().Count() != items.Count)
            throw new InvalidDataException("Status lookup identities are invalid or duplicated.");
        return items;
    }

    private long ValidateStatus(JsonElement status, EffectIntent intent, RequiredStatusBoundary boundary)
    {
        var id = status.GetProperty("id").GetInt64();
        var payload = intent.RequiredStatus!;
        if (id <= 0 || status.GetProperty("url").GetString() != $"https://api.github.com/repos/{api.Repository}/statuses/{intent.Sha}" ||
            status.GetProperty("context").GetString() != payload.Context || status.GetProperty("state").GetString() != payload.State ||
            status.GetProperty("target_url").GetString() != payload.TargetUrl || status.GetProperty("description").GetString() != payload.Description ||
            status.GetProperty("created_at").GetDateTimeOffset().ToUnixTimeSeconds() < boundary.PreparedAt.ToUnixTimeSeconds() ||
            status.GetProperty("created_at").GetDateTimeOffset() > clock.GetUtcNow().AddMinutes(1))
            throw new InvalidDataException("Required-status receipt identity/payload mismatch.");
        ValidateActor(status.GetProperty("creator"), boundary.ActorId, boundary.ActorLogin, boundary.ActorNodeId, boundary.ActorType);
        return id;
    }

    private void ValidatePublisher(RequiredStatusBoundary boundary)
    {
        if (publisher.ActorId <= 0 || string.IsNullOrWhiteSpace(publisher.Login) || string.IsNullOrWhiteSpace(publisher.NodeId) ||
            publisher.Type is not ("User" or "Bot") || boundary.ActorId != publisher.ActorId ||
            boundary.ActorLogin != publisher.Login || boundary.ActorNodeId != publisher.NodeId || boundary.ActorType != publisher.Type)
            throw new InvalidDataException("Required-status publisher is not the trusted actor.");
    }

    private static void ValidateActor(JsonElement actor, long id, string login, string nodeId, string type)
    {
        if (actor.GetProperty("id").GetInt64() != id || actor.GetProperty("login").GetString() != login ||
            actor.GetProperty("node_id").GetString() != nodeId || actor.GetProperty("type").GetString() != type)
            throw new InvalidDataException("Required-status creator identity mismatch.");
    }

    private async Task SaveBoundaryAsync(EffectIntent intent, RequiredStatusBoundary boundary, CancellationToken token)
    {
        diagnostics?.Mark(GitHubLiveStage.RequiredStatusBoundary);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await store.LoadAsync(token);
            ValidateIntent(snapshot.State, intent);
            if (boundary.StatusId is null && snapshot.State.Outbox[intent.Id].RequiredStatusBoundary is not null)
                throw new InvalidDataException("Another publisher already prepared this intent.");
            var next = new OrchestrationReducer().RecordRequiredStatusBoundary(snapshot.State, intent.Id, boundary);
            if (await store.TrySaveAsync(snapshot, next, token)) return;
        }
        throw new InvalidOperationException("Required-status receipt publication conflict; intent remains lookup-only.");
    }
}
