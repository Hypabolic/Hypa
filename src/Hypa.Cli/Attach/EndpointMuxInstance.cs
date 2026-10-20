namespace Hypa.Cli.Attach;

/// <summary>
/// One mux process and one connection to it: the boot id of the mux and the
/// generation of the connection. A restarted mux has a new boot id, and its
/// frame generations start again. A new connection to the same mux has a new
/// generation, a new subscription and no pane sizes of this client.
/// </summary>
internal readonly record struct EndpointMuxInstance(string BootId, ulong ConnectionGeneration)
{
    /// <summary>True when the boot id and the generation are both known.</summary>
    public bool IsKnown => !string.IsNullOrEmpty(BootId) && ConnectionGeneration > 0;

    /// <summary>
    /// True when <paramref name="other"/> is the same mux process. Its frame
    /// generations go on from the frames of the earlier connection. An
    /// unknown boot id never matches.
    /// </summary>
    public bool SameMux(EndpointMuxInstance? other) =>
        !string.IsNullOrEmpty(BootId)
        && other is { } known
        && string.Equals(BootId, known.BootId, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="other"/> is the same mux and the same
    /// connection. An unknown instance never matches: the caller then treats
    /// the connection as new.
    /// </summary>
    public bool SameConnection(EndpointMuxInstance? other) =>
        IsKnown
        && other is { IsKnown: true } known
        && string.Equals(BootId, known.BootId, StringComparison.Ordinal)
        && ConnectionGeneration == known.ConnectionGeneration;
}
