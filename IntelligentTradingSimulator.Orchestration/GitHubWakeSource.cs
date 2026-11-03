using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubWakeRoute(long WorkflowId, string DefinitionSha);
public sealed record GitHubWakeApproval(bool ActivationApproved = false, bool ApiCredentialVerified = false,
    bool TrustedWorkflowVerified = false, bool NoAdditionalChargesVerified = false)
{
    public bool AllowsWake => ActivationApproved && ApiCredentialVerified && TrustedWorkflowVerified && NoAdditionalChargesVerified;
}

/// <summary>Wake notifications authorize observation only, never a phase transition or new opt-in.</summary>
public sealed class GitHubWakeSource(GitHubRepositoryApi api, GitHubWakeRoute route)
{
    public async Task<bool> VerifyAsync(JsonElement payload, string eventName, string runId,
        RepositoryState state, CancellationToken token)
    {
        if (eventName is not ("schedule" or "workflow_run" or "issue_comment" or "pull_request_target") ||
            route.WorkflowId <= 0 || route.DefinitionSha.Length != 40 || !route.DefinitionSha.All(Uri.IsHexDigit) ||
            state.Repository != api.Repository || !long.TryParse(runId, out var id) || id <= 0 ||
            payload.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId ||
            payload.GetProperty("repository").GetProperty("full_name").GetString() != api.Repository)
            throw new InvalidDataException("Invalid automatic wake scope or trusted definition.");
        await api.VerifyRepositoryAsync(token);
        using var response = await api.GetAsync($"actions/runs/{id}", token);
        var run = response.RootElement;
        if (run.GetProperty("id").GetInt64() != id || run.GetProperty("workflow_id").GetInt64() != route.WorkflowId ||
            run.GetProperty("path").GetString() != ".github/workflows/agent-state-machine.yml" ||
            run.GetProperty("head_sha").GetString() != route.DefinitionSha ||
            run.GetProperty("event").GetString() != eventName ||
            run.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId)
            throw new InvalidDataException("Wake does not match the trusted Actions definition/run.");

        if (eventName == "workflow_run")
        {
            if (payload.GetProperty("action").GetString() != "completed") return false;
            var sourceId = payload.GetProperty("workflow_run").GetProperty("id").GetInt64();
            var candidates = state.Issues.Values.Select(issue => issue.Ci).Where(ci =>
                ci?.DispatchBoundary?.RunId == sourceId).ToList();
            if (candidates.Count != 1) return false;
            var ci = candidates[0];
            if (ci?.DispatchBoundary is not { } boundary) return false;
            using var source = await api.GetAsync($"actions/runs/{sourceId}", token);
            var upstream = source.RootElement;
            if (upstream.GetProperty("id").GetInt64() != sourceId ||
                upstream.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId ||
                upstream.GetProperty("workflow_id").GetInt64() != boundary.WorkflowId ||
                upstream.GetProperty("path").GetString() != GitHubCiAdapter.WorkflowPath ||
                upstream.GetProperty("head_sha").GetString() != boundary.WorkflowSha ||
                upstream.GetProperty("event").GetString() != "workflow_dispatch")
                throw new InvalidDataException("CI wake identity mismatch.");
        }
        // No payload head/status/body/labels are converted into reducer evidence.
        return true;
    }
}
