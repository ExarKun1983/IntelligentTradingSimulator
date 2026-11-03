namespace IntelligentTradingSimulator.Orchestration;

public sealed record StateSnapshot(string? Revision, RepositoryState State);
public interface IStateStore
{
    Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken);
    Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState state, CancellationToken cancellationToken);
}

public sealed record GitCommitObject(string Id, string StateJson);
public sealed record RefUpdateResult(bool Advanced);
public sealed class PublicationUncertainException(string message) : IOException(message);

/// <summary>Git objects and a compare-and-swap, non-force ref update; never a push or shell command.</summary>
public interface IGitObjectRefTransport
{
    Task<string?> ReadRefAsync(string reference, CancellationToken cancellationToken);
    Task<GitCommitObject> ReadCommitAsync(string commit, CancellationToken cancellationToken);
    Task<string> CreateBlobAsync(string content, CancellationToken cancellationToken);
    Task<string> CreateTreeAsync(string path, string blob, CancellationToken cancellationToken);
    Task<string> CreateCommitAsync(string tree, string? parent, string message, CancellationToken cancellationToken);
    Task<RefUpdateResult> TryAdvanceRefAsync(string reference, string? expectedTip, string commit, CancellationToken cancellationToken);
    Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken cancellationToken);
}

public sealed class GitStateStore(
    IGitObjectRefTransport transport,
    string repository,
    string reference = "refs/heads/agent-state",
    bool allowInitialization = false) : IStateStore
{
    private int observedExistingState;

    public async Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var tip = await transport.ReadRefAsync(reference, cancellationToken);
        if (tip is null)
        {
            RequireExplicitInitialization();
            return new(null, RepositoryState.Create(repository));
        }
        Interlocked.Exchange(ref observedExistingState, 1);
        var commit = await transport.ReadCommitAsync(tip, cancellationToken);
        if (commit.Id != tip) throw new InvalidDataException("Git commit identity mismatch.");
        var state = StateJson.Deserialize(commit.StateJson);
        if (state.Repository != repository || state.SchemaVersion != 1)
            throw new InvalidDataException("State repository or schema mismatch.");
        return new(tip, state);
    }

    public async Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState state, CancellationToken cancellationToken)
    {
        if (state.Repository != repository || state.SchemaVersion != 1 ||
            expected.State.Repository != repository || state.Generation < expected.State.Generation)
            throw new InvalidDataException("Invalid state publication.");
        var current = await transport.ReadRefAsync(reference, cancellationToken);
        if (current is null) RequireExplicitInitialization();
        else Interlocked.Exchange(ref observedExistingState, 1);
        if (current != expected.Revision) return false;
        var blob = await transport.CreateBlobAsync(StateJson.Serialize(state), cancellationToken);
        var tree = await transport.CreateTreeAsync("state.json", blob, cancellationToken);
        var commit = await transport.CreateCommitAsync(tree, expected.Revision,
            $"Orchestration state v{state.Version}", cancellationToken);
        // Bootstrap is a one-shot operator action; an uncertain attempt cannot authorize reinitialization.
        if (expected.Revision is null) Interlocked.Exchange(ref observedExistingState, 1);
        try
        {
            var advanced = (await transport.TryAdvanceRefAsync(reference, expected.Revision, commit, cancellationToken)).Advanced;
            if (advanced) Interlocked.Exchange(ref observedExistingState, 1);
            return advanced;
        }
        catch (PublicationUncertainException)
        {
            // A lost response is not a failed write: find the exact commit before re-reducing.
            var observed = await transport.ReadRefAsync(reference, cancellationToken);
            if (observed is null)
            {
                RequireExplicitInitialization();
                return false;
            }
            Interlocked.Exchange(ref observedExistingState, 1);
            return observed == commit || await transport.IsAncestorAsync(commit, observed, cancellationToken);
        }
    }

    private void RequireExplicitInitialization()
    {
        if (!allowInitialization || Volatile.Read(ref observedExistingState) != 0)
            throw new InvalidDataException("Canonical state ref is missing; explicit operator bootstrap is required.");
    }
}
