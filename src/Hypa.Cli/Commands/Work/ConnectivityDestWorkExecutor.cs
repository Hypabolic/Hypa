using System.Text;
using System.Text.Json;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypa.Continuity.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Cli.Commands.Work;

/// <summary>
/// Source dest-worker client over Connectivity framed control. SSH is not
/// this path. Dest HOME is not on this type.
/// </summary>
public sealed class ConnectivityDestWorkExecutor : IDestWorkExecutor
{
    private readonly IFramedSession? _session;
    private readonly IDestWorkSessionOpener? _opener;
    private readonly DestWorkerInvocationTransport _transport;

    [ActivatorUtilitiesConstructor]
    public ConnectivityDestWorkExecutor(IDestWorkSessionOpener opener)
    {
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _transport = new DestWorkerInvocationTransport();
    }

    public ConnectivityDestWorkExecutor(
        IFramedSession session,
        DestWorkerInvocationTransport? transport = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _transport = transport ?? new DestWorkerInvocationTransport();
    }

    public IDestWorkSessionOpener? Opener => _opener;

    public async ValueTask<DestApplyResult> ApplyAsync(
        DestApplyInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(invocation.Request);
        var attemptId = invocation.Request.AttemptId;
        IFramedSession session;
        var ownsSession = false;
        if (_session is not null)
        {
            session = _session;
        }
        else
        {
            var opened = await _opener!
                .OpenAsync(invocation.Request.DestPlacementId, cancellationToken)
                .ConfigureAwait(false);
            if (!opened.Ok || opened.Value is null)
            {
                return DestApplyResult.Fail(
                    attemptId,
                    MapReason(opened.Reason),
                    opened.Detail ?? "dest worker session is not joined");
            }

            session = opened.Value;
            ownsSession = true;
        }

        try
        {
            return await InvokeAsync(session, invocation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsSession)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<DestApplyResult> InvokeAsync(
        IFramedSession session,
        DestApplyInvocation invocation,
        CancellationToken cancellationToken)
    {
        var attemptId = invocation.Request.AttemptId;
        if (string.IsNullOrWhiteSpace(invocation.PackPath) || !File.Exists(invocation.PackPath))
        {
            return DestApplyResult.Fail(
                attemptId,
                ContinuityReasons.PackInvalid,
                "source pack is missing");
        }

        var packBytes = new FileInfo(invocation.PackPath).Length;
        if (packBytes < 1)
        {
            return DestApplyResult.Fail(
                attemptId,
                ContinuityReasons.PackInvalid,
                "source pack is empty");
        }

        var request = invocation.Request with { PackBytes = packBytes };
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(
            request,
            ContinuityJsonContext.Default.DestApplyRequest);
        var sent = await _transport.SendUtf8Async(session, utf8, cancellationToken)
            .ConfigureAwait(false);
        if (!sent.Ok)
        {
            return DestApplyResult.Fail(
                attemptId,
                MapReason(sent.Reason),
                sent.Detail ?? "dest worker invoke failed");
        }

        var delivered = await SendPackAsync(session, invocation.PackPath, packBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!delivered.Ok)
        {
            return DestApplyResult.Fail(
                attemptId,
                MapReason(delivered.Reason),
                delivered.Detail ?? "dest pack send failed");
        }

        var received = await _transport.ReceiveUtf8Async(session, cancellationToken)
            .ConfigureAwait(false);
        if (!received.Ok || received.Value is null)
        {
            return DestApplyResult.Fail(
                attemptId,
                MapReason(received.Reason),
                received.Detail ?? "dest worker result missing");
        }

        var result = JsonSerializer.Deserialize(
            received.Value,
            ContinuityJsonContext.Default.DestApplyResult);
        if (result is null)
        {
            return DestApplyResult.Fail(
                attemptId,
                ContinuityReasons.Internal,
                "dest worker result is empty");
        }

        if (!string.Equals(result.AttemptId, attemptId, StringComparison.Ordinal))
        {
            return DestApplyResult.Fail(
                attemptId,
                ContinuityReasons.Internal,
                "dest worker attempt_id mismatch");
        }

        return result;
    }

    private async ValueTask<ConnectivityOutcome> SendPackAsync(
        IFramedSession session,
        string packPath,
        long packBytes,
        CancellationToken cancellationToken)
    {
        var chunkSize = ApplicationEncryption.MaxPlaintextBytes(
            StreamFrameKind.Binary,
            StreamBudget.Default);
        if (chunkSize < 1)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "dest pack chunk size is invalid");
        }

        var buffer = new byte[chunkSize];
        await using var src = new FileStream(
            packPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: chunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long remaining = packBytes;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var n = await src.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                .ConfigureAwait(false);
            if (n < 1)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.TransferIncomplete,
                    "source pack ended before PackBytes");
            }

            var payload = new byte[n];
            Buffer.BlockCopy(buffer, 0, payload, 0, n);
            var sent = await _transport.SendBinaryAsync(session, payload, cancellationToken)
                .ConfigureAwait(false);
            if (!sent.Ok)
                return sent;
            remaining -= n;
        }

        return ConnectivityOutcome.Success();
    }

    private static string MapReason(string? reason) =>
        reason is ConnectivityReasons.PeerUnavailable
            or ConnectivityReasons.StreamReset
            or ConnectivityReasons.TransferIncomplete
            or ConnectivityReasons.JoinDenied
            or ConnectivityReasons.JoinExpired
            or ConnectivityReasons.UnknownCommit
            ? reason
            : ContinuityReasons.PeerUnavailable;
}

