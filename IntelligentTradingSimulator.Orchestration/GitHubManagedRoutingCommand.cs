using System.Net.Http.Headers;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public static class GitHubManagedRoutingCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (!args.Contains("--dry-run", StringComparer.Ordinal) || args.Contains("--execute", StringComparer.Ordinal))
                throw new InvalidDataException("Managed routing is observation-only.");
            var enabled = Environment.GetEnvironmentVariable("ORCHESTRATION_MANAGED_ROUTING_APPROVED") == "true";
            var run = true;
            if (enabled)
            {
                if (Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") != "pull_request" ||
                    Environment.GetEnvironmentVariable("ORCHESTRATION_ACTIVATION_APPROVED") != "true" ||
                    Environment.GetEnvironmentVariable("ORCHESTRATION_REQUIRED_CONTEXT_VERIFIED") != "true" ||
                    Environment.GetEnvironmentVariable("ORCHESTRATION_NO_ADDED_CHARGES_VERIFIED") != "true")
                    throw new InvalidOperationException("Managed routing prerequisites are unverified.");
                var path = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
                if (string.IsNullOrWhiteSpace(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("Missing or oversized trusted notification.");
                using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")!;
                var repositoryId = long.Parse(Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_ID")!,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (payload.RootElement.GetProperty("repository").GetProperty("id").GetInt64() != repositoryId ||
                    payload.RootElement.GetProperty("repository").GetProperty("full_name").GetString() != repository)
                    throw new InvalidDataException("Managed routing repository mismatch.");
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    { BaseAddress = new("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
                var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrWhiteSpace(token))
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var api = new GitHubRepositoryApi(client, repository, repositoryId);
                await api.VerifyRepositoryAsync(default);
                var pull = payload.RootElement.GetProperty("pull_request");
                var number = pull.GetProperty("number").GetInt32();
                using var response = await api.GetAsync($"pulls/{number}", default);
                var live = response.RootElement;
                if (number <= 0 || live.GetProperty("number").GetInt32() != number ||
                    live.GetProperty("id").GetInt64() != pull.GetProperty("id").GetInt64() ||
                    live.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != repositoryId)
                    throw new InvalidDataException("Managed routing PR identity mismatch.");
                var state = (await new GitStateStore(new GitHubGitTransport(client, repository), repository).LoadAsync(default)).State;
                var decision = ManagedPushRouting.Decide(state, new(repository, number,
                    pull.GetProperty("head").GetProperty("sha").GetString()!,
                    live.GetProperty("head").GetProperty("sha").GetString()!,
                    live.GetProperty("state").GetString() == "open",
                    live.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() == repositoryId), true);
                run = decision.RunFullCi;
            }
            var value = run ? "true" : "false";
            output.WriteLine($"run_full_ci={value}");
            var outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
            if (!string.IsNullOrWhiteSpace(outputPath)) await File.AppendAllTextAsync(outputPath, $"run_full_ci={value}\n");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or
            ArgumentException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or
            OperationCanceledException or FormatException or OverflowException)
        {
            error.WriteLine($"Managed routing stopped ({exception.GetType().Name}); no ownership or completion inferred.");
            return 1;
        }
    }
}
