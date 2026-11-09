namespace IntelligentTradingSimulator.Orchestration;

public static class GitHubLiveCredentials
{
    public const long ProjectionActorId = 41898282;
    public const string ProjectionLogin = "github-actions[bot]";

    public static async Task<GitHubStatusPublisher> VerifyAsync(GitHubRepositoryApi workflowApi,
        GitHubRepositoryApi userApi, long expectedUserId, CancellationToken token = default)
    {
        if (workflowApi.Repository != userApi.Repository || workflowApi.RepositoryId != userApi.RepositoryId)
            throw new InvalidDataException("Credential repository mismatch.");
        using var user = await userApi.GetGlobalAsync("user", token);
        var human = user.RootElement;
        if (expectedUserId <= 0 || expectedUserId == ProjectionActorId ||
            human.GetProperty("id").GetInt64() != expectedUserId ||
            human.GetProperty("type").GetString() != "User" ||
            string.IsNullOrWhiteSpace(human.GetProperty("login").GetString()))
            throw new InvalidDataException("User credential identity mismatch.");

        // GITHUB_TOKEN is an installation token: /user is not its identity endpoint.
        using var response = await workflowApi.PostGlobalAsync("graphql",
            new { query = "query { viewer { login databaseId id } }" }, token);
        var root = response.RootElement;
        if (root.TryGetProperty("errors", out var errors) &&
            (errors.ValueKind != System.Text.Json.JsonValueKind.Array || errors.GetArrayLength() != 0))
            throw new InvalidDataException("Workflow credential identity is incomplete.");
        var viewer = root.GetProperty("data").GetProperty("viewer");
        if (viewer.GetProperty("databaseId").GetInt64() != ProjectionActorId ||
            viewer.GetProperty("login").GetString() != ProjectionLogin)
            throw new InvalidDataException("Workflow credential identity mismatch.");
        using var bot = await workflowApi.GetGlobalAsync("users/" + Uri.EscapeDataString(ProjectionLogin), token);
        var actor = bot.RootElement;
        var nodeId = actor.GetProperty("node_id").GetString();
        if (actor.GetProperty("id").GetInt64() != ProjectionActorId ||
            actor.GetProperty("login").GetString() != ProjectionLogin ||
            actor.GetProperty("type").GetString() != "Bot" || string.IsNullOrWhiteSpace(nodeId) ||
            viewer.GetProperty("id").GetString() != nodeId)
            throw new InvalidDataException("Projection actor identity mismatch.");
        await workflowApi.VerifyRepositoryAsync(token);
        return new(ProjectionActorId, ProjectionLogin, nodeId, "Bot");
    }
}
