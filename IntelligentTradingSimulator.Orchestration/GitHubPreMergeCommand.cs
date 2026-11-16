using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public static class GitHubPreMergeCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (!args.Contains("--dry-run", StringComparer.Ordinal) ||
                args.Contains("--execute", StringComparer.Ordinal))
                throw new PreMergeCheckException("The pre-merge check is read-only and requires --dry-run.");
            string Argument(string name)
            {
                var indices = Enumerable.Range(0, args.Length).Where(index => args[index] == name).ToList();
                if (indices.Count != 1 || indices[0] + 1 >= args.Length ||
                    args[indices[0] + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new PreMergeCheckException("A required check argument is missing or repeated.");
                return args[indices[0] + 1];
            }
            long Positive(string name) =>
                long.TryParse(Argument(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                    ? value : throw new PreMergeCheckException("A check identity is invalid.");
            var repository = Argument("--repository");
            var repositoryId = Positive("--repository-id");
            var creatorId = Positive("--task-creator-id");
            var issue = Positive("--issue");
            if (issue > int.MaxValue) throw new PreMergeCheckException("The Issue number is invalid.");
            var baseBranch = Argument("--base-branch");
            if (string.IsNullOrWhiteSpace(baseBranch)) throw new PreMergeCheckException("The base branch is missing.");
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            var taskToken = Environment.GetEnvironmentVariable("ORCHESTRATION_USER_TOKEN");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(taskToken))
                throw new PreMergeCheckException("Read-only repository and agent-task credentials are required.");
            using var client = CreateClient(token);
            using var taskClient = CreateClient(taskToken);
            var api = new GitHubRepositoryApi(client, repository, repositoryId);
            var taskApi = new GitHubRepositoryApi(taskClient, repository, repositoryId);
            var store = new GitStateStore(new GitHubGitTransport(client, repository), repository);
            var candidate = new GitHubTaskAdapter(api, store, baseBranch, creatorId, taskApi: taskApi);
            var result = await new GitHubPreMergeCheck(api, store, candidate).CheckAsync((int)issue);
            output.WriteLine(JsonSerializer.Serialize(result, StateJson.Options));
            return 0;
        }
        catch (PreMergeCheckException exception)
        {
            error.WriteLine($"Pre-merge check stopped: {exception.Message} No merge was approved.");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException or
            HttpRequestException or OperationCanceledException or FormatException or OverflowException)
        {
            error.WriteLine($"Pre-merge evidence could not be verified ({exception.GetType().Name}); no merge was approved.");
            return 1;
        }
    }

    private static HttpClient CreateClient(string token)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
