using System.Net.Sockets;
using System.Text.Json;
using Hypa.ControlPlane;

namespace Hypa.Cli.Mux;

/// <summary>
/// Shared control-plane probe used by attach (supervisor) and mux stop.
/// </summary>
internal static class MuxControlPlane
{
    public static async Task<string?> TryPingAsync(string socketPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() && !File.Exists(socketPath) && !Directory.Exists(socketPath))
        {
            try
            {
                if (!Path.Exists(socketPath))
                    return null;
            }
            catch
            {
                return null;
            }
        }

        try
        {
            await using var client = new ControlPlaneClient(socketPath);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var result = await client.CallAsync("ping", ct: ct).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object)
                return null;
            if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                return null;
            if (!result.TryGetProperty("protocol", out var proto)
                || !proto.TryGetInt32(out var version)
                || version != 1)
            {
                return null;
            }

            return result.GetRawText();
        }
        catch (SocketException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
