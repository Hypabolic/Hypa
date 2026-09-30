namespace Hypa.Cli.Attach.ReleaseNotes;

/// <summary>
/// Loads pack notes and tracks whether the current version was dismissed.
/// </summary>
public sealed class PackNotesService
{
    private readonly PackNotesLoader _loader;
    private readonly PackNotesSeenStore _seenStore;

    public PackNotesService(
        PackNotesLoader? loader = null,
        PackNotesSeenStore? seenStore = null)
    {
        _loader = loader ?? new PackNotesLoader();
        _seenStore = seenStore ?? new PackNotesSeenStore();
    }

    public PackNotesDocument? TryLoad() => _loader.TryLoad();

    public bool ShouldShowOnStartup()
    {
        var notes = TryLoad();
        if (notes is null)
            return false;
        var seen = _seenStore.TryLoadSeenVersion();
        return !string.Equals(seen, notes.Version, StringComparison.Ordinal);
    }

    public bool MarkSeen()
    {
        var notes = TryLoad();
        if (notes is null)
            return false;
        return _seenStore.TryMarkSeen(notes.Version);
    }

    public string? TryLoadSeenVersion() => _seenStore.TryLoadSeenVersion();
}
