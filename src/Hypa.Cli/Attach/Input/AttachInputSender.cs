using System.Threading.Channels;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Input;

/// <summary>
/// Bounded ordered attach input. The stdin loop enqueues decoded bytes and
/// never waits for <c>pane.send_keys</c>. One sender batches and writes a
/// JSON-RPC notification. It does not wait for an admission reply.
/// </summary>
public sealed class AttachInputSender : IAsyncDisposable
{
    public const int DefaultCapacity = 64;
    public const int DefaultMaxQueuedBytes = 256 * 1024;
    public const int TransientInvalidStateLimit = 3;

    /// <summary>
    // / InvalidState bursts reset after this quiet window.
    /// <c>src/server/headless.rs:3141-3143</c> continues after apply failure.
    /// Three lifetime queue-full events must not latch detach.
    /// </summary>
    public static readonly TimeSpan TransientInvalidStateWindow = TimeSpan.FromSeconds(1);

    private readonly Channel<byte[]> _channel;
    private ControlPlaneClient _client;
    private readonly Func<(string PaneId, string LeaseId)> _target;
    private readonly Func<bool>? _blocksForward;
    private readonly Action<AttachInputFault>? _onFault;
    private readonly SemaphoreSlim _sendAdmission = new(1, 1);
    private readonly SemaphoreSlim _retargetAdmission = new(1, 1);
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();
    private long _sendGeneration;
    private int _queuedBytes;
    private int _rejected;
    private int _undeliverable;
    private Exception? _fault;

    public AttachInputSender(
        ControlPlaneClient client,
        Func<(string PaneId, string LeaseId)> target,
        int capacity = DefaultCapacity,
        Action<AttachInputFault>? onFault = null,
        Func<bool>? blocksForward = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        _client = client;
        _target = target;
        _blocksForward = blocksForward;
        _onFault = onFault;
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    internal void Rebind(ControlPlaneClient client) =>
        Retarget(client, installTarget: null);

    /// <summary>Test seam: in-flight notify gate for concurrent send tests.</summary>
    internal SemaphoreSlim SendAdmissionForTests => _sendAdmission;

    /// <summary>Test seam: retarget drain gate for concurrent retarget fencing tests.</summary>
    internal SemaphoreSlim RetargetAdmissionForTests => _retargetAdmission;

    /// <summary>Test seam: set true to block delivery inside <see cref="NotifyAsync"/>.</summary>
    internal bool BlockNotifyForTests { get; set; }

    /// <summary>Test seam: signaled when delivery reaches the in-flight notify gate.</summary>
    internal SemaphoreSlim NotifyEnteredForTests { get; } = new(0, 1);

    /// <summary>Test seam: release to unblock an in-flight notify blocked for tests.</summary>
    internal SemaphoreSlim NotifyHoldForTests { get; } = new(0, 1);

    /// <summary>
    /// Quiesce queued input and atomically install a new client/target generation.
    /// <c>catalog_reload.rs:59-65</c> freezes presentation without ending the client.
    /// Retirement must not deliver pre-switch bytes.
    /// </summary>
    internal void Retarget(ControlPlaneClient client, Action? installTarget)
    {
        ArgumentNullException.ThrowIfNull(client);
        _sendAdmission.Wait();
        _retargetAdmission.Wait();
        try
        {
            DrainQueuedInternal();
            Interlocked.Increment(ref _sendGeneration);
            installTarget?.Invoke();
            _client = client;
        }
        finally
        {
            _retargetAdmission.Release();
            _sendAdmission.Release();
        }
    }

    public bool IsFaulted => Volatile.Read(ref _fault) is not null;

    public Exception? Fault => Volatile.Read(ref _fault);

    public int RejectedBatches => Volatile.Read(ref _rejected);

    public int UndeliverableBytes => Volatile.Read(ref _undeliverable);

    public bool TryEnqueue(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return true;
        if (_blocksForward?.Invoke() == true)
            return false;
        if (Volatile.Read(ref _fault) is not null)
            return false;
        if (!_retargetAdmission.Wait(0))
        {
            Interlocked.Increment(ref _rejected);
            return false;
        }

        try
        {
            var enqueueGeneration = Volatile.Read(ref _sendGeneration);
            var copy = bytes.ToArray();
            while (true)
            {
                var queued = Volatile.Read(ref _queuedBytes);
                if (queued + copy.Length > DefaultMaxQueuedBytes)
                {
                    Interlocked.Increment(ref _rejected);
                    return false;
                }

                if (Interlocked.CompareExchange(ref _queuedBytes, queued + copy.Length, queued) == queued)
                    break;
            }

            if (Volatile.Read(ref _sendGeneration) != enqueueGeneration)
            {
                Interlocked.Add(ref _queuedBytes, -copy.Length);
                Interlocked.Increment(ref _rejected);
                return false;
            }

            if (_channel.Writer.TryWrite(copy))
                return true;

            Interlocked.Add(ref _queuedBytes, -copy.Length);
            Interlocked.Increment(ref _rejected);
            return false;
        }
        finally
        {
            _retargetAdmission.Release();
        }
    }

    public void DiscardQueued()
    {
        _retargetAdmission.Wait();
        try
        {
            DrainQueuedInternal();
            Interlocked.Increment(ref _sendGeneration);
        }
        finally
        {
            _retargetAdmission.Release();
        }
    }

    /// <summary>Test seam: generation bump without drain for reverse-interleaving tests.</summary>
    internal void BumpGenerationForTests() =>
        Interlocked.Increment(ref _sendGeneration);

    /// <summary>
    /// Test seam: bump generation under admission without draining, matching the
    /// pre-fix retarget window between increment and drain.
    /// </summary>
    internal void IncrementGenerationUnderAdmissionForTests()
    {
        _retargetAdmission.Wait();
        try
        {
            Interlocked.Increment(ref _sendGeneration);
        }
        finally
        {
            _retargetAdmission.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already */ }
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* disconnect */ }
        catch (ObjectDisposedException) { /* client gone */ }
        catch (ControlPlaneException) { /* admission failed */ }
        _sendAdmission.Dispose();
        _retargetAdmission.Dispose();
        _cts.Dispose();
    }

