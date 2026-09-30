using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Outbound join that keeps the byte path and switches to framed multiplex.
/// </summary>
public sealed class OutboundFramedSession : IFramedSession
{
    private readonly Stream _stream;
    private readonly IAsyncDisposable _pathLifetime;
    private readonly StreamBudget _budget;
    private readonly IApplicationFrameCipher _cipher;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly DirectionFlowControl _incoming;
    private readonly ChannelSequenceTracker _received = new();
    private readonly TaskCompletionSource<ConnectivityOutcome<StreamFrame>> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _receive;
    private readonly object _pauseGate = new();
    private TaskCompletionSource _resume = NewResume();
    private bool _binaryPaused;
    private long _nextSequence;
    private bool _disposed;

    private OutboundFramedSession(
        Stream stream,
        IAsyncDisposable pathLifetime,
        JoinBinding binding,
        StreamBudget budget,
        IApplicationFrameCipher cipher)
    {
        _stream = stream;
        _pathLifetime = pathLifetime ?? throw new ArgumentNullException(nameof(pathLifetime));
        Binding = binding;
        _budget = budget;
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
        _incoming = new DirectionFlowControl(StreamFrameRules.Incoming(binding.Role), budget);
        _receive = ReceiveLoopAsync(_lifetime.Token);
    }

    public JoinBinding Binding { get; }

    public bool ApplicationEncryptionEnabled => true;

    public bool IsBinaryPaused
    {
        get
        {
            lock (_pauseGate)
                return _binaryPaused;
        }
    }

    public long IncomingBinaryBytes => _incoming.BinaryBytes;

    public int IncomingQueuedFrames => _incoming.QueuedFrameCount;

    public IReadOnlyList<ChannelCursor> LastReceived => _received.Snapshot();

    public static Task<ConnectivityOutcome<OutboundFramedSession>> ConnectAsync(
        RendezvousUrl url,
        JoinBootstrap bootstrap,
        StreamBudget? budget = null,
        X509Certificate2? tlsTrust = null,
        IBytePath? bytePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!BytePathEndpointRules.TryFromRendezvousUrl(url, out var endpoint, out var endpointDetail))
        {
            return Task.FromResult(ConnectivityOutcome<OutboundFramedSession>.Failure(
                ConnectivityReasons.RendezvousUrlInvalid,
                endpointDetail));
        }

