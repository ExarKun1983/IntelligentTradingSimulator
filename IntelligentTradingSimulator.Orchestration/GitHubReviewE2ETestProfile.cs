namespace IntelligentTradingSimulator.Orchestration;

public static class GitHubReviewE2ETestProfile
{
    public const int IssueNumber = 265;
    public const string StateReference = "refs/heads/agent-state-review-e2e-265-v1";
    public const string BootstrapWorkflowPath = ".github/workflows/agent-review-e2e-bootstrap.yml";
    public const string BootstrapConfirmation = "BOOTSTRAP-REVIEW-E2E-265";

    public static string ManifestDigest(string repository, long repositoryId, string definitionSha) =>
        StateJson.Digest(System.Text.Json.JsonSerializer.Serialize(new
        {
            repository,
            repositoryId,
            issue = IssueNumber,
            stateReference = StateReference,
            definitionSha = definitionSha.ToLowerInvariant(),
            bootstrapWorkflowPath = BootstrapWorkflowPath
        }, StateJson.Options));
}
