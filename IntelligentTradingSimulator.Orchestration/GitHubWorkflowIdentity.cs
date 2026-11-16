namespace IntelligentTradingSimulator.Orchestration;

internal static class GitHubWorkflowIdentity
{
    private const string WorkflowPath = ".github/workflows/agent-state-machine.yml";

    internal static string ValidateContext(GitHubRepositoryApi api, GitHubWakeRoute route)
    {
        if (route.WorkflowPath != WorkflowPath ||
            route.WorkflowExecutionRef != $"{api.Repository}/{WorkflowPath}@refs/heads/master" ||
            !IsSha(route.DefinitionSha) || !IsSha(route.WorkflowExecutionSha))
            throw new InvalidDataException("Invalid trusted workflow execution context.");
        return route.WorkflowExecutionSha!;
    }

    internal static async Task VerifyDefinitionAsync(GitHubRepositoryApi api, GitHubWakeRoute route,
        string executionSha, CancellationToken token)
    {
        if (string.Equals(route.DefinitionSha, executionSha, StringComparison.OrdinalIgnoreCase)) return;
        var trustedBlob = await ReadWorkflowBlobAsync(api, route.DefinitionSha, token);
        var executionBlob = await ReadWorkflowBlobAsync(api, executionSha, token);
        if (!string.Equals(trustedBlob, executionBlob, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Workflow definition differs from the trusted revision.");
    }

    private static async Task<string> ReadWorkflowBlobAsync(GitHubRepositoryApi api, string commitSha,
        CancellationToken token)
    {
        using var commit = await api.GetAsync($"git/commits/{commitSha}", token);
        var root = commit.RootElement;
        var actualCommit = root.GetProperty("sha").GetString();
        var treeSha = root.GetProperty("tree").GetProperty("sha").GetString();
        if (!string.Equals(actualCommit, commitSha, StringComparison.OrdinalIgnoreCase) || !IsSha(treeSha))
            throw new InvalidDataException("Invalid workflow commit identity.");
        using var tree = await api.GetAsync($"git/trees/{treeSha}?recursive=1", token);
        var treeRoot = tree.RootElement;
        if (!string.Equals(treeRoot.GetProperty("sha").GetString(), treeSha, StringComparison.OrdinalIgnoreCase) ||
            treeRoot.GetProperty("truncated").GetBoolean())
            throw new InvalidDataException("Invalid or incomplete workflow tree.");
        var entries = treeRoot.GetProperty("tree").EnumerateArray()
            .Where(entry => entry.GetProperty("path").GetString() == WorkflowPath).ToList();
        if (entries.Count != 1 || entries[0].GetProperty("type").GetString() != "blob" ||
            entries[0].GetProperty("mode").GetString() != "100644")
            throw new InvalidDataException("Trusted workflow is not a regular file.");
        var blobSha = entries[0].GetProperty("sha").GetString();
        if (!IsSha(blobSha)) throw new InvalidDataException("Invalid workflow blob identity.");
        return blobSha!;
    }

    private static bool IsSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);
}