        return ConnectDirectAsync(endpoint, bootstrap, budget, tlsTrust, bytePath, cancellationToken);
    }

    public static Task<ConnectivityOutcome<OutboundFramedSession>> ConnectDirectAsync(
        BytePathEndpoint endpoint,
        JoinBootstrap bootstrap,
        StreamBudget? budget = null,
        X509Certificate2? tlsTrust = null,
        IBytePath? bytePath = null,
        CancellationToken cancellationToken = default) =>
        ConnectDirectAsync(
            endpoint,
            bootstrap,
            budget,
            tlsTrust,
            bytePath,
            deviceKeys: null,
            certificatePin: null,
            cancellationToken);

    public static Task<ConnectivityOutcome<OutboundFramedSession>> ConnectDirectAsync(
        BytePathEndpoint endpoint,
        JoinBootstrap bootstrap,
        StreamBudget? budget,
        X509Certificate2? tlsTrust,
        IBytePath? bytePath,
        IDeviceKeyStore? deviceKeys,
        TlsCertificatePin? certificatePin,
        CancellationToken cancellationToken = default)
    {
        if (!BytePathEndpointRules.TryValidate(endpoint, out var invalid))
        {
            return Task.FromResult(ConnectivityOutcome<OutboundFramedSession>.Failure(
                ConnectivityReasons.PeerUnavailable,
                invalid));
        }

        return ConnectDirectCoreAsync(
            endpoint,
            bootstrap,
            budget,
            tlsTrust,
            bytePath,
            deviceKeys,
            certificatePin,
            cancellationToken);
    }

    /// <summary>
    /// Mux-role session on an already joined accept stream.
    /// Join lines are complete. The next bytes are Connectivity frames.
    /// </summary>
    public static OutboundFramedSession FromAccepted(
        Stream stream,
        JoinBinding binding,
        IApplicationFrameCipher cipher,
        StreamBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(cipher);
        if (binding.Role != JoinRole.Mux)
            throw new ArgumentException("accepted session role must be mux", nameof(binding));

        return new OutboundFramedSession(
            stream,
            NullPathLifetime.Instance,
            binding,
            budget ?? StreamBudget.Default,
            cipher);
    }

    private static async Task<ConnectivityOutcome<OutboundFramedSession>> ConnectDirectCoreAsync(
        BytePathEndpoint endpoint,
        JoinBootstrap bootstrap,
        StreamBudget? budget,
        X509Certificate2? tlsTrust,
        IBytePath? bytePath,
        IDeviceKeyStore? deviceKeys,
        TlsCertificatePin? certificatePin,
        CancellationToken cancellationToken)
    {
        var pathProvider = bytePath ?? new TcpTlsBytePath(tlsTrust, certificatePin);
        BytePathHandle? path = null;
        JoinEphemeralKeyPair? keys = null;
        try
        {
            keys = JoinEphemeralKeyPair.Create();
            var prepared = bootstrap.Capability.Secret.IsEmpty
                ? JoinBootstrapRules.Validate(bootstrap)
                : JoinEphAuthenticator.Stamp(bootstrap, keys);
            if (!prepared.Ok || prepared.Value is null)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    prepared.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                    prepared.Detail ?? "join secret is required");
            }

            var stamped = prepared.Value.Capability.Secret.IsEmpty
                ? ConnectivityOutcome<JoinBootstrap>.Success(
                    prepared.Value with { EphPublicKey = keys.PublicKey })
                : prepared;
            if (!stamped.Ok || stamped.Value is null)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    stamped.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                    stamped.Detail ?? "join secret is required");
            }

            var outbound = stamped.Value;
            if (deviceKeys is not null)
            {
                var signed = await JoinDeviceAuthenticator.StampAsync(outbound, deviceKeys, cancellationToken)
                    .ConfigureAwait(false);
                if (!signed.Ok || signed.Value is null)
                {
                    return ConnectivityOutcome<OutboundFramedSession>.Failure(
                        signed.Reason ?? ConnectivityReasons.Unauthorized,
                        signed.Detail ?? "device signature is required");
                }

                outbound = signed.Value;
            }
            var admittedOutbound = JoinMatcher.Admit(outbound, InferRelay(outbound), DateTimeOffset.UtcNow);
            if (!admittedOutbound.Ok)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    admittedOutbound.Reason ?? ConnectivityReasons.JoinDenied,
                    admittedOutbound.Detail ?? "bootstrap denied");
            }

            var opened = await pathProvider.OpenAsync(
                    new BytePathRequest
                    {
                        Provider = pathProvider.Provider,
                        Endpoint = endpoint,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!opened.Ok || opened.Value is null)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    opened.Reason ?? ConnectivityReasons.PeerUnavailable,
                    opened.Detail ?? "path open failed");
            }

            path = opened.Value;
            var stream = path.Stream;
            await BoundedJoinLine.WriteAsync(stream, JoinBootstrapCodec.Write(outbound), cancellationToken)
                .ConfigureAwait(false);
            var line = await BoundedJoinLine.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!line.Ok || line.Value is null)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    line.Reason ?? ConnectivityReasons.PeerUnavailable,
                    line.Detail ?? "relay closed");
            }

            var parsed = JoinBootstrapCodec.ReadResult(line.Value);
            if (!parsed.Ok || parsed.Value is null)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    parsed.Reason ?? ConnectivityReasons.BootstrapInvalid,
                    parsed.Detail ?? "join result json is invalid");
            }

            var result = parsed.Value;
            if (!result.Ok)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    string.IsNullOrWhiteSpace(result.Reason)
                        ? ConnectivityReasons.JoinDenied
                        : result.Reason,
                    result.Detail ?? "join denied");
            }

            if (string.IsNullOrWhiteSpace(result.PlacementId)
                || result.StreamClass is null
                || result.Role is null)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "join result is incomplete");
            }

            if (!outbound.Capability.Secret.IsEmpty)
            {
                var peer = JoinEphAuthenticator.VerifyPeer(
                    outbound.Capability.Secret,
                    result.PeerEphPublicKey,
                    result.PeerEphPublicMac);
                if (!peer.Ok)
                {
                    return ConnectivityOutcome<OutboundFramedSession>.Failure(
                        peer.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                        peer.Detail ?? "peer ephemeral public key is required");
                }
            }
            else if (string.IsNullOrWhiteSpace(result.PeerEphPublicKey))
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    ConnectivityReasons.ApplicationEncryptionRequired,
                    "peer ephemeral public key is required");
            }

            var shared = keys.DeriveSharedSecret(result.PeerEphPublicKey);
            if (!shared.Ok || shared.Value is null)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    shared.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                    shared.Detail ?? "peer ephemeral public key is required");
            }

            if (!PlacementId.TryParse(result.PlacementId, out var placementId))
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "join result placement id is invalid");
            }

            var cipher = ApplicationFrameCipher.Create(
                shared.Value,
                outbound.Nonce,
                placementId.Value,
                outbound.Role);
            if (!cipher.Ok || cipher.Value is null)
            {
                return ConnectivityOutcome<OutboundFramedSession>.Failure(
                    cipher.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                    cipher.Detail ?? "application encryption is required");
            }

            var binding = new JoinBinding
            {
                PlacementId = placementId,
                StreamClass = result.StreamClass.Value,
                Role = result.Role.Value,
                PeerEphPublicKey = result.PeerEphPublicKey!,
                PeerEphPublicMac = result.PeerEphPublicMac ?? "",
            };
            var session = new OutboundFramedSession(
                stream,
                path.Lifetime,
                binding,
                budget ?? StreamBudget.Default,
                cipher.Value);
            var outcome = ConnectivityOutcome<OutboundFramedSession>.Success(session);
            path = null;
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            return ConnectivityOutcome<OutboundFramedSession>.Failure(
                ConnectivityReasons.PeerUnavailable,
                ex.Message);
        }
        finally
        {
            keys?.Dispose();
            if (path is not null)
                await pathProvider.CloseAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask<ConnectivityOutcome> SendAsync(
        StreamFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var outbound = frame with
        {
            Direction = StreamFrameRules.Outgoing(Binding.Role),
            Sequence = NextSequence(),
        };
        var valid = StreamFrameRules.ValidateFrame(outbound, _budget);
        if (!valid.Ok)
            return valid;

        var sealedFrame = _cipher.Seal(outbound);
        if (!sealedFrame.Ok || sealedFrame.Value is null)
        {
            return ConnectivityOutcome.Failure(
                sealedFrame.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                sealedFrame.Detail ?? "application encryption is required");
        }

        if (outbound.CountsAsBinary)
            await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StreamFrameCodec.WriteAsync(_stream, sealedFrame.Value, cancellationToken).ConfigureAwait(false);
            return ConnectivityOutcome.Success();
        }
        catch (IOException ex)
        {
            return ConnectivityOutcome.Failure(ConnectivityReasons.PeerUnavailable, ex.Message);
        }
        catch (ObjectDisposedException)
        {
            return ConnectivityOutcome.Failure(ConnectivityReasons.PeerUnavailable, "session closed");
        }
        finally
        {
            _write.Release();
        }
    }

    public async ValueTask<ConnectivityOutcome<StreamFrame>> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed.Task.IsCompleted)
                return await _closed.Task.ConfigureAwait(false);

            if (_incoming.TryDequeue(out var frame))
            {
                _incoming.CompleteWrite(frame);
                _received.Note(frame);
                return ConnectivityOutcome<StreamFrame>.Success(frame);
            }

            var wait = _incoming.WaitForDataAsync(cancellationToken);
            var finished = await Task.WhenAny(wait, _closed.Task).ConfigureAwait(false);
            if (finished == _closed.Task)
                return await _closed.Task.ConfigureAwait(false);

            await wait.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        CloseIncoming(
            ConnectivityOutcome<StreamFrame>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "session closed"));
        try
        {
            await _receive.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        await _pathLifetime.DisposeAsync().ConfigureAwait(false);
        _write.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var header = await StreamFrameCodec.ReadHeaderAsync(_stream, _budget, cancellationToken)
                    .ConfigureAwait(false);
                if (!header.Ok)
                {
                    CloseIncoming(
                        ConnectivityOutcome<StreamFrame>.Failure(
                            header.Reason ?? ConnectivityReasons.PeerUnavailable,
                            header.Detail ?? "peer closed"));
                    break;
                }

                var parsed = header.Value;
                var body = await StreamFrameCodec.ReadExactAsync(_stream, parsed.Length, cancellationToken)
                    .ConfigureAwait(false);
                if (!body.Ok || body.Value is null)
                {
                    CloseIncoming(
                        ConnectivityOutcome<StreamFrame>.Failure(
                            body.Reason ?? ConnectivityReasons.PeerUnavailable,
                            body.Detail ?? "peer closed"));
                    break;
                }

                var value = StreamFrameCodec.ToFrame(parsed, body.Value);
                if (value.IsFlowControl)
                {
                    ApplyFlow(value);
                    continue;
                }

                var opened = _cipher.Open(value);
                if (!opened.Ok || opened.Value is null)
                {
                    CloseIncoming(
                        ConnectivityOutcome<StreamFrame>.Failure(
                            opened.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                            opened.Detail ?? "application frame is not encrypted"));
                    break;
                }

                value = opened.Value;
                if (value.CountsAsBinary)
                {
                    await _incoming.WaitForBinaryRoomAsync(value.PayloadLength, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (_incoming.TryEnqueue(value, DateTimeOffset.UtcNow))
                    continue;

                if (!value.CountsAsBinary)
                {
                    CloseIncoming(
                        ConnectivityOutcome<StreamFrame>.Failure(
                            ConnectivityReasons.StreamReset,
                            "control reserved capacity is exhausted"));
                    break;
                }

                await _incoming.WaitForBinaryRoomAsync(value.PayloadLength, cancellationToken)
                    .ConfigureAwait(false);
                if (!_incoming.TryEnqueue(value, DateTimeOffset.UtcNow))
                {
                    CloseIncoming(
                        ConnectivityOutcome<StreamFrame>.Failure(
                            ConnectivityReasons.StreamReset,
                            "incoming binary queue is full"));
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            CloseIncoming(
                ConnectivityOutcome<StreamFrame>.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    ex.Message));
        }
        finally
        {
            CloseIncoming(
                ConnectivityOutcome<StreamFrame>.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    "session closed"));
            SignalResume();
        }
    }

    private void CloseIncoming(ConnectivityOutcome<StreamFrame> outcome)
    {
        _closed.TrySetResult(outcome);
    }

    private void ApplyFlow(StreamFrame frame)
    {
        if (!StreamFrameRules.TryReadFlowCommand(frame, out var command))
            return;
        if (frame.Direction != StreamFrameRules.Outgoing(Binding.Role))
            return;

        lock (_pauseGate)
        {
            _binaryPaused = command == StreamFlowCommand.Pause;
            if (_binaryPaused)
                _resume = NewResume();
            else
                _resume.TrySetResult();
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_pauseGate)
        {
            if (!_binaryPaused)
                return;
            wait = _resume.Task;
        }

        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SignalResume()
    {
        lock (_pauseGate)
        {
            _binaryPaused = false;
            _resume.TrySetResult();
        }
    }

    private ulong NextSequence() => (ulong)Interlocked.Increment(ref _nextSequence);

    private static TaskCompletionSource NewResume() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RendezvousRelayIdentity InferRelay(JoinBootstrap bootstrap) =>
        new()
        {
            Deployment = string.Equals(bootstrap.TenantScope, RendezvousTenants.Local, StringComparison.Ordinal)
                ? RendezvousDeployment.SelfHosted
                : RendezvousDeployment.Hosted,
            Audience = bootstrap.Audience,
            TenantScope = bootstrap.TenantScope,
            ProtocolVersion = bootstrap.ProtocolVersion,
        };

    private sealed class NullPathLifetime : IAsyncDisposable
    {
        public static readonly NullPathLifetime Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
