using System.Globalization;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubPocSettings(string Repository, long RepositoryId, long ActorId,
    long DriverWorkflowId, string DefinitionSha, GitHubCiRoute CiRoute,
    GitHubExecutionApproval Tasks, GitHubCiApproval Ci, GitHubReviewApproval Review,
    DateTimeOffset ExpiresAt, string ManifestDigest, bool BootstrapApproved)
{
    public const int IssueNumber = 268;
    public const string StateReference = "refs/heads/agent-state-poc-268-v1";
    public const string BaseBranch = "master";
    public const string DefinitionReference = "refs/heads/master";
    public const string DriverWorkflowPath = ".github/workflows/agent-poc.yml";
    public const string CiWorkflowPath = ".github/workflows/agent-poc-ci.yml";
    public const string EnvironmentName = "agent-orchestration";

    public static GitHubPocSettings Read(Func<string, string?> read, DateTimeOffset now)
    {
        string Required(string name) => !string.IsNullOrWhiteSpace(read(name)) ? read(name)! :
            throw new InvalidDataException("Missing PoC configuration.");
        long Id(string name) => long.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture,
            out var value) && value > 0 ? value : throw new InvalidDataException("Invalid PoC identity.");
        int ReviewTimeoutMinutes()
        {
            var value = read("ORCHESTRATION_POC_REVIEW_TIMEOUT_MINUTES");
            if (string.IsNullOrWhiteSpace(value)) return 15;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) &&
                minutes is >= 1 and <= 30
                ? minutes : throw new InvalidDataException("Invalid Copilot review timeout.");
        }
        bool Approved(string name) => read("ORCHESTRATION_POC_" + name) == "true";

        var repository = Required("GITHUB_REPOSITORY");
        var repositoryId = Id("GITHUB_REPOSITORY_ID");
        var actorId = Id("ORCHESTRATION_POC_ACTOR_ID");
        var driverId = Id("ORCHESTRATION_POC_WORKFLOW_ID");
        var ciId = Id("ORCHESTRATION_POC_CI_WORKFLOW_ID");
        var reviewTimeoutMinutes = ReviewTimeoutMinutes();
        var definition = Required("ORCHESTRATION_POC_DEFINITION_SHA");
        if (repository != "ExarKun1983/IntelligentTradingSimulator" || driverId == ciId ||
            definition.Length != 40 || !definition.All(Uri.IsHexDigit))
            throw new InvalidDataException("PoC repository or definition identity is invalid.");
        if (!DateTimeOffset.TryParse(Required("ORCHESTRATION_POC_EXPIRES_AT"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expiresAt) || expiresAt <= now || expiresAt > now.AddHours(72))
            throw new InvalidDataException("PoC authorization is expired or exceeds 72 hours.");

        var activation = Approved("APPROVED");
        var credential = Approved("CREDENTIALS_VERIFIED");
        var cost = Approved("NO_ADDED_CHARGES_VERIFIED");
        var tasks = new GitHubExecutionApproval(activation, credential, Approved("TASK_ENTITLEMENT_VERIFIED"), cost);
        var ci = new GitHubCiApproval(activation, credential, Approved("TRUSTED_DEFINITION_VERIFIED"), cost);
        var review = new GitHubReviewApproval(activation, Approved("REVIEW_POLICY_VERIFIED"), cost,
            Approved("AUTOMATIC_REVIEWS_DISABLED"))
            { ReviewTimeout = TimeSpan.FromMinutes(reviewTimeoutMinutes) };
        if (!tasks.AllowsTasks || !ci.AllowsCi || !review.AllowsReviews)
            throw new InvalidOperationException("PoC execution prerequisites are unverified.");

        var manifest = new
        {
            repository, repositoryId, actorId, driverId, ciId,
            definition = definition.ToLowerInvariant(), issue = IssueNumber,
            stateRef = StateReference, baseBranch = BaseBranch, definitionRef = DefinitionReference,
            driverPath = DriverWorkflowPath, ciPath = CiWorkflowPath, environment = EnvironmentName,
            expiresAt = expiresAt.ToUniversalTime(), maxRepairTasks = 1, maxCiRuns = 2, maxReviews = 2,
            reviewTimeoutMinutes
        };
        return new(repository, repositoryId, actorId, driverId, definition,
            new(ciId, DefinitionReference, definition, CiWorkflowPath), tasks, ci, review,
            expiresAt.ToUniversalTime(), StateJson.Digest(JsonSerializer.Serialize(manifest, StateJson.Options)),
            Approved("BOOTSTRAP_APPROVED"));
    }
}
