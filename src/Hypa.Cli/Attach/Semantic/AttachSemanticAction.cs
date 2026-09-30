using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;

namespace Hypa.Cli.Attach;

/// <summary>
/// The running attach admits the action on its own connection.
/// </summary>
internal static class AttachSemanticActions
{
    public const string TabFocus = "tab.focus";

    public const string PlacementConnect = "placement.connect";

    public const string SettingsOpen = "settings.open";

    public const string SettingsClose = "settings.close";
}

internal static class AttachSemanticOutcomes
{
    public const string Applied = "applied";

    public const string Pending = "pending";

    public const string Rejected = "rejected";

    public const string Absent = "absent";
}

internal static class AttachSemanticSources
{
    public const string Cli = "cli";

    public const string Mouse = "mouse";

    /// <summary>A mux show moved this client to a new tab.</summary>
    public const string Mux = "mux";
}

internal sealed record AttachSemanticRequest
{
    public string Action { get; init; } = "";

    public string? TabId { get; init; }

    public string? PlacementId { get; init; }

    /// <summary>Settings page id for settings.open. Null opens the first page.</summary>
    public string? PageId { get; init; }

    public string? RequestId { get; init; }

    public string? AttachClientId { get; init; }

    public string? EndpointId { get; init; }

    public ulong? ConnectionGeneration { get; init; }

    public bool Verbose { get; init; }
}

internal sealed record AttachSemanticReply
{
    public string? RequestId { get; init; }

    public string Action { get; init; } = "";

    public string Source { get; init; } = AttachSemanticSources.Cli;

    public string? AttachClientId { get; init; }

    public string? EndpointId { get; init; }

    public ulong? ConnectionGeneration { get; init; }

    public string Outcome { get; init; } = "";

    public string? WorkspaceId { get; init; }

    public string? TabId { get; init; }

    public string? PaneId { get; init; }

    public string? ConnectedPlacementId { get; init; }

    public string? Error { get; init; }

    public string? Detail { get; init; }
}

internal sealed record AttachClientRecord
{
    public string AttachClientId { get; init; } = "";

    public string Socket { get; init; } = "";

    public string? EndpointId { get; init; }

    public ulong ConnectionGeneration { get; init; }

    public int Pid { get; init; }
}

internal sealed record AttachClientIndex
{
    public List<AttachClientRecord>? Clients { get; init; }
}

internal static class AttachSemanticIdentity
{
    public static (string? EndpointId, ulong Generation) Read(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        string? endpoint = live.PendingActivation?.Target.EndpointId;
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = live.ActiveProjectionEndpointId;
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = live.SelectedCubeId;
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = live.ConnectedPlacementId;
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = null;
        return (endpoint, live.TransportEnvelope.Generation);
    }
}

internal static class AttachSemanticIo
{
    public const int MaxLineBytes = 65_536;

    public static int MaxSocketPathBytes => OperatingSystem.IsMacOS() ? 103 : 107;

    public static void EnsureSocketPathFits(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) > MaxSocketPathBytes)
            throw new InvalidOperationException("attach action socket path is too long");
    }

    public static void ClearStaleSocket(string path)
    {
        if (!File.Exists(path))
            return;

        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException)
        {
            File.Delete(path);
            return;
        }

        throw new InvalidOperationException("attach action socket is already open");
    }

    public static async Task WriteLineAsync(Stream stream, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxLineBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer[count] == (byte)'\n')
                return Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\r');
            count++;
        }

        if (count == 0)
            return null;
        if (count >= buffer.Length)
            throw new InvalidOperationException("attach action request is too large");
        return Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\r');
    }
}

[JsonSerializable(typeof(AttachSemanticRequest))]
[JsonSerializable(typeof(AttachSemanticReply))]
[JsonSerializable(typeof(AttachClientIndex))]
[JsonSerializable(typeof(AttachClientRecord))]
[JsonSerializable(typeof(List<AttachClientRecord>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AttachSemanticJsonContext : JsonSerializerContext;
