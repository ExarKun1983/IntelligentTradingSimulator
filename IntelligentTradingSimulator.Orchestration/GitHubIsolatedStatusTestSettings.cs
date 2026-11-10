using System.Globalization;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubIsolatedStatusTestSettings(
    string Repository,
    long RepositoryId,
    long ActorId,
    GitHubExecutionApproval Tasks,
    GitHubCiApproval Ci,
    GitHubCiRoute CiRoute,
    GitHubRequiredStatusApproval Status,
    long DriverWorkflowId,
    string DefinitionSha,
    int IssueNumber,
    DateTimeOffset ExpiresAt,
    string ManifestDigest,
    bool BootstrapApproved)
{
    public const string StateReference = "refs/heads/agent-state-status-revocation-v1";
    public const string BaseBranch = "agent-status-revocation-base-v1";
    public const string DriverReference = "refs/tags/agent-status-revocation-definition-v1";
    public const string DriverWorkflowPath = ".github/workflows/agent-status-revocation-test.yml";
    public const string CiWorkflowPath = ".github/workflows/agent-status-revocation-ci.yml";
    public const string CiReference = "refs/tags/agent-status-revocation-definition-v1";
    public const string EnvironmentName = "agent-status-revocation-test";

    public static GitHubIsolatedStatusTestSettings Read(Func<string, string?> read, DateTimeOffset now)
    {
        string Required(string name) => !string.IsNullOrWhiteSpace(read(name)) ? read(name)! :
            throw new InvalidDataException("Missing isolated status-test configuration.");
        bool Approved(string name) => read(name) == "true";
        long Id(string name) => long.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture,
            out var value) && value > 0 ? value : throw new InvalidDataException("Invalid isolated status-test identity.");
        int Issue()
        {
            if (!int.TryParse(Required("ORCHESTRATION_STATUS_TEST_ISSUE"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var value) || value <= 0 || value is 227 or 237)
                throw new InvalidDataException("Isolated status-test Issue must be a fresh, non-production Issue.");
            return value;
        }

        var repository = Required("GITHUB_REPOSITORY");
        var repositoryId = Id("GITHUB_REPOSITORY_ID");
        var actorId = Id("ORCHESTRATION_STATUS_TEST_ACTOR_ID");
        var issueNumber = Issue();
        var driverWorkflowId = Id("ORCHESTRATION_STATUS_TEST_WORKFLOW_ID");
        var ciWorkflowId = Id("ORCHESTRATION_STATUS_TEST_CI_WORKFLOW_ID");
        var definitionSha = Required("ORCHESTRATION_STATUS_TEST_DEFINITION_SHA");
        var ciSha = Required("ORCHESTRATION_STATUS_TEST_CI_DEFINITION_SHA");
        if (!IsSha(definitionSha) || !IsSha(ciSha) || driverWorkflowId == ciWorkflowId)
            throw new InvalidDataException("Isolated status-test definition identity is invalid.");
        if (Required("ORCHESTRATION_STATUS_TEST_STATE_REF") != StateReference ||
            Required("ORCHESTRATION_STATUS_TEST_BASE_BRANCH") != BaseBranch ||
            Required("ORCHESTRATION_STATUS_TEST_CI_REF") != CiReference ||
            Required("ORCHESTRATION_STATUS_TEST_DRIVER_REF") != DriverReference ||
            Required("ORCHESTRATION_STATUS_TEST_ENVIRONMENT") != EnvironmentName)
            throw new InvalidDataException("Isolated status-test resource names do not match the reviewed profile.");
        if (!DateTimeOffset.TryParse(Required("ORCHESTRATION_STATUS_TEST_EXPIRES_AT"),
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt) ||
            expiresAt <= now || expiresAt > now.AddHours(72))
            throw new InvalidDataException("Isolated status-test authorization is expired or exceeds its 72-hour window.");

        var activation = Approved("ORCHESTRATION_STATUS_TEST_APPROVED");
        var bootstrapApproved = Approved("ORCHESTRATION_STATUS_TEST_BOOTSTRAP_APPROVED");
        var credential = Approved("ORCHESTRATION_STATUS_TEST_CREDENTIALS_VERIFIED");
        var cost = Approved("ORCHESTRATION_STATUS_TEST_NO_ADDED_CHARGES_VERIFIED");
        var tasks = new GitHubExecutionApproval(activation, credential,
            Approved("ORCHESTRATION_STATUS_TEST_TASK_ENTITLEMENT_VERIFIED"), cost);
        var ci = new GitHubCiApproval(activation, credential,
            Approved("ORCHESTRATION_STATUS_TEST_CI_VERIFIED"), cost);
        var status = new GitHubRequiredStatusApproval(activation, credential,
            Approved("ORCHESTRATION_STATUS_TEST_STATUS_PUBLISHER_VERIFIED"),
            Approved("ORCHESTRATION_STATUS_TEST_CONTEXT_PROTECTION_VERIFIED"), cost);
        if (!tasks.AllowsTasks || !status.AllowsPublication)
            throw new InvalidOperationException("Isolated status-test task/status approvals are unverified.");

        var ciRoute = new GitHubCiRoute(ciWorkflowId, CiReference, ciSha, CiWorkflowPath);
        var manifest = new
        {
            repository,
            repositoryId,
            actorId,
            issueNumber,
            stateReference = StateReference,
            baseBranch = BaseBranch,
            driverReference = DriverReference,
            environment = EnvironmentName,
            driverWorkflowId,
            driverWorkflowPath = DriverWorkflowPath,
            definitionSha = definitionSha.ToLowerInvariant(),
            ciWorkflowId,
            ciWorkflowPath = CiWorkflowPath,
            ciReference = CiReference,
            ciSha = ciSha.ToLowerInvariant(),
            expiresAt = expiresAt.ToUniversalTime()
        };
        var digest = StateJson.Digest(JsonSerializer.Serialize(manifest, StateJson.Options));
        return new(repository, repositoryId, actorId, tasks, ci, ciRoute, status, driverWorkflowId,
            definitionSha, issueNumber, expiresAt.ToUniversalTime(), digest, bootstrapApproved);
    }

    private static bool IsSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
}
