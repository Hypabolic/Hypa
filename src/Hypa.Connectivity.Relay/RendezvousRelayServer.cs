using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;

namespace Hypa.Connectivity.Relay;

/// <summary>
/// Self-hosted relay. Binds matching outbound mux and client legs. Does not attach.
/// </summary>
public sealed class RendezvousRelayServer : IAsyncDisposable, IActiveJoinCloser
{
    private readonly TcpListener _listener;
    private readonly RendezvousRelayIdentity _identity;
    private readonly IJoinTrustGate? _trustGate;
    private readonly TimeProvider _clock;
    private readonly IRelayForwardSink _forwardSink;
    private readonly IRelayDurableStore _durableStore;
    private readonly X509Certificate2? _tlsCertificate;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingLeg> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumedNonces = new(StringComparer.Ordinal);
    private readonly string? _reusableNonce;
    private Task? _accept;

    public RendezvousRelayServer(
        RendezvousRelayIdentity identity,
        IPAddress address,
        int port = 0,
        IJoinTrustGate? trustGate = null,
        TimeProvider? clock = null,
        IRelayForwardSink? forwardSink = null,
        IRelayDurableStore? durableStore = null,
        string? dataDirectory = null,
        X509Certificate2? tlsCertificate = null,
        string? reusableNonce = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentNullException.ThrowIfNull(address);
        _trustGate = trustGate;
        _clock = clock ?? TimeProvider.System;
        _reusableNonce = string.IsNullOrWhiteSpace(reusableNonce) ? null : reusableNonce;
        _forwardSink = forwardSink ?? NullRelayForwardSink.Instance;
        _durableStore = durableStore ?? NullRelayDurableStore.Instance;
        DataDirectory = dataDirectory;
        _tlsCertificate = tlsCertificate;
        if (!string.IsNullOrWhiteSpace(dataDirectory))
            Directory.CreateDirectory(dataDirectory);

        _listener = new TcpListener(address, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BoundUrl = FormatBoundUrl(address, Port, tlsCertificate is not null);
    }

    public int Port { get; }

    public string BoundUrl { get; }

    public string? DataDirectory { get; }

    public IRelayDurableStore DurableStore => _durableStore;

    public RendezvousRelayIdentity Identity => _identity;

    public bool HasPendingJoin(JoinNonce nonce)
    {
        if (string.IsNullOrEmpty(nonce.Value))
            return false;

        lock (_gate)
            return _pending.ContainsKey(JoinSlot.Key(nonce));
    }

    public static RendezvousRelayServer StartSelfHostedLoopback()
    {
        var server = new RendezvousRelayServer(RendezvousRelayIdentity.SelfHostedV0, IPAddress.Loopback);
        server.Run();
        return server;
    }

    public static RendezvousRelayServer StartSelfHostedLoopback(
        IJoinTrustGate trustGate,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(trustGate);
        var server = new RendezvousRelayServer(
            RendezvousRelayIdentity.SelfHostedV0,
            IPAddress.Loopback,
            port: 0,
            trustGate,
            clock);
        server.Run();
        return server;
    }

    public static RendezvousRelayServer StartSelfHostedLoopback(
        IRelayForwardSink? forwardSink = null,
        IRelayDurableStore? durableStore = null,
        string? dataDirectory = null)
    {
        var server = new RendezvousRelayServer(
            RendezvousRelayIdentity.SelfHostedV0,
            IPAddress.Loopback,
            forwardSink: forwardSink,
            durableStore: durableStore,
            dataDirectory: dataDirectory);
        server.Run();
        return server;
    }

    public static RendezvousRelayServer StartSelfHostedLoopbackTls(
        X509Certificate2 tlsCertificate,
        IRelayForwardSink? forwardSink = null,
        IRelayDurableStore? durableStore = null,
        string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(tlsCertificate);
        var server = new RendezvousRelayServer(
            RendezvousRelayIdentity.SelfHostedV0,
            IPAddress.Loopback,
            forwardSink: forwardSink,
            durableStore: durableStore,
            dataDirectory: dataDirectory,
            tlsCertificate: tlsCertificate);
        server.Run();
        return server;
    }

    public IReadOnlyList<string> ListDataDirectoryFiles()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory) || !Directory.Exists(DataDirectory))
            return [];

        return Directory.GetFiles(DataDirectory, "*", SearchOption.AllDirectories);
    }

    public void Run()
    {
        _accept ??= AcceptLoopAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        FailAllPending(ConnectivityReasons.PeerUnavailable, "relay closed");
        _listener.Stop();
        if (_accept is not null)
        {
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            Stream stream;
            try
            {
                stream = client.GetStream();
                if (_tlsCertificate is not null)
                {
                    var wrapped = await RendezvousTls.WrapServerAsync(stream, _tlsCertificate, cancellationToken)
                        .ConfigureAwait(false);
                    if (!wrapped.Ok || wrapped.Value is null)
                        return;
                    stream = wrapped.Value;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var line = await BoundedJoinLine.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!line.Ok || line.Value is null)
            {
                await WriteFailureAsync(stream, line.WithoutValue(), cancellationToken).ConfigureAwait(false);
                return;
            }

            var parsed = JoinBootstrapCodec.Read(line.Value);
            if (!parsed.Ok || parsed.Value is null)
            {
                if (JoinBootstrapCodec.TryPeekJoinNonce(line.Value, out var peekedNonce))
                    FailSlot(peekedNonce, parsed.WithoutValue());
                await WriteFailureAsync(stream, parsed.WithoutValue(), cancellationToken).ConfigureAwait(false);
                return;
            }

            var bootstrap = parsed.Value;
            var now = _clock.GetUtcNow();
            // Pairing admission owns operator identity and expiry when a trust gate is present.
            var admitted = JoinMatcher.Admit(
                bootstrap,
                _identity,
                now,
                enforceSelfHostedLocalOperator: _trustGate is null,
                enforceCapabilityExpiry: _trustGate is null);
            if (!admitted.Ok)
            {
                FailSlot(bootstrap.Nonce, admitted);
                await WriteFailureAsync(stream, admitted, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_trustGate is not null)
            {
                var trusted = await _trustGate.AdmitAsync(bootstrap, now, cancellationToken)
                    .ConfigureAwait(false);
                if (!trusted.Ok)
                {
                    FailSlot(bootstrap.Nonce, trusted);
                    await WriteFailureAsync(stream, trusted, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            var handoff = await BindOrWaitAsync(bootstrap, stream, now, cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteJoinAsync(stream, handoff.Outcome, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                handoff.JoinWritten.TrySetResult();
            }

            if (handoff.Splicer is null)
                return;

            await handoff.PeerJoinWritten.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (handoff.OwnsSplicerRun)
                await handoff.Splicer.RunAsync(cancellationToken).ConfigureAwait(false);
            else
                await handoff.Splicer.WhenCompleted.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JoinHandoff> BindOrWaitAsync(
        JoinBootstrap bootstrap,
        Stream stream,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (_trustGate is not null)
        {
            var stillValid = await _trustGate
                .IsJoinStillValidAsync(bootstrap, utcNow, cancellationToken)
                .ConfigureAwait(false);
            if (!stillValid)
            {
                var revoked = ConnectivityOutcome<JoinBinding>.Failure(
                    ConnectivityReasons.JoinDenied,
                    "device is revoked");
                FailSlot(bootstrap.Nonce, revoked.WithoutValue());
                return JoinHandoff.Denied(revoked);
            }
        }

        var key = JoinSlot.Key(bootstrap.Nonce);
        TaskCompletionSource<JoinHandoff> completion;
        lock (_gate)
        {
            if (_consumedNonces.Contains(bootstrap.Nonce.Value)
                && !IsReusableNonce(bootstrap.Nonce))
            {
                return JoinHandoff.Denied(
                    ConnectivityOutcome<JoinBinding>.Failure(
                        ConnectivityReasons.JoinDenied,
                        "join nonce already used"));
            }

            if (_pending.Remove(key, out var peer))
            {
                var bound = JoinMatcher.Bind(peer.Bootstrap, bootstrap, _identity, _clock.GetUtcNow());
                if (!bound.Ok || bound.Value is null)
                {
                    var denied = ConnectivityOutcome<JoinBinding>.Failure(
                        bound.Reason ?? ConnectivityReasons.JoinDenied,
                        bound.Detail ?? "join denied");
                    RememberNonce(bootstrap.Nonce);
                    peer.Completion.TrySetResult(JoinHandoff.Denied(denied));
                    return JoinHandoff.Denied(denied);
                }

                RememberNonce(bootstrap.Nonce);
                var muxStream = peer.Bootstrap.Role == JoinRole.Mux ? peer.Stream : stream;
                var clientStream = peer.Bootstrap.Role == JoinRole.Client ? peer.Stream : stream;
                var splicer = new JoinedStreamSplicer(
                    muxStream,
                    clientStream,
                    forwardSink: _forwardSink,
                    durableStore: _durableStore);
                var firstWritten = NewJoinSignal();
                var secondWritten = NewJoinSignal();
                peer.Completion.TrySetResult(new JoinHandoff
                {
                    Outcome = ConnectivityOutcome<JoinBinding>.Success(bound.Value.First),
                    Splicer = splicer,
                    OwnsSplicerRun = false,
                    JoinWritten = firstWritten,
                    PeerJoinWritten = secondWritten.Task,
                });
                return new JoinHandoff
                {
                    Outcome = ConnectivityOutcome<JoinBinding>.Success(bound.Value.Second),
                    Splicer = splicer,
                    OwnsSplicerRun = true,
                    JoinWritten = secondWritten,
                    PeerJoinWritten = firstWritten.Task,
                };
            }

            completion = new TaskCompletionSource<JoinHandoff>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[key] = new PendingLeg
            {
                Bootstrap = bootstrap,
                Stream = stream,
                Completion = completion,
            };
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var cancelReg = linked.Token.Register(() =>
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(key, out var pending) && ReferenceEquals(pending.Completion, completion))
                    _pending.Remove(key);
            }

            completion.TrySetCanceled(linked.Token);
        });

        try
        {
            if (_trustGate is null)
                return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);

            while (!linked.Token.IsCancellationRequested)
            {
                var delay = Task.Delay(50, linked.Token);
                var finished = await Task.WhenAny(completion.Task, delay).ConfigureAwait(false);
                if (finished == completion.Task)
                    return await completion.Task.ConfigureAwait(false);

                var stillValid = await _trustGate
                    .IsJoinStillValidAsync(bootstrap, _clock.GetUtcNow(), linked.Token)
                    .ConfigureAwait(false);
                if (stillValid)
                    continue;

                var denied = ConnectivityOutcome<JoinBinding>.Failure(
                    ConnectivityReasons.JoinDenied,
                    "device is revoked");
                FailSlot(bootstrap.Nonce, denied.WithoutValue());
                return JoinHandoff.Denied(denied);
            }

            return JoinHandoff.Denied(JoinWaitCancelled(bootstrap.Nonce));
        }
        catch (OperationCanceledException)
        {
            return JoinHandoff.Denied(JoinWaitCancelled(bootstrap.Nonce));
        }
    }

    public void CloseJoinsForDevice(DeviceId deviceId)
    {
        if (string.IsNullOrEmpty(deviceId.Value))
            return;

        var denied = ConnectivityOutcome<JoinBinding>.Failure(
            ConnectivityReasons.JoinDenied,
            "device is revoked");
        List<PendingLeg> closing = [];
        lock (_gate)
        {
            foreach (var (key, pending) in _pending.ToArray())
            {
                if (pending.Bootstrap.Capability.DeviceId.Value != deviceId.Value)
                    continue;

                _pending.Remove(key);
                RememberNonce(pending.Bootstrap.Nonce);
                closing.Add(pending);
            }
        }

        foreach (var pending in closing)
            pending.Completion.TrySetResult(JoinHandoff.Denied(denied));
    }

    private void FailAllPending(string reason, string detail)
    {
        List<PendingLeg> legs;
        lock (_gate)
        {
            legs = _pending.Values.ToList();
            _pending.Clear();
            foreach (var leg in legs)
                RememberNonce(leg.Bootstrap.Nonce);
        }

        foreach (var leg in legs)
        {
            var loss = RelayFailurePolicy.ForJoinLoss(
                AttemptIds.FromJoinNonce(leg.Bootstrap.Nonce),
                reason);
            leg.Completion.TrySetResult(
                JoinHandoff.Denied(
                    ConnectivityOutcome<JoinBinding>.Failure(
                        reason,
                        detail,
                        stage: loss.Stage,
                        attemptId: loss.AttemptId,
                        retryable: loss.Retryable)));
        }
    }

    private void FailSlot(JoinNonce nonce, ConnectivityOutcome outcome)
    {
        if (string.IsNullOrEmpty(nonce.Value))
            return;

        var denied = ConnectivityOutcome<JoinBinding>.Failure(
            outcome.Reason ?? ConnectivityReasons.JoinDenied,
            outcome.Detail ?? "join denied",
            stage: outcome.Stage,
            attemptId: outcome.AttemptId,
            retryable: outcome.Retryable);
        lock (_gate)
        {
            RememberNonce(nonce);
            if (_pending.Remove(JoinSlot.Key(nonce), out var peer))
                peer.Completion.TrySetResult(JoinHandoff.Denied(denied));
        }
    }

    private void RememberNonce(JoinNonce nonce)
    {
        if (IsReusableNonce(nonce))
            return;
        _consumedNonces.Add(nonce.Value);
    }

    private bool IsReusableNonce(JoinNonce nonce) =>
        _reusableNonce is not null
        && string.Equals(nonce.Value, _reusableNonce, StringComparison.Ordinal);

    private static ConnectivityOutcome<JoinBinding> JoinWaitCancelled(JoinNonce nonce) =>
        ConnectivityOutcome<JoinBinding>.Failure(
            ConnectivityReasons.PeerUnavailable,
            "join wait cancelled",
            stage: RelayFailureStageWire.Join,
            attemptId: AttemptIds.FromJoinNonce(nonce),
            retryable: true);

    private static async Task WriteJoinAsync(
        Stream stream,
        ConnectivityOutcome<JoinBinding> outcome,
        CancellationToken cancellationToken)
    {
        JoinResultDocument document;
        if (outcome.Ok && outcome.Value is not null)
        {
            document = new JoinResultDocument
            {
                Ok = true,
                PlacementId = outcome.Value.PlacementId.Value,
                StreamClass = outcome.Value.StreamClass,
                Role = outcome.Value.Role,
                PeerEphPublicKey = outcome.Value.PeerEphPublicKey,
                PeerEphPublicMac = outcome.Value.PeerEphPublicMac,
            };
        }
        else
        {
            document = new JoinResultDocument
            {
                Ok = false,
                Reason = outcome.Reason ?? ConnectivityReasons.JoinDenied,
                Detail = outcome.Detail,
                Stage = outcome.Stage,
                AttemptId = outcome.AttemptId,
                Retryable = outcome.Retryable,
            };
        }

        await WriteDocumentAsync(stream, document, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFailureAsync(
        Stream stream,
        ConnectivityOutcome outcome,
        CancellationToken cancellationToken)
    {
        var document = new JoinResultDocument
        {
            Ok = false,
            Reason = outcome.Reason ?? ConnectivityReasons.JoinDenied,
            Detail = outcome.Detail,
            Stage = outcome.Stage,
            AttemptId = outcome.AttemptId,
            Retryable = outcome.Retryable,
        };
        await WriteDocumentAsync(stream, document, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDocumentAsync(
        Stream stream,
        JoinResultDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            await BoundedJoinLine.WriteAsync(stream, JoinBootstrapCodec.WriteResult(document), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string FormatBoundUrl(IPAddress address, int port, bool tls)
    {
        var scheme = tls ? "https://" : "http://";
        if (IPAddress.Loopback.Equals(address))
            return scheme + "127.0.0.1:" + port;
        if (IPAddress.IPv6Loopback.Equals(address))
            return scheme + "[::1]:" + port;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return scheme + "[" + address + "]:" + port;
        return scheme + address + ":" + port;
    }

    private static TaskCompletionSource NewJoinSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PendingLeg
    {
        public required JoinBootstrap Bootstrap { get; init; }
        public required Stream Stream { get; init; }
        public required TaskCompletionSource<JoinHandoff> Completion { get; init; }
    }

    private sealed class JoinHandoff
    {
        public required ConnectivityOutcome<JoinBinding> Outcome { get; init; }
        public JoinedStreamSplicer? Splicer { get; init; }
        public bool OwnsSplicerRun { get; init; }
        public required TaskCompletionSource JoinWritten { get; init; }
        public required Task PeerJoinWritten { get; init; }

        public static JoinHandoff Denied(ConnectivityOutcome<JoinBinding> outcome)
        {
            var done = NewJoinSignal();
            done.TrySetResult();
            return new JoinHandoff
            {
                Outcome = outcome,
                JoinWritten = NewJoinSignal(),
                PeerJoinWritten = done.Task,
            };
        }
    }
}
