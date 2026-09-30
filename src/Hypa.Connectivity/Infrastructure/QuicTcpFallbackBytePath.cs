using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Attempts QUIC first when supported, then falls back to TLS/TCP.</summary>
public sealed class QuicTcpFallbackBytePath : IBytePath
{
    private readonly IBytePath _quic;
    private readonly IBytePath _tcp;
    private readonly IQuicTransportCapabilityProbe _probe;

    public QuicTcpFallbackBytePath(
        X509Certificate2? tlsTrust = null,
        IQuicTransportCapabilityProbe? probe = null,
        IBytePath? quicPath = null,
        IBytePath? tcpPath = null,
        TlsCertificatePin? certificatePin = null)
    {
        _probe = probe ?? new QuicTransportCapabilityProbe();
        _quic = quicPath ?? new QuicBytePath(tlsTrust, _probe, certificatePin);
        _tcp = tcpPath ?? new TcpTlsBytePath(tlsTrust, certificatePin);
    }

    public string Provider => BytePathProviders.Quic;

    public async ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
        BytePathRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Provider, Provider, StringComparison.Ordinal))
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.Internal,
                "byte path provider is not supported");
        }

        if (ShouldAttemptQuic(request))
        {
            var quic = await TryQuicDialAsync(request, cancellationToken).ConfigureAwait(false);
            if (quic.Ok)
                return quic;

            cancellationToken.ThrowIfCancellationRequested();
        }

        return await _tcp.OpenAsync(
                request with { Provider = BytePathProviders.Tcp },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _ = cancellationToken;
        try
        {
            await handle.Lifetime.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool ShouldAttemptQuic(BytePathRequest request)
    {
        var endpoint = request.Endpoint;
        if (endpoint is not { Tls: true })
            return false;

        if (endpoint.QuicListening == false)
            return false;

        return QuicTransportRuntime.IsUsable(_probe);
    }

    private async ValueTask<ConnectivityOutcome<BytePathHandle>> TryQuicDialAsync(
        BytePathRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var quicDial = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var quicAttempt = Task.Run(
            async () => await _quic.OpenAsync(request, quicDial.Token).ConfigureAwait(false),
            CancellationToken.None);

        var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(static state =>
            ((TaskCompletionSource)state!).TrySetResult(),
            cancelSignal))
        {
            var dialTimeout = Task.Delay(QuicTransportPolicy.DialTimeout);
            var finished = await Task.WhenAny(quicAttempt, dialTimeout, cancelSignal.Task).ConfigureAwait(false);
            if (finished == cancelSignal.Task)
            {
                await CancelAbandonedQuicDialAsync(quicDial, quicAttempt).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (finished == quicAttempt)
            {
                try
                {
                    return await quicAttempt.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await CancelAbandonedQuicDialAsync(quicDial, quicAttempt).ConfigureAwait(false);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return ConnectivityOutcome<BytePathHandle>.Failure(
                        ConnectivityReasons.PeerUnavailable,
                        "quic dial was canceled");
                }
            }

            await CancelAbandonedQuicDialAsync(quicDial, quicAttempt).ConfigureAwait(false);
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "quic dial timed out");
        }
    }

    private static async Task CancelAbandonedQuicDialAsync(
        CancellationTokenSource quicDial,
        Task<ConnectivityOutcome<BytePathHandle>> quicAttempt)
    {
        try
        {
            await quicDial.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        ObserveAbandonedQuicAttemptAsync(quicAttempt);
    }

    internal static Task ObserveAbandonedQuicAttemptForTestAsync(
        Task<ConnectivityOutcome<BytePathHandle>> quicAttempt) =>
        ObserveAbandonedQuicAttemptCoreAsync(quicAttempt);

    private static void ObserveAbandonedQuicAttemptAsync(
        Task<ConnectivityOutcome<BytePathHandle>> quicAttempt)
    {
        _ = ObserveAbandonedQuicAttemptCoreAsync(quicAttempt);
    }

    private static async Task ObserveAbandonedQuicAttemptCoreAsync(
        Task<ConnectivityOutcome<BytePathHandle>> quicAttempt)
    {
        try
        {
            var quic = await quicAttempt.ConfigureAwait(false);
            if (quic.Ok && quic.Value is not null)
                await quic.Value.Lifetime.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
