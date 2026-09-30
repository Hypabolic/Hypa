namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Creates <see cref="IVtEngine"/> instances from process-level VT provider selection.
/// Engine kind is locked for the process; panes do not re-read env or swap mid-life.
/// </summary>
public interface IVtEngineFactory
{
    /// <summary>Process-level provider selection used by this factory.</summary>
    VtProviderSelection Selection { get; }

    /// <summary>Wire provider name (<c>ghostty</c>).</summary>
    string ProviderWireName { get; }

    /// <summary>
    /// Create an engine for a new pane. Dimensions are validated against the floor budget.
    /// Always Ghostty. Missing native fails closed (never silent Basic).
    /// </summary>
    IVtEngine Create(int cols, int rows);

    IVtEngine Create(int cols, int rows, int maxScrollback) => Create(cols, rows);
}

/// <summary>
/// Default factory: Ghostty from a fixed <see cref="VtProviderSelection"/>.
/// Construct once at process start (AgentServer DI singleton).
/// </summary>
public sealed class VtEngineFactory : IVtEngineFactory
{
    private readonly VtProviderSelection _selection;

    public VtEngineFactory(VtProviderSelection selection)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    /// <summary>Convenience: selection from environment at factory construction.</summary>
    public static VtEngineFactory FromEnvironment() =>
        new(VtProviderSelection.FromEnvironment());

    public VtProviderSelection Selection => _selection;

    public string ProviderWireName => _selection.ProviderWireName;

    public IVtEngine Create(int cols, int rows) =>
        Create(cols, rows, GhosttyVtEngine.DefaultMaxScrollback);

    public IVtEngine Create(int cols, int rows, int maxScrollback)
    {
        BasicVtFloor.EnsureValidDimensions(cols, rows);

        return new GhosttyVtEngine(
            cols,
            rows,
            maxScrollback: maxScrollback,
            libraryPathOverride: _selection.LibraryPathOverride);
    }
}
