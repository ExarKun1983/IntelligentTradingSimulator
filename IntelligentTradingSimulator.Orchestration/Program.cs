using System.Text.Json;
using System.Net.Http.Headers;
using IntelligentTradingSimulator.Orchestration;

return await OrchestrationCommand.RunAsync(args, Console.Out, Console.Error);

public static class OrchestrationCommand
{
    private const int MaximumFixtureBytes = 2 * 1024 * 1024;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (!args.Contains("--github-read-only", StringComparer.Ordinal)) return Run(args, output, error);
        try
        {
            if (!args.Contains("--dry-run", StringComparer.Ordinal) || args.Contains("--execute", StringComparer.Ordinal) ||
                args.Contains("--context", StringComparer.Ordinal) || args.Contains("--replay", StringComparer.Ordinal))
                throw new InvalidDataException("GitHub mode is read-only and requires trusted Actions input.");
            var path = Argument(args, "--notification") ?? throw new InvalidDataException("Missing notification.");
            if (new FileInfo(path).Length > MaximumFixtureBytes) throw new InvalidDataException("Oversized notification.");
            using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30)
            };
            var credential = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(credential))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var repository = Argument(args, "--repository") ?? throw new InvalidDataException("Missing repository.");
            var api = new GitHubRepositoryApi(client, repository,
                long.Parse(Argument(args, "--repository-id")!, System.Globalization.CultureInfo.InvariantCulture));
            var host = new GitHubOrchestrationHost(api, new GitStateStore(new GitHubGitTransport(client, repository), repository));
            var result = await host.PreviewAsync(payload.RootElement, Argument(args, "--event-name")!,
                Argument(args, "--run-id")!,
                long.Parse(Argument(args, "--repository-generation")!, System.Globalization.CultureInfo.InvariantCulture),
                long.Parse(Argument(args, "--issue-generation")!, System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(Argument(args, "--occurred-at")!, System.Globalization.CultureInfo.InvariantCulture), []);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = "github-read-only", AcceptedDurably = false, ExternalEffectsExecuted = 0, Result = result,
                BlockedPrerequisites = new[] { "Task/review API and billing verification", "Exact-SHA CI routing", "Activation approval" }
            }, StateJson.Options));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or
            ArgumentException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or
            OperationCanceledException or FormatException or OverflowException)
        {
            error.WriteLine($"GitHub preview stopped ({exception.GetType().Name}); no live effects are enabled.");
            return 1;
        }
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (!args.Contains("--dry-run", StringComparer.Ordinal) ||
                args.Contains("--execute", StringComparer.Ordinal))
                throw new InvalidDataException("Live orchestration is disabled. Only --dry-run is supported.");
            var replay = Argument(args, "--replay");
            var notification = Argument(args, "--notification");
            if ((replay is null) == (notification is null))
                throw new InvalidDataException("Specify exactly one --replay or --notification file.");
            var path = replay ?? notification!;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFixtureBytes)
                throw new InvalidDataException("Missing or oversized fixture (maximum 2 MiB).");
            var json = File.ReadAllText(path);
            if (replay is not null)
            {
                var fixture = JsonSerializer.Deserialize<ReplayFixture>(json, StateJson.Options)
                    ?? throw new InvalidDataException("Missing replay fixture.");
                var result = OfflineReplay.Run(fixture);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Mode = "dry-run",
                    AcceptedDurably = false,
                    ExternalEffectsExecuted = 0,
                    result.State,
                    result.PendingEffects
                }, StateJson.Options));
                return 0;
            }

            var eventName = Argument(args, "--event-name")
                ?? throw new InvalidDataException("--event-name is required for a notification.");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (eventName is not ("issues" or "workflow_dispatch"))
                throw new InvalidDataException("Unsupported notification.");
            var contextPath = Argument(args, "--context");
            if (contextPath is not null)
            {
                var contextInfo = new FileInfo(contextPath);
                if (!contextInfo.Exists || contextInfo.Length > MaximumFixtureBytes)
                    throw new InvalidDataException("Missing or oversized notification context.");
                var context = JsonSerializer.Deserialize<NotificationReplayContext>(
                    File.ReadAllText(contextPath), StateJson.Options)
                    ?? throw new InvalidDataException("Missing notification context.");
                var decision = OfflineNotificationReplay.Run(root, eventName, context);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Mode = "notification-dry-run",
                    AcceptedDurably = false,
                    ExternalEffectsExecuted = 0,
                    decision.State,
                    decision.PendingEffects
                }, StateJson.Options));
                return 0;
            }
            var repository = root.GetProperty("repository").GetProperty("full_name").GetString();
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = "notification-preview",
                Repository = repository,
                Event = eventName,
                AcceptedDurably = false,
                ExternalEffectsExecuted = 0,
                StopReason = "Not activated. Trusted API identity/permission/live-state evidence and canonical persistence are required before acceptance."
            }, StateJson.Options));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException or KeyNotFoundException)
        {
            // Do not echo arbitrary payloads, local paths, or exception messages that could contain credentials.
            error.WriteLine($"Orchestration stopped ({exception.GetType().Name}); no live effects are enabled.");
            return 1;
        }
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new InvalidDataException("Missing argument value.");
        return args[index + 1];
    }
}

public sealed record ReplayFixture(
    RepositoryState State, List<OrchestrationEvent> Events, LiveObservation Observation,
    List<string> AuthorizedActors);

public sealed record NotificationReplayContext(
    RepositoryState State, LiveObservation Observation, IngressEvidence Evidence,
    string RunId, long RepositoryGeneration, long IssueGeneration, DateTimeOffset OccurredAt);

public static class OfflineNotificationReplay
{
    public static ReconcileDecision Run(JsonElement payload, string eventName, NotificationReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.State is null || context.Observation is null || context.Evidence is null ||
            context.State.Repository != context.Evidence.Repository)
            throw new InvalidDataException("Incomplete or mismatched notification context.");
        if (!context.Observation.Issues.TryGetValue(context.Evidence.IssueNumber, out var observed) ||
            observed.Ready != context.Evidence.Ready || observed.Closed == context.Evidence.IssueOpen)
            throw new InvalidDataException("Notification evidence contradicts the supplied live observation.");
        var message = GitHubActionsIngress.PrepareEvent(payload, eventName, context.RunId,
            context.Evidence, context.RepositoryGeneration, context.IssueGeneration, context.OccurredAt);
        // Like replay fixtures, supplied evidence is for offline validation, never production authorization.
        var reducer = new OrchestrationReducer(new NotificationAuthorization(message));
        var state = message is null ? context.State : reducer.Apply(context.State, message);
        return reducer.Reconcile(state, context.Observation);
    }

    private sealed class NotificationAuthorization(OrchestrationEvent? expected) : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) =>
            expected is not null && message.Digest == expected.Digest;
    }
}

public static class OfflineReplay
{
    public static ReconcileDecision Run(ReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.State is null || fixture.Events is null || fixture.Observation is null ||
            fixture.AuthorizedActors is null)
            throw new InvalidDataException("Incomplete replay fixture.");
        // Fixture authorization is deliberately confined to offline reduction; it is never a live credential.
        var reducer = new OrchestrationReducer(new FixtureAuthorization(fixture.AuthorizedActors));
        var state = fixture.State;
        foreach (var message in fixture.Events)
            state = reducer.Apply(state, message);
        return reducer.Reconcile(state, fixture.Observation);
    }

    private sealed class FixtureAuthorization(List<string> actors) : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) =>
            actors.Contains(message.Actor.Login, StringComparer.Ordinal);
    }
}