/// <summary>
/// Destination host loop. Receives the apply request and pack. Runs dest apply.
/// Returns the result. Dest HOME lives on <see cref="DestApplyHostContext"/>.
/// </summary>
public sealed class DestWorkerHost
{
    private readonly IFramedSession _session;
    private readonly DestApplyService _apply;
    private readonly DestApplyHostContext _host;
    private readonly IWorkPackSpool _destSpool;
    private readonly DestWorkerInvocationTransport _transport;

    public DestWorkerHost(
        IFramedSession session,
        DestApplyService apply,
        DestApplyHostContext host,
        IWorkPackSpool destSpool,
        DestWorkerInvocationTransport? transport = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _destSpool = destSpool ?? throw new ArgumentNullException(nameof(destSpool));
        _transport = transport ?? new DestWorkerInvocationTransport();
    }

    public async ValueTask<ConnectivityOutcome<DestApplyResult>> ServeOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var received = await _transport.ReceiveUtf8Async(_session, cancellationToken)
            .ConfigureAwait(false);
        if (!received.Ok || received.Value is null)
        {
            return ConnectivityOutcome<DestApplyResult>.Failure(
                received.Reason ?? ConnectivityReasons.PeerUnavailable,
                received.Detail ?? "dest apply request missing");
        }

        var request = JsonSerializer.Deserialize(
            received.Value,
            ContinuityJsonContext.Default.DestApplyRequest);
        if (request is null || string.IsNullOrWhiteSpace(request.WorkId))
        {
            return ConnectivityOutcome<DestApplyResult>.Failure(
                ConnectivityReasons.StreamReset,
                "dest apply request is invalid");
        }

        Directory.CreateDirectory(_destSpool.SpoolDirectory);
        var destPack = Path.Combine(_destSpool.SpoolDirectory, request.WorkId.Trim() + ".workpack");
        if (request.PackBytes is > 0)
        {
            var packed = await ReceivePackAsync(destPack, request, cancellationToken)
                .ConfigureAwait(false);
            if (!packed.Ok)
            {
                var fail = DestApplyResult.Fail(
                    request.AttemptId,
                    ContinuityReasons.TransferIncomplete,
                    packed.Detail ?? "dest pack receive failed");
                return await SendResultAsync(fail, cancellationToken).ConfigureAwait(false);
            }
        }

        var result = await _apply.ApplyPackAsync(destPack, request, _host, cancellationToken)
            .ConfigureAwait(false);
        return await SendResultAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ConnectivityOutcome> ReceivePackAsync(
        string destPack,
        DestApplyRequest request,
        CancellationToken cancellationToken)
    {
        var packBytes = request.PackBytes!.Value;
        var partial = destPack + ".partial";
        try
        {
            if (File.Exists(partial))
                File.Delete(partial);

            await using (var dest = new FileStream(
                partial,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long remaining = packBytes;
                while (remaining > 0)
                {
                    var chunk = await _transport.ReceiveBinaryAsync(_session, cancellationToken)
                        .ConfigureAwait(false);
                    if (!chunk.Ok || chunk.Value is null)
                    {
                        return ConnectivityOutcome.Failure(
                            chunk.Reason ?? ConnectivityReasons.TransferIncomplete,
                            chunk.Detail ?? "dest pack receive failed");
                    }

                    if (chunk.Value.Length > remaining)
                    {
                        return ConnectivityOutcome.Failure(
                            ConnectivityReasons.StreamReset,
                            "dest pack exceeded PackBytes");
                    }

                    await dest.WriteAsync(chunk.Value, cancellationToken).ConfigureAwait(false);
                    remaining -= chunk.Value.Length;
                }
            }

            File.Move(partial, destPack, overwrite: true);
            var hash = string.IsNullOrWhiteSpace(request.PackSha256)
                ? new WorkPackCodec().HashPackFile(destPack)
                : request.PackSha256.Trim();
            File.WriteAllText(
                destPack + ".sha256",
                hash + "  " + Path.GetFileName(destPack) + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return ConnectivityOutcome.Success();
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(partial))
                    File.Delete(partial);
            }
            catch (IOException)
            {
            }

            return ConnectivityOutcome.Failure(
                ConnectivityReasons.TransferIncomplete,
                ex.Message);
        }
    }

    private async ValueTask<ConnectivityOutcome<DestApplyResult>> SendResultAsync(
        DestApplyResult result,
        CancellationToken cancellationToken)
    {
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(
            result,
            ContinuityJsonContext.Default.DestApplyResult);
        var sent = await _transport.SendUtf8Async(_session, utf8, cancellationToken)
            .ConfigureAwait(false);
        if (!sent.Ok)
        {
            return ConnectivityOutcome<DestApplyResult>.Failure(
                sent.Reason ?? ConnectivityReasons.PeerUnavailable,
                sent.Detail ?? "dest worker result send failed");
        }

        return ConnectivityOutcome<DestApplyResult>.Success(result);
    }
}
