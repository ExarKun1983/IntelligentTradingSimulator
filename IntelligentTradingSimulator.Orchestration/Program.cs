using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;

return OrchestrationCommand.Run(args, Console.Out, Console.Error);

public static class OrchestrationCommand
{
    private const int MaximumFixtureBytes = 2 * 1024 * 1024;

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
