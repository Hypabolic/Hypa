using System.Net.Sockets;
using System.Text.Json;

namespace Hypa.Cli.Attach;

internal static class AttachSemanticClient
{
    public const int ExitOk = 0;

    public const int ExitFailed = 1;

    public const int ExitUsage = 4;

    public static async Task<int> ExecuteAsync(
        string sessionDir,
        string? attachClientId,
        AttachSemanticRequest request,
        TextWriter stdout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(request);
        if (OperatingSystem.IsWindows())
        {
            return await WriteAsync(
                    stdout,
                    new AttachSemanticReply
                    {
                        Action = request.Action,
                        Source = AttachSemanticSources.Cli,
                        Outcome = AttachSemanticOutcomes.Rejected,
                        Error = "attach actions use a Unix socket",
                    },
                    ExitFailed,
                    ct)
                .ConfigureAwait(false);
        }

        var clients = AttachClientDirectory.ListLive(sessionDir);
        AttachClientRecord? target = null;
        if (!string.IsNullOrWhiteSpace(attachClientId))
        {
            target = clients.FirstOrDefault(client =>
                string.Equals(client.AttachClientId, attachClientId, StringComparison.Ordinal));
            if (target is null)
            {
                return await WriteAsync(
                        stdout,
                        Absent(request, "attach client is not running"),
                        ExitFailed,
                        ct)
                    .ConfigureAwait(false);
            }
        }
        else if (clients.Count == 0)
        {
            return await WriteAsync(
                    stdout,
                    Absent(request, "no attach client is running"),
                    ExitFailed,
                    ct)
                .ConfigureAwait(false);
        }
        else if (clients.Count > 1)
        {
            return await WriteAsync(
                    stdout,
                    new AttachSemanticReply
                    {
                        Action = request.Action,
                        Source = AttachSemanticSources.Cli,
                        Outcome = AttachSemanticOutcomes.Rejected,
                        Error = "more than one attach client; pass --client",
                    },
                    ExitUsage,
                    ct)
                .ConfigureAwait(false);
        }
        else
        {
            target = clients[0];
        }

        request = request with { AttachClientId = target.AttachClientId };
        try
        {
            var reply = await SendAsync(target.Socket, request, ct).ConfigureAwait(false);
            var exit = reply.Outcome is AttachSemanticOutcomes.Applied or AttachSemanticOutcomes.Pending
                ? ExitOk
                : ExitFailed;
            return await WriteAsync(stdout, reply, exit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is SocketException or IOException or InvalidOperationException or JsonException)
        {
            return await WriteAsync(
                    stdout,
                    Absent(request, "attach client is not running"),
                    ExitFailed,
                    ct)
                .ConfigureAwait(false);
        }
    }

    public static async Task<AttachSemanticReply> SendAsync(
        string socketPath,
        AttachSemanticRequest request,
        CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        await using var stream = new NetworkStream(socket, ownsSocket: true);
        var json = JsonSerializer.Serialize(request, AttachSemanticJsonContext.Default.AttachSemanticRequest);
        await AttachSemanticIo.WriteLineAsync(stream, json, ct).ConfigureAwait(false);
        var line = await AttachSemanticIo.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return new AttachSemanticReply
            {
                Action = request.Action,
                Source = AttachSemanticSources.Cli,
                AttachClientId = request.AttachClientId,
                Outcome = AttachSemanticOutcomes.Rejected,
                Error = "attach closed the action socket",
            };
        }

        return JsonSerializer.Deserialize(line, AttachSemanticJsonContext.Default.AttachSemanticReply)
            ?? new AttachSemanticReply
            {
                Action = request.Action,
                Source = AttachSemanticSources.Cli,
                Outcome = AttachSemanticOutcomes.Rejected,
                Error = "action reply is empty",
            };
    }

    public static int WriteUsage(TextWriter stdout, string action, string error)
    {
        var json = JsonSerializer.Serialize(
            new AttachSemanticReply
            {
                Action = action,
                Source = AttachSemanticSources.Cli,
                Outcome = AttachSemanticOutcomes.Rejected,
                Error = error,
            },
            AttachSemanticJsonContext.Default.AttachSemanticReply);
        stdout.WriteLine(json);
        return ExitUsage;
    }

    private static AttachSemanticReply Absent(AttachSemanticRequest request, string error) =>
        new()
        {
            Action = request.Action,
            Source = AttachSemanticSources.Cli,
            AttachClientId = request.AttachClientId,
            Outcome = AttachSemanticOutcomes.Absent,
            Error = error,
        };

    private static Task<int> WriteAsync(
        TextWriter stdout,
        AttachSemanticReply reply,
        int exitCode,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(reply, AttachSemanticJsonContext.Default.AttachSemanticReply);
        stdout.WriteLine(json);
        return Task.FromResult(exitCode);
    }
}
