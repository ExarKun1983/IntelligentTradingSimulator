using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public static class GitHubReviewE2ETestBootstrapCommand
{
    private const string Repository = "ExarKun1983/IntelligentTradingSimulator";

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) =>
        RunAsync(args, output, error, Environment.GetEnvironmentVariable, CreateClient);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string, string?> read, Func<string, HttpClient> createClient)
    {
        try
        {
            if (!args.Contains("--execute", StringComparer.Ordinal) ||
                args.Any(arg => arg is "--dry-run" or "--replay" or "--context"))
                throw new InvalidDataException("Review-test bootstrap requires a trusted Actions invocation.");

            var eventPath = read("GITHUB_EVENT_PATH");
            if (string.IsNullOrWhiteSpace(eventPath) || new FileInfo(eventPath).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Missing or oversized bootstrap notification.");
            using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(eventPath));
            var root = payload.RootElement;
            var definitionSha = RequiredSha(read("ORCHESTRATION_DEFINITION_SHA"));
            var repositoryId = RequiredIdentity(read("GITHUB_REPOSITORY_ID"));
            var actorId = RequiredIdentity(read("ORCHESTRATION_ACTOR_ID"));
            var runId = RequiredIdentity(read("GITHUB_RUN_ID"));

            if (read("GITHUB_REPOSITORY") != Repository ||
                read("GITHUB_EVENT_NAME") != "workflow_dispatch" ||
                read("GITHUB_REF") != "refs/heads/master" ||
                read("GITHUB_WORKFLOW_REF") != $"{Repository}/{GitHubReviewE2ETestProfile.BootstrapWorkflowPath}@refs/heads/master" ||
                read("GITHUB_WORKFLOW_SHA") != definitionSha ||
                read("ORCHESTRATION_REVIEW_E2E_BOOTSTRAP_APPROVED") != "true" ||
                read("ORCHESTRATION_ACTIVATION_APPROVED") != "false")
                throw new InvalidDataException("Bootstrap is not isolated from normal execution.");

            var inputs = root.GetProperty("inputs");
            if (inputs.GetProperty("issue").GetString() != GitHubReviewE2ETestProfile.IssueNumber.ToString(CultureInfo.InvariantCulture) ||
                inputs.GetProperty("confirmation").GetString() != GitHubReviewE2ETestProfile.BootstrapConfirmation ||
                root.GetProperty("sender").GetProperty("id").GetInt64() != actorId ||
                root.GetProperty("sender").GetProperty("type").GetString() != "User")
                throw new InvalidDataException("Bootstrap confirmation or operator identity is invalid.");

            var token = Required(read("GITHUB_TOKEN"));
            using var client = createClient(token);
            var api = new GitHubRepositoryApi(client, Repository, repositoryId);
            await api.VerifyRepositoryAsync(default);
            using var run = await api.GetAsync($"actions/runs/{runId}", default);
            if (run.RootElement.GetProperty("id").GetInt64() != runId ||
                run.RootElement.GetProperty("path").GetString() != GitHubReviewE2ETestProfile.BootstrapWorkflowPath ||
                run.RootElement.GetProperty("event").GetString() != "workflow_dispatch" ||
                run.RootElement.GetProperty("head_sha").GetString() != definitionSha ||
                run.RootElement.GetProperty("run_attempt").GetInt32() != 1 ||
                run.RootElement.GetProperty("repository").GetProperty("id").GetInt64() != repositoryId ||
                run.RootElement.GetProperty("actor").GetProperty("id").GetInt64() != actorId)
                throw new InvalidDataException("Bootstrap run identity does not match the reviewed workflow.");

            var ingress = await api.ReadIngressEvidenceAsync(root, "workflow_dispatch", runId.ToString(CultureInfo.InvariantCulture), [actorId], default,
                GitHubReviewE2ETestProfile.BootstrapWorkflowPath);
            if (ingress.IssueNumber != GitHubReviewE2ETestProfile.IssueNumber || !ingress.IssueOpen ||
                ingress.Ready || ingress.IsPullRequest || ingress.ActorId != actorId || ingress.ActorType != "User")
                throw new UnauthorizedAccessException("Bootstrap requires the open test Issue and the approved human operator.");

            var transport = new GitHubGitTransport(client, Repository, allowMutations: true,
                stateReference: GitHubReviewE2ETestProfile.StateReference, allowIsolatedInitialization: true);
            var store = new GitStateStore(transport, Repository, GitHubReviewE2ETestProfile.StateReference,
                allowInitialization: true);
            var snapshot = await store.LoadAsync(default);
            if (snapshot.Revision is not null || snapshot.State.Issues.Count != 0 ||
                snapshot.State.Inbox.Count != 0 || snapshot.State.Outbox.Count != 0)
                throw new InvalidOperationException("The isolated state ref already exists; bootstrap is one-shot.");

            var manifest = GitHubReviewE2ETestProfile.ManifestDigest(Repository, repositoryId, definitionSha);
            snapshot.State.IsolatedTestManifestDigest = manifest;
            if (!await store.TrySaveAsync(snapshot, snapshot.State, default))
                throw new InvalidOperationException("Bootstrap conflicted; inspect the state ref before retrying.");
            var saved = await store.LoadAsync(default);
            if (saved.State.IsolatedTestManifestDigest != manifest ||
                saved.State.Issues.Count != 0 || saved.State.Inbox.Count != 0 || saved.State.Outbox.Count != 0)
                throw new InvalidDataException("Bootstrap receipt does not match the isolated manifest.");

            output.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = "review-e2e-bootstrap",
                Issue = GitHubReviewE2ETestProfile.IssueNumber,
                StateReference = GitHubReviewE2ETestProfile.StateReference,
                StateRevision = saved.Revision,
                ManifestDigest = manifest
            }, StateJson.Options));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException or
            HttpRequestException or OperationCanceledException or FormatException or OverflowException)
        {
            error.WriteLine($"Review-test bootstrap stopped ({exception.GetType().Name}); inspect the isolated state ref before retrying.");
            return 1;
        }
    }

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidDataException("Missing bootstrap configuration.");

    private static long RequiredIdentity(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var identity) && identity > 0
            ? identity : throw new InvalidDataException("Invalid bootstrap identity.");

    private static string RequiredSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit)
            ? value : throw new InvalidDataException("Invalid bootstrap definition SHA.");

    private static HttpClient CreateClient(string token)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
