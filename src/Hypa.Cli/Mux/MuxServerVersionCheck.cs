using System.Reflection;
using System.Text.Json;
using Hypa.AgentRuntime.Infrastructure;

namespace Hypa.Cli.Mux;

/// <summary>
/// Whether a live mux was started from a different or removed install.
/// A package upgrade leaves the old mux running; it can no longer start
/// panes once its install directory is gone.
/// </summary>
public sealed record MuxServerVersionCheck(
    string ClientVersion,
    string? ServerVersion,
    bool InstallPresent)
{
    public bool InstallRemoved => !InstallPresent;

    /// <summary>Servers before 1.0.6 do not report a version.</summary>
    public bool VersionUnknown => string.IsNullOrWhiteSpace(ServerVersion);

    public bool IsStale =>
        InstallRemoved
        || VersionUnknown
        || !string.Equals(ServerVersion, ClientVersion, StringComparison.Ordinal);

    public string ServerVersionText => VersionUnknown ? "an older Hypa" : "Hypa " + ServerVersion;

    /// <summary>Why the mux needs a restart, for the attach prompt and status.</summary>
    public string Describe(string session)
    {
        var reason = InstallRemoved
            ? $"The mux for session '{session}' is running {ServerVersionText}, whose install was removed by an upgrade."
            : $"The mux for session '{session}' is running {ServerVersionText}.";
        var impact = InstallRemoved
            ? " New tabs and panes will fail until it restarts."
            : "";
        return $"{reason} This client is Hypa {ClientVersion}.{impact}";
    }

    public static string? CurrentClientVersion() =>
        ProcessServerInstallProbe.NormalizeVersion(
            Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);

    /// <summary>
    /// Reads <c>version</c> and <c>install_present</c> from a <c>ping</c>
    /// result. Returns null when the ping or client version is unreadable.
    /// </summary>
    public static MuxServerVersionCheck? FromPing(string? pingJson, string? clientVersion)
    {
        if (string.IsNullOrWhiteSpace(pingJson) || string.IsNullOrWhiteSpace(clientVersion))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(pingJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            string? serverVersion = null;
            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                serverVersion = ProcessServerInstallProbe.NormalizeVersion(version.GetString());
            var installPresent = !root.TryGetProperty("install_present", out var present)
                || present.ValueKind != JsonValueKind.False;
            return new MuxServerVersionCheck(clientVersion, serverVersion, installPresent);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
