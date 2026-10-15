using Hypa.AgentRuntime.Domain.Cubes.Connections;
using Hypa.Connectivity.Domain;

namespace Hypa.Cli.Mux;

/// <summary>
/// Builds ConnectError values from Connectivity outcomes and exceptions.
/// </summary>
internal static class ConnectErrors
{
    public static ConnectError FromOutcome<T>(ConnectivityOutcome<T> outcome) =>
        new(outcome.Reason ?? ConnectivityReasons.PeerUnavailable, OneLine(outcome.Detail));

    public static ConnectError FromException(string reason, Exception error) =>
        new(reason, OneLine(error.Message));

    /// <summary>Collapses a message to one trimmed line. Null when empty.</summary>
    public static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var firstLine = text.Split('\n', 2)[0].Trim();
        return firstLine.Length == 0 ? null : firstLine;
    }
}