    private void DrainQueuedInternal()
    {
        while (_channel.Reader.TryRead(out var next))
            Interlocked.Add(ref _queuedBytes, -next.Length);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await _sendAdmission.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var batchGeneration = Volatile.Read(ref _sendGeneration);
                    var batch = new List<byte>();
                    while (_channel.Reader.TryRead(out var next))
                    {
                        batch.AddRange(next);
                        Interlocked.Add(ref _queuedBytes, -next.Length);
                        if (batch.Count >= 4096)
                            break;
                    }

                    if (batch.Count == 0)
                        continue;

                    if (!CanDeliverBatch(batchGeneration))
                        continue;

                    if (!_retargetAdmission.Wait(0))
                        continue;

                    ControlPlaneClient client;
                    string paneId;
                    string leaseId;
                    try
                    {
                        var deliverGeneration = Volatile.Read(ref _sendGeneration);
                        if (deliverGeneration != batchGeneration)
                            continue;

                        (paneId, leaseId) = _target();
                        if (string.IsNullOrWhiteSpace(paneId))
                        {
                            Interlocked.Add(ref _undeliverable, batch.Count);
                            RaiseFault(new AttachInputFault(
                                AttachInputFaultKind.Undeliverable,
                                batch.Count,
                                "input target pane is empty"));
                            continue;
                        }

                        if (!CanDeliverBatch(batchGeneration)
                            || Volatile.Read(ref _sendGeneration) != deliverGeneration)
                        {
                            continue;
                        }

                        client = _client;
                        if (Volatile.Read(ref _sendGeneration) != deliverGeneration)
                            continue;
                    }
                    finally
                    {
                        _retargetAdmission.Release();
                    }

                    if (BlockNotifyForTests)
                    {
                        NotifyEnteredForTests.Release();
                        await NotifyHoldForTests.WaitAsync(ct).ConfigureAwait(false);
                    }

                    try
                    {
                        var body = new JsonObject
                        {
                            ["pane_id"] = paneId,
                            ["encoding"] = "base64",
                            ["data"] = Convert.ToBase64String(batch.ToArray()),
                        };
                        if (!string.IsNullOrWhiteSpace(leaseId))
                            body["lease_id"] = leaseId;
                        await client.NotifyAsync(
                                ProtocolMethods.PaneSendKeys,
                                body,
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception) when (!CanDeliverBatch(batchGeneration))
                    {
                    }
                }
                finally
                {
                    _sendAdmission.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RaiseFault(new AttachInputFault(AttachInputFaultKind.SenderFault, 0, ex.Message, ex));
        }
    }

    private bool CanDeliverBatch(long batchGeneration) =>
        _blocksForward?.Invoke() != true
        && Volatile.Read(ref _sendGeneration) == batchGeneration;

    private void RaiseFault(AttachInputFault fault)
    {
        Volatile.Write(ref _fault, fault);
        try { _onFault?.Invoke(fault); }
        catch
        {
            // Status callback must not kill the sender loop.
        }
    }
}

public enum AttachInputFaultKind
{
    Undeliverable = 0,
    AdmissionRejected = 1,
    SenderFault = 2,
}

public sealed class AttachInputFault : Exception
{
    public AttachInputFault(
        AttachInputFaultKind kind,
        int undeliverableBytes,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        UndeliverableBytes = undeliverableBytes;
    }

    public AttachInputFaultKind Kind { get; }

    public int UndeliverableBytes { get; }
}
