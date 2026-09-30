using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Production <see cref="IPtyProcess"/> adapter over hypa-pty-host.
/// One helper process equals one child session (P0). Does not call managed fork.
/// Always pumps helper Output while writing Input to avoid stdio deadlock.
/// same-host H2 handoff via <see cref="ExportHandoffAsync"/> / <see cref="AdoptHandoffAsync"/>.
/// </summary>
public sealed class PtyHostProcess : IPtyProcess, IPtyProcessControl
{
    public const int DefaultOutputChannelCapacity = ProcessPtyFallback.DefaultOutputChannelCapacity;

    private readonly PtyHostSupervisor _supervisor;
    private readonly ILogger _logger;
    private readonly Channel<byte[]> _channel;
    private readonly ChannelStream _output;
    private readonly InputFrameStream _input;
    private readonly CancellationTokenSource _pumpCts;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource _exitedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task _pumpTask = Task.CompletedTask;
    private int _childPid;
    private int? _exitCode;
    private int _disposed;
    private int _handedOff;
    /// <summary>
    /// Set on post-HandleMeta hard abandon (KillHelperOnly leave-alive). Adapter is
    /// terminal without claiming successful H2 (<see cref="WasHandedOff"/> stays false)
    /// and without inventing <see cref="ExitCode"/>. Distinct from dispose.
    /// </summary>
    private int _abandoned;
    /// <summary>0=Idle, 1=Exporting, 2=Committed. Failed exports reset to Idle for retry.</summary>
    private int _handoffState;
    /// <summary>
    /// When set, dispose must never terminate the child (Close terminate / process-group kill).
    /// Set on PauseOutput success (export in flight), successful H2 Commit, and
    /// ReleaseAdopted import rollback. Prevents dispose-during-export from murdering
    /// a session whose master FD may already have been duplicated or transferred.
    /// </summary>
    private int _skipChildTerminate;

    /// <summary>Post-Commit budget for CloseOldOwner + helper exit (decoupled from export timeout).</summary>
    internal static readonly TimeSpan PostCommitReleaseBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// After HandleMeta, wait this long for Commit or peer Abort before hard-abandon.
    /// Must cover a typical import Adopt (helper spawn + SCM_RIGHTS) so late Commit
    /// can still converge to single ownership. Independent of pre-HandleMeta timeout.
    /// </summary>
    internal static readonly TimeSpan PostHandleMetaAuthorityBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// After export Abort on post-HandleMeta timeout, wait this long for peer Abort
    /// (proves ReleaseAdopted) before KillHelperOnly abandon.
    /// </summary>
    internal static readonly TimeSpan PostHandleMetaPeerReleaseBudget = TimeSpan.FromSeconds(3);

    private PtyHostProcess(
        PtyHostSupervisor supervisor,
        int childPid,
        Channel<byte[]> channel,
        ChannelStream output,
        InputFrameStream input,
        CancellationTokenSource pumpCts,
        ILogger logger)
    {
        _supervisor = supervisor;
        _childPid = childPid;
        _channel = channel;
        _output = output;
        _input = input;
        _pumpCts = pumpCts;
        _logger = logger;
    }

    public int Pid => _childPid;
    /// <summary>
    /// False after child exit, dispose, successful H2 handoff, or hard-abandon leave-alive.
    /// Hard abandon does not set <see cref="ExitCode"/> or <see cref="WasHandedOff"/>.
    /// </summary>
    public bool IsRunning =>
        _exitCode is null && _disposed == 0 && _handedOff == 0 && _abandoned == 0;
    public int? ExitCode => _exitCode;
    /// <summary>True after a successful H2 export; child continues under the new owner.</summary>
    public bool WasHandedOff => _handedOff != 0;
    /// <summary>
    /// True after post-HandleMeta hard abandon: adapter dead, helper killed, child may live
    /// under a peer-held master. Not a successful handoff.
    /// </summary>
    public bool WasAbandoned => _abandoned != 0;
    public Stream StandardInput => _input;
    public Stream StandardOutput => _output;

    /// <summary>
    /// Starts helper, Hello, Spawn, awaits Spawned, then continuous Output pump.
    /// Blocks the calling thread (matches factory-side Spawn seam).
    /// </summary>
    public static PtyHostProcess Spawn(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        int cols,
        int rows,
        IReadOnlyDictionary<string, string>? env = null,
        PtyHostOptions? hostOptions = null,
        TimeSpan? spawnTimeout = null,
        ILogger? logger = null)
    {
        return SpawnAsync(
                fileName, args, cwd, cols, rows, env, hostOptions, spawnTimeout, logger)
            .GetAwaiter()
            .GetResult();
    }

    public static async Task<PtyHostProcess> SpawnAsync(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        int cols,
        int rows,
        IReadOnlyDictionary<string, string>? env = null,
        PtyHostOptions? hostOptions = null,
        TimeSpan? spawnTimeout = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("hypa-pty-host is Unix-only.");

        logger ??= NullLogger.Instance;
        hostOptions ??= new PtyHostOptions();
        var timeout = spawnTimeout ?? TimeSpan.FromSeconds(15);

        var argv = new List<string>(1 + args.Count) { fileName };
        argv.AddRange(args);

        var envMap = ChildEnvironmentBuilder.BuildHosted(env);
        var channel = CreateOutputChannel();
        var output = new ChannelStream(channel.Reader);

        PtyHostSupervisor? supervisor = null;
        try
        {
            supervisor = await PtyHostSupervisor.StartAsync(hostOptions, logger, ct)
                .ConfigureAwait(false);

            var spawnFrame = PtyHostFrameCodec.CreateSpawn(
                (ushort)Math.Clamp(cols, 1, 1000),
                (ushort)Math.Clamp(rows, 1, 1000),
                string.IsNullOrEmpty(cwd) ? "/" : cwd,
                argv,
                envMap);

            await supervisor.WriteFrameAsync(spawnFrame, ct).ConfigureAwait(false);

            int childPid;
            using (var spawnCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                spawnCts.CancelAfter(timeout);
                childPid = await AwaitSpawnedAsync(supervisor, spawnCts.Token).ConfigureAwait(false);
            }

            var pumpCts = new CancellationTokenSource();
            // Write callback is bound after construction via a holder (avoids self-ref in ctor).
            var writeHolder = new WriteHolder();
            var input = new InputFrameStream((data, token) => writeHolder.Write(data, token));

            var process = new PtyHostProcess(
                supervisor,
                childPid,
                channel,
                output,
                input,
                pumpCts,
                logger);
            writeHolder.Target = process;

            process._pumpTask = Task.Run(
                () => process.PumpFramesAsync(channel.Writer, pumpCts.Token),
                CancellationToken.None);

            supervisor = null; // ownership transferred
            return process;
        }
        catch
        {
            if (supervisor is not null)
            {
                try { await supervisor.DisposeAsync().ConfigureAwait(false); }
                catch { /* preserve spawn exception */ }
            }

            channel.Writer.TryComplete();
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> AwaitSpawnedAsync(PtyHostSupervisor supervisor, CancellationToken ct)
    {
        while (true)
        {
            var frame = await supervisor.ReadFrameAsync(ct).ConfigureAwait(false);
            switch (frame.Type)
            {
                case PtyHostFrameType.Spawned:
                    return PtyHostFrameCodec.ParseSpawned(frame.Payload);

                case PtyHostFrameType.Error:
                    throw new InvalidOperationException(
                        "hypa-pty-host Spawn failed: " + FormatError(frame.Payload));

                case PtyHostFrameType.Output:
                    // Spec: Spawned is sent immediately after spawn; tolerate rare pre-ack noise.
                    break;

                case PtyHostFrameType.Exit:
                    var (code, pid) = PtyHostFrameCodec.ParseExit(frame.Payload);
                    throw new InvalidOperationException(
                        $"hypa-pty-host child exited during spawn (code={code}, pid={pid}).");

                default:
                    throw new InvalidDataException(
                        $"Unexpected frame during Spawn wait: {frame.Type}");
            }
        }
    }

    private async Task PumpFramesAsync(ChannelWriter<byte[]> writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PtyHostFrame frame;
                try
                {
                    frame = await _supervisor.ReadFrameAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Supervisor disposed while pump was mid-read.
                    break;
                }

                switch (frame.Type)
                {
                    case PtyHostFrameType.Output:
                        if (frame.Payload.Length > 0)
                        {
                            // Prefer non-blocking TryWrite. If the channel is full and
                            // still open, await WriteAsync for correct backpressure
                            // (large-output fidelity). If the channel was completed
                            // (dispose), WriteAsync throws ChannelClosedException
                            // immediately — drop and keep the pump alive so helper
                            // stdout drains and Exit is observed.
                            if (!writer.TryWrite(frame.Payload))
                            {
                                try
                                {
                                    await writer.WriteAsync(frame.Payload, ct)
                                        .ConfigureAwait(false);
                                }
                                catch (ChannelClosedException)
                                {
                                    // Drop; continue reading frames.
                                }
                                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                                {
                                    break;
                                }
                            }
                        }

                        break;

                    case PtyHostFrameType.Exit:
                        {
                            // After H2 handoff / hard abandon the old helper exits without
                            // meaning the OS child died under this adapter.
                            if (Volatile.Read(ref _handedOff) != 0
                                || Volatile.Read(ref _abandoned) != 0)
                                return;

                            var (code, pid) = PtyHostFrameCodec.ParseExit(frame.Payload);
                            OnChildExit(code, pid);
                            return;
                        }

                    case PtyHostFrameType.Error:
                    case PtyHostFrameType.Spawned:
                    case PtyHostFrameType.Adopted:
                        break;

                    default:
                        break;
                }
            }
        }
        finally
        {
            writer.TryComplete();
            var leaveAlive =
                Volatile.Read(ref _handedOff) != 0
                || Volatile.Read(ref _abandoned) != 0
                || Volatile.Read(ref _skipChildTerminate) != 0;
            if (_exitCode is null && !leaveAlive)
            {
                // Helper died without Exit frame and child was still ours to terminate.
                _exitCode = -1;
                _exitedTcs.TrySetResult();
            }
            else if (leaveAlive)
            {
                // Handoff / abandon / ReleaseAdopted: complete wait without inventing
                // child death — OS process may still live under a peer.
                _exitedTcs.TrySetResult();
            }
        }
    }

    private void OnChildExit(int exitCode, int pid)
    {
        if (pid > 0)
            _childPid = pid;
        _exitCode = exitCode;
        _exitedTcs.TrySetResult();
    }

    private async Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (data.Length == 0)
            return;

        var offset = 0;
        while (offset < data.Length)
        {
            var take = Math.Min(PtyHostFrameCodec.MaxChunkPayload, data.Length - offset);
            var chunk = data.Slice(offset, take);
            var frame = PtyHostFrameCodec.CreateInput(chunk.Span);
            await WriteFrameLockedAsync(frame, ct).ConfigureAwait(false);
            offset += take;
        }
    }

    private async Task WriteFrameLockedAsync(PtyHostFrame frame, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            await _supervisor.WriteFrameAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed != 0 || _exitCode is not null)
            return;

        var frame = PtyHostFrameCodec.CreateResize(
            (ushort)Math.Clamp(cols, 1, 1000),
            (ushort)Math.Clamp(rows, 1, 1000));

        _ = ResizeAsync(frame);
    }

    private async Task ResizeAsync(PtyHostFrame frame)
    {
        try
        {
            await WriteFrameLockedAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resize frame to hypa-pty-host failed");
        }
    }

    public ValueTask SendSignalAsync(int signal, CancellationToken ct = default)
    {
        if (_disposed != 0 || _handedOff != 0)
            return ValueTask.CompletedTask;

        return SendSignalCoreAsync(signal, ct);
    }

    private async ValueTask SendSignalCoreAsync(int signal, CancellationToken ct)
    {
        try
        {
            await WriteFrameLockedAsync(PtyHostFrameCodec.CreateSignal(signal), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Signal frame to hypa-pty-host failed (sig={Sig})", signal);
        }
    }

    /// <summary>
    /// old owner: Hello → Prepare → PauseOutput + FD → HandleMeta → Commit → CloseOldOwner.
    /// On any failure before Commit the old owner remains authoritative (no half-claimed H2).
    /// After HandleMeta the master FD has left this process: do not ResumeHandoff unless the
    /// peer Abort proves ReleaseAdopted. Failed pre-HandleMeta exports reset to Idle so retry
    /// is possible. Post-HandleMeta failures do not return until Commit, peer Abort, or hard
    /// abandon (KillHelperOnly) restores sole ownership — never leave split-brain open.
    /// Once Commit is accepted, ownership is transferred and export returns Ok even if
    /// CloseOldOwner helper cleanup is slow (child remains protected; abandon-only dispose).
    /// </summary>
    public async ValueTask<PtyHandoffResult> ExportHandoffAsync(
        IPtyHandoffPort handoff,
        PtyHandoffExportOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        options ??= new PtyHandoffExportOptions();

        if (OperatingSystem.IsWindows())
            return PtyHandoffResult.PlatformUnsupported();
        if (_disposed != 0)
            return PtyHandoffResult.Failed("Process is disposed.");
        if (_handedOff != 0)
            return PtyHandoffResult.Failed("Already handed off.");
        if (_exitCode is not null)
            return PtyHandoffResult.Failed("Child already exited.");
        if (Interlocked.CompareExchange(ref _handoffState, 1, 0) != 0)
            return PtyHandoffResult.Failed("Handoff already in progress.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);
        var token = timeoutCts.Token;

        byte[] nonce;
        if (options.Nonce is { Length: PtyHandoffMessage.NonceLength } provided)
            nonce = provided.ToArray();
        else
        {
            nonce = new byte[PtyHandoffMessage.NonceLength];
            RandomNumberGenerator.Fill(nonce);
        }

        var generation = options.Generation;
        PtyHandoffHandle? localMaster = null;
        UnixFdPassServer? fdServer = null;
        var pauseStarted = false;
        var handleMetaSent = false;
        var commitAccepted = false;
        // After HandleMeta without peer release: do not reset Idle (retry would dual-own).
        var authorityAmbiguous = false;

        try
        {
            // 1) Hello on handoff port
            await handoff.SendAsync(
                    PtyHandoffMessage.CreateHello(
                        options.RuntimeSessionId,
                        options.PaneId,
                        generation,
                        nonce),
                    token)
                .ConfigureAwait(false);

            // 2) Await Prepare; verify nonce (Abort maps to fail-closed capability result)
            var prepare = await handoff.ReceiveAsync(token).ConfigureAwait(false);
            if (prepare.Kind == PtyHandoffMessageKind.Abort)
                return FailExport(MapPeerAbort(prepare));
            if (prepare.Kind != PtyHandoffMessageKind.Prepare)
                return FailExport(PtyHandoffResult.Protocol($"Expected Prepare, got {prepare.Kind}."));
            if (!NonceEquals(prepare.Nonce, nonce))
                return FailExport(PtyHandoffResult.NonceMismatch());

            // 3) PauseOutput: helper sendmsg → managed recv
            fdServer = UnixFdPassServer.Create();
            var pauseFrame = PtyHostFrameCodec.CreatePauseOutput(nonce, generation, fdServer.Path);
            await WriteFrameLockedAsync(pauseFrame, token).ConfigureAwait(false);
            pauseStarted = true;
            // Dispose during export after Pause must never Close/tree-kill the child:
            // master is duplicated and may already be en route to a peer.
            Volatile.Write(ref _skipChildTerminate, 1);

            using (var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                acceptCts.CancelAfter(TimeSpan.FromSeconds(3));
                var safe = await fdServer.AcceptAndRecvFdAsync(acceptCts.Token).ConfigureAwait(false);
                localMaster = new PtyHandoffHandle(safe);
            }

            await fdServer.DisposeAsync().ConfigureAwait(false);
            fdServer = null;

            // 4) HandleMeta + SCM_RIGHTS to new owner
            var meta = PtyHandoffMessage.CreateHandleMeta(
                nonce,
                generation,
                _childPid,
                options.Cols,
                options.Rows);
            await handoff.SendHandleAsync(meta, localMaster, token).ConfigureAwait(false);
            // FD has left this process. Ownership is ambiguous until Commit or peer Abort.
            handleMetaSent = true;

            // 5) Await Commit — post-HandleMeta wait uses authority budget (not only
            //    remaining negotiation timeout) so a late import Commit can converge.
            PtyHandoffMessage commit;
            try
            {
                commit = await handoff.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Negotiation timeout while waiting for Commit: resolve authority below.
                return await ResolvePostHandleMetaAuthorityAsync(
                        handoff, nonce, PtyHandoffResult.Timeout())
                    .ConfigureAwait(false);
            }

            if (commit.Kind == PtyHandoffMessageKind.Abort)
            {
                // Peer Abort after HandleMeta proves import rolled back (ReleaseAdopted).
                return FailExport(MapPeerAbort(commit), peerReleased: true);
            }

            // Wrong kind or nonce after HandleMeta: do not return with authority ambiguous.
            // Converge via late Commit, peer Abort+Resume, or hard abandon (same as timeout).
            if (commit.Kind != PtyHandoffMessageKind.Commit)
            {
                return await ResolvePostHandleMetaAuthorityAsync(
                        handoff,
                        nonce,
                        PtyHandoffResult.Protocol($"Expected Commit, got {commit.Kind}."))
                    .ConfigureAwait(false);
            }

            if (!NonceEquals(commit.Nonce, nonce))
            {
                return await ResolvePostHandleMetaAuthorityAsync(
                        handoff,
                        nonce,
                        PtyHandoffResult.NonceMismatch("Commit nonce mismatch."))
                    .ConfigureAwait(false);
            }

            return await AcceptCommitAndCloseOldOwnerAsync(nonce, generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await ResolvePostHandleMetaAuthorityAsync(
                    handoff, nonce, PtyHandoffResult.Failed("Export cancelled."))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await ResolvePostHandleMetaAuthorityAsync(
                    handoff, nonce, PtyHandoffResult.Timeout())
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExportHandoffAsync failed");
            return await ResolvePostHandleMetaAuthorityAsync(
                    handoff, nonce, PtyHandoffResult.Failed(ex.Message))
                .ConfigureAwait(false);
        }
        finally
        {
            localMaster?.Dispose();
            if (fdServer is not null)
            {
                try { await fdServer.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            // Fail-closed path that returned without FailExport: reset Idle for retry
            // only when the master FD never left this process.
            if (!authorityAmbiguous
                && Volatile.Read(ref _handedOff) == 0
                && Volatile.Read(ref _handoffState) == 1)
            {
                Volatile.Write(ref _handoffState, 0);
            }
        }

        async ValueTask<PtyHandoffResult> AcceptCommitAndCloseOldOwnerAsync(
            byte[] exportNonce,
            int exportGeneration)
        {
            commitAccepted = true;

            // Authority transferred. Protect child; abandon-only dispose from here.
            Volatile.Write(ref _skipChildTerminate, 1);
            Volatile.Write(ref _handedOff, 1);
            Volatile.Write(ref _handoffState, 2);
            _supervisor.RequestAbandonDispose();

            // CloseOldOwner — independent of export negotiation timeout.
            // After Commit, export is Ok even if helper cleanup is slow.
            using var releaseCts = new CancellationTokenSource(PostCommitReleaseBudget);
            try
            {
                await WriteFrameLockedAsync(
                        PtyHostFrameCodec.CreateCloseOldOwner(exportNonce, exportGeneration),
                        releaseCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CloseOldOwner frame write failed after Commit");
                _supervisor.KillHelperOnly();
            }

            if (!await _supervisor.WaitForExitAsync(PostCommitReleaseBudget, CancellationToken.None)
                    .ConfigureAwait(false))
            {
                _logger.LogDebug("Old helper still alive after CloseOldOwner; KillHelperOnly");
                _supervisor.KillHelperOnly();
                await _supervisor.WaitForExitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            _exitedTcs.TrySetResult();
            return PtyHandoffResult.Ok();
        }

        /// <summary>
        /// Post-HandleMeta: do not return a terminal failure until Commit (Ok + CloseOldOwner),
        /// peer Abort proving ReleaseAdopted (Resume + Idle), or hard abandon (KillHelperOnly)
        /// so the old helper cannot dual-pump after safety expiry.
        /// </summary>
        async ValueTask<PtyHandoffResult> ResolvePostHandleMetaAuthorityAsync(
            IPtyHandoffPort port,
            byte[] exportNonce,
            PtyHandoffResult result)
        {
            if (!handleMetaSent || commitAccepted)
                return FailExport(result);

            // Phase 1: wait for late Commit or peer Abort (import may still be Adopting).
            try
            {
                using var authority = new CancellationTokenSource(PostHandleMetaAuthorityBudget);
                var late = await port.ReceiveAsync(authority.Token).ConfigureAwait(false);
                if (late.Kind == PtyHandoffMessageKind.Abort)
                    return FailExport(MapPeerAbort(late), peerReleased: true);
                if (late.Kind == PtyHandoffMessageKind.Commit && NonceEquals(late.Nonce, exportNonce))
                    return await AcceptCommitAndCloseOldOwnerAsync(exportNonce, generation)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // No Commit/Abort within authority budget.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Post-HandleMeta authority wait failed");
            }

            // Phase 2: signal importer to ReleaseAdopted; wait for peer Abort confirmation.
            TrySendAbort(result);
            try
            {
                using var peerWait = new CancellationTokenSource(PostHandleMetaPeerReleaseBudget);
                var peer = await port.ReceiveAsync(peerWait.Token).ConfigureAwait(false);
                if (peer.Kind == PtyHandoffMessageKind.Abort)
                    return FailExport(MapPeerAbort(peer), peerReleased: true);
                if (peer.Kind == PtyHandoffMessageKind.Commit && NonceEquals(peer.Nonce, exportNonce))
                    return await AcceptCommitAndCloseOldOwnerAsync(exportNonce, generation)
                        .ConfigureAwait(false);
            }
            catch
            {
                // Peer did not confirm release or Commit.
            }

            // Phase 3: hard abandon — drop old helper master without Resume (no dual-pump).
            // Native safety expiry also abandons master; this is the managed fast path.
            return FailExport(result, peerReleased: false, hardAbandon: true);
        }

        PtyHandoffResult FailExport(
            PtyHandoffResult result,
            bool peerReleased = false,
            bool hardAbandon = false)
        {
            // After Commit the new owner adopted — keep child protection and terminal state.
            // Ownership already transferred; never return Failed for cleanup issues here.
            if (commitAccepted || Volatile.Read(ref _handedOff) != 0)
            {
                Volatile.Write(ref _skipChildTerminate, 1);
                Volatile.Write(ref _handedOff, 1);
                Volatile.Write(ref _handoffState, 2);
                _supervisor.RequestAbandonDispose();
                _exitedTcs.TrySetResult();
                return PtyHandoffResult.Ok();
            }

            if (handleMetaSent && !peerReleased)
            {
                // Master FD left this process. Do NOT ResumeHandoff (would dual-own).
                authorityAmbiguous = true;
                Volatile.Write(ref _skipChildTerminate, 1);
                Volatile.Write(ref _handoffState, 1);
                _supervisor.RequestAbandonDispose();

                if (hardAbandon)
                {
                    // Force sole ownership from old side: kill helper only (close master),
                    // never process-group kill child. Peer must ReleaseAdopted on Abort;
                    // if peer already Adopted and keeps pumping, it is the sole master holder
                    // after our helper dies — still single pump.
                    // Terminal adapter state without claiming H2 or inventing ExitCode:
                    // IsRunning false, WaitForExit complete, WasHandedOff false, ExitCode null.
                    try
                    {
                        _supervisor.KillHelperOnly();
                    }
                    catch
                    {
                        // best-effort
                    }

                    // Block retry (ambiguous export cannot be safely re-exported).
                    Volatile.Write(ref _handoffState, 1);
                    Volatile.Write(ref _abandoned, 1);
                    _exitedTcs.TrySetResult();
                }
                else
                {
                    TrySendAbort(result);
                }

                return result;
            }

            // Sole-authority restore: pre-HandleMeta failure, or peer Abort after ReleaseAdopted.
            Volatile.Write(ref _handoffState, 0);
            if (pauseStarted)
            {
                TryResumeHandoff();
                // Master is ours again — dispose may kill normally.
                Volatile.Write(ref _skipChildTerminate, 0);
            }

            TrySendAbort(result);
            return result;
        }

        void TrySendAbort(PtyHandoffResult result)
        {
            try
            {
                handoff.SendAsync(
                        PtyHandoffMessage.CreateAbort((int)result.Status, result.Message),
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // best-effort abort to peer
            }
        }

        void TryResumeHandoff()
        {
            try
            {
                WriteFrameLockedAsync(PtyHostFrameCodec.CreateResumeHandoff(), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ResumeHandoff after failed export");
            }
        }
    }

    /// <summary>
    /// new owner: start helper (Hello only), Adopt + SCM_RIGHTS, await Adopted, pump.
    /// </summary>
    public static async Task<PtyHostProcess> AdoptHandoffAsync(
        PtyHandoffHandle handle,
        PtyHandoffAdoptOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("hypa-pty-host adopt is Unix-only.");
        if (options.Nonce is not { Length: PtyHandoffMessage.NonceLength })
            throw new ArgumentException("Nonce must be 16 bytes.", nameof(options));
        if (options.ChildPid <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ChildPid must be positive.");

        logger ??= NullLogger.Instance;
        var hostOptions = options.HostOptions ?? new PtyHostOptions();
        if (options.HelperPath is not null)
            hostOptions = hostOptions with { HelperPath = options.HelperPath };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);
        var token = timeoutCts.Token;

        var channel = CreateOutputChannel();
        var output = new ChannelStream(channel.Reader);
        PtyHostSupervisor? supervisor = null;
        UnixFdPassServer? fdServer = null;
        var masterTransferred = false;

        try
        {
            supervisor = await PtyHostSupervisor.StartAsync(hostOptions, logger, token)
                .ConfigureAwait(false);

            fdServer = UnixFdPassServer.Create();
            var adoptFrame = PtyHostFrameCodec.CreateAdopt(
                options.Nonce,
                options.Generation,
                options.ChildPid,
                fdServer.Path);

            // Accept helper connection + send FD concurrently with Adopt frame.
            var sendTask = fdServer.AcceptAndSendFdAsync(
                handle.DangerousGetFileDescriptor(),
                token);
            await supervisor.WriteFrameAsync(adoptFrame, token).ConfigureAwait(false);
            await sendTask.ConfigureAwait(false);
            masterTransferred = true;

            await fdServer.DisposeAsync().ConfigureAwait(false);
            fdServer = null;

            // Helper now owns the master; release managed copy.
            handle.Dispose();

            // Await Adopted (may see Error)
            while (true)
            {
                var frame = await supervisor.ReadFrameAsync(token).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case PtyHostFrameType.Adopted:
                        {
                            var (n, gen, pid) = PtyHostFrameCodec.ParseAdopted(frame.Payload);
                            if (!NonceEquals(n, options.Nonce))
                                throw new InvalidOperationException("Adopted nonce mismatch.");
                            if (gen != options.Generation)
                                throw new InvalidOperationException("Adopted generation mismatch.");
                            if (pid != options.ChildPid)
                                throw new InvalidOperationException("Adopted child_pid mismatch.");

                            var pumpCts = new CancellationTokenSource();
                            var writeHolder = new WriteHolder();
                            var input = new InputFrameStream((data, t) => writeHolder.Write(data, t));
                            var process = new PtyHostProcess(
                                supervisor,
                                options.ChildPid,
                                channel,
                                output,
                                input,
                                pumpCts,
                                logger);
                            writeHolder.Target = process;
                            process._pumpTask = Task.Run(
                                () => process.PumpFramesAsync(channel.Writer, pumpCts.Token),
                                CancellationToken.None);
                            supervisor = null;
                            return process;
                        }
                    case PtyHostFrameType.Error:
                        throw new InvalidOperationException(
                            "hypa-pty-host Adopt failed: " + FormatError(frame.Payload));
                    case PtyHostFrameType.Output:
                        break;
                    default:
                        throw new InvalidDataException($"Unexpected frame during Adopt: {frame.Type}");
                }
            }
        }
        catch
        {
            if (fdServer is not null)
            {
                try { await fdServer.DisposeAsync().ConfigureAwait(false); }
                catch { /* preserve */ }
            }

            if (supervisor is not null)
            {
                // After master FD transfer, never Close/Kill(tree) — that kills the live child.
                if (masterTransferred)
                {
                    try
                    {
                        await supervisor.WriteFrameAsync(
                                PtyHostFrameCodec.CreateReleaseAdopted(),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        await supervisor.WaitForExitAsync(PostCommitReleaseBudget, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        supervisor.KillHelperOnly();
                        try
                        {
                            await supervisor.WaitForExitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch { /* preserve */ }
                    }

                    supervisor.RequestAbandonDispose();
                }

                try { await supervisor.DisposeAsync().ConfigureAwait(false); }
                catch { /* preserve */ }
            }

            channel.Writer.TryComplete();
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Importer convenience: receive Hello/HandleMeta from port, adopt, Commit.
    /// When <see cref="PtyHandoffAdoptOptions.ExpectedGeneration"/> is set, Hello
    /// generation mismatch fails closed before Prepare/Adopt.
    /// After HandleMeta, multiplexes receive for export Abort while Adopt/Commit runs;
    /// on Abort, ReleaseAdopted and fail closed (old remains authoritative).
    /// After successful Adopt, post-commit failures release the master without killing the child.
    /// </summary>
    public static async Task<(PtyHostProcess Process, PtyHandoffMessage Hello)> ImportHandoffAsync(
        IPtyHandoffPort handoff,
        PtyHandoffAdoptOptions? baseOptions = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        baseOptions ??= new PtyHandoffAdoptOptions { Nonce = new byte[16], ChildPid = 1 };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(baseOptions.Timeout);
        var token = timeoutCts.Token;

        var hello = await handoff.ReceiveAsync(token).ConfigureAwait(false);
        if (hello.Kind != PtyHandoffMessageKind.Hello)
            throw new InvalidDataException($"Expected Hello, got {hello.Kind}.");

        if (baseOptions.ExpectedGeneration is { } expectedGen
            && hello.Generation != expectedGen)
        {
            try
            {
                await handoff.SendAsync(
                        PtyHandoffMessage.CreateAbort(
                            (int)PtyHandoffStatus.GenerationMismatch,
                            $"generation mismatch: expected {expectedGen}, got {hello.Generation}"),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }

            throw new PtyHandoffException(
                PtyHandoffResult.GenerationMismatch(
                    $"generation mismatch: expected {expectedGen}, got {hello.Generation}"));
        }

        await handoff.SendAsync(
                PtyHandoffMessage.CreatePrepare(hello.Nonce, baseOptions.TargetRuntimeId),
                token)
            .ConfigureAwait(false);

        var meta = await handoff.ReceiveAsync(token).ConfigureAwait(false);
        if (meta.Kind == PtyHandoffMessageKind.Abort)
            throw new InvalidOperationException(meta.AbortMessage ?? "Export aborted.");
        if (meta.Kind != PtyHandoffMessageKind.HandleMeta)
            throw new InvalidDataException($"Expected HandleMeta, got {meta.Kind}.");
        if (!NonceEquals(meta.Nonce, hello.Nonce))
            throw new InvalidOperationException("HandleMeta nonce mismatch.");
        if (meta.Generation != hello.Generation)
            throw new InvalidOperationException("HandleMeta generation mismatch.");

        PtyHandoffHandle? handle = await handoff.ReceiveHandleAsync(token).ConfigureAwait(false);
        var adoptOptions = baseOptions with
        {
            Nonce = hello.Nonce,
            Generation = hello.Generation,
            ChildPid = meta.ChildPid,
            Cols = meta.Cols == 0 ? baseOptions.Cols : meta.Cols,
            Rows = meta.Rows == 0 ? baseOptions.Rows : meta.Rows,
        };

        PtyHostProcess? process = null;
        var adopted = false;
        // Watch for export Abort while Adopt/Commit runs (full-duplex port). Export
        // timeout after HandleMeta must cancel Commit and ReleaseAdopted — never leave
        // import success without CloseOldOwner (authority split-brain).
        using var abortWatchCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var commitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        PtyHandoffMessage? peerAbort = null;
        var abortWatch = Task.Run(async () =>
        {
            try
            {
                var msg = await handoff.ReceiveAsync(abortWatchCts.Token).ConfigureAwait(false);
                if (msg.Kind == PtyHandoffMessageKind.Abort)
                {
                    peerAbort = msg;
                    try { commitCts.Cancel(); }
                    catch { /* already disposed/cancelled */ }
                }
            }
            catch
            {
                // cancelled or port closed — ignore
            }
        }, CancellationToken.None);

        try
        {
            process = await AdoptHandoffAsync(handle, adoptOptions, logger, token)
                .ConfigureAwait(false);
            adopted = true;
            // AdoptHandoffAsync disposed the handle into the helper.
            handle = null;

            if (peerAbort is not null)
            {
                throw new PtyHandoffException(
                    MapPeerAbort(peerAbort));
            }

            // Commit under commitCts: if export Abort arrives mid-stall, cancel so Commit
            // is never written after Abort (export then resumes after peer Abort).
            await handoff.SendAsync(PtyHandoffMessage.CreateCommit(hello.Nonce), commitCts.Token)
                .ConfigureAwait(false);

            // Commit reached the wire. Stop Abort watch. A late Abort after Commit is
            // ignored — export either already accepted Commit or will accept it in grace.
            await abortWatchCts.CancelAsync().ConfigureAwait(false);
            try { await abortWatch.ConfigureAwait(false); }
            catch { /* ignore */ }

            return (process, hello);
        }
        catch
        {
            await abortWatchCts.CancelAsync().ConfigureAwait(false);
            try { await abortWatch.ConfigureAwait(false); }
            catch { /* ignore */ }

            // Fail-closed: never Close/terminate the child after Adopt.
            // Old owner remains authoritative with a living session.
            if (adopted && process is not null)
            {
                try { await process.ReleaseAdoptedSessionAsync().ConfigureAwait(false); }
                catch { /* preserve original */ }
            }
            else
            {
                try { handle?.Dispose(); }
                catch { /* preserve */ }
                if (process is not null)
                {
                    try { await process.DisposeAsync().ConfigureAwait(false); }
                    catch { /* preserve */ }
                }
            }

            try
            {
                // Failed (7), never CapabilityAbsent (1): mid-protocol import failures are not
                // missing H2 capability. Callers that branch on CapabilityAbsent must not stop
                // retrying H2 when the old owner remains authoritative and capable.
                await handoff.SendAsync(
                        PtyHandoffMessage.CreateAbort(
                            (int)PtyHandoffStatus.Failed,
                            peerAbort?.AbortMessage ?? "import failed"),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }

            throw;
        }
    }

    /// <summary>
    /// After a successful Adopt, release the master FD without terminating the child.
    /// Used when Commit (or later import steps) fail — fail-closed leave-alive path.
    /// </summary>
    public async Task ReleaseAdoptedSessionAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _skipChildTerminate, 1) == 0)
        {
            // first transition to no-terminate
        }

        _supervisor.RequestAbandonDispose();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PostCommitReleaseBudget);
            await WriteFrameLockedAsync(PtyHostFrameCodec.CreateReleaseAdopted(), cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReleaseAdopted frame failed; KillHelperOnly");
            _supervisor.KillHelperOnly();
        }

        if (!await _supervisor.WaitForExitAsync(PostCommitReleaseBudget, CancellationToken.None)
                .ConfigureAwait(false))
        {
            _supervisor.KillHelperOnly();
            await _supervisor.WaitForExitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
        }

        // Tear down managed side without child kill (DisposeAsync honors _skipChildTerminate).
        await DisposeAsync().ConfigureAwait(false);
    }

    private static bool NonceEquals(byte[] a, byte[] b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    private static PtyHandoffResult MapPeerAbort(PtyHandoffMessage abort)
    {
        var msg = abort.AbortMessage ?? "Peer aborted.";
        return (PtyHandoffStatus)abort.AbortReason switch
        {
            PtyHandoffStatus.NonceMismatch => PtyHandoffResult.NonceMismatch(msg),
            PtyHandoffStatus.GenerationMismatch => PtyHandoffResult.GenerationMismatch(msg),
            PtyHandoffStatus.Timeout => PtyHandoffResult.Timeout(msg),
            PtyHandoffStatus.Protocol => PtyHandoffResult.Protocol(msg),
            PtyHandoffStatus.CapabilityAbsent => PtyHandoffResult.CapabilityAbsent(msg),
            PtyHandoffStatus.PlatformUnsupported => PtyHandoffResult.PlatformUnsupported(msg),
            _ => PtyHandoffResult.Failed(msg),
        };
    }

    public Task WaitForExitAsync(CancellationToken ct)
    {
        if (_exitCode is not null || _handedOff != 0 || _abandoned != 0)
            return Task.CompletedTask;

        return _exitedTcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Managed Close/Exit wait must exceed native terminate grace (2s) so the
    /// helper can finish SIGTERM → grace → group SIGKILL and emit Exit.
    /// </summary>
    internal static readonly TimeSpan CloseExitWait = TimeSpan.FromSeconds(5);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Snapshot acked child pid for managed process-group kill fallback.
        // Do not use after hard kill for leader-only signals (PID reuse).
        var childPid = Volatile.Read(ref _childPid);
        var skipChildTerminate =
            Volatile.Read(ref _handedOff) != 0
            || Volatile.Read(ref _abandoned) != 0
            || Volatile.Read(ref _skipChildTerminate) != 0;

        try
        {
            // 0) Complete the consumer channel first. PaneRuntime cancels its
            //    StandardOutput reader before DisposeAsync; a full bounded
            //    channel would otherwise stall PumpFramesAsync on WriteAsync,
            //    which stalls helper stdout, which stalls native Close handling.
            //    Completing unblocks the pump so it can drop Output, keep
            //    draining helper frames, and still deliver Exit.
            _channel.Writer.TryComplete();

            if (!skipChildTerminate)
            {
                // 1) While the pump still runs: send Close and wait for Exit.
                //    Cancelling the pump first drops the Exit frame and races a
                //    synthetic ExitCode=-1 while the OS child is still alive.
                if (_exitCode is null)
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(CloseExitWait);
                        await _writeGate.WaitAsync(closeCts.Token).ConfigureAwait(false);
                        try
                        {
                            await _supervisor.SendCloseAsync(closeCts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            _writeGate.Release();
                        }

                        try
                        {
                            await _exitedTcs.Task.WaitAsync(closeCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Grace expired — fall through to kill helper / process group.
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Close frame during PtyHostProcess dispose");
                    }
                }
            }
            else
            {
                // Handoff / ReleaseAdopted: ensure supervisor never Close/Kill(tree).
                _supervisor.RequestAbandonDispose();
            }

            // 2) Stop the output pump only after Close/Exit observation (or timeout).
            //    Do not depend on consumer drain for liveness after this point.
            _pumpCts.Cancel();

            // 3) Dispose supervisor.
            //    After handoff: abandon only (no Close, no entireProcessTree).
            //    Normal: Close + kill helper process tree.
            try
            {
                await _supervisor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Supervisor dispose during PtyHostProcess dispose");
            }

            // 4) Managed process-group kill fallback — SKIPPED after H2 handoff
            //    or ReleaseAdopted. Killing here would murder the live session.
            if (!skipChildTerminate
                && childPid > 0
                && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            {
                try
                {
                    UnixProcessGroupSignals.SignalGroup(
                        childPid, UnixProcessGroupSignals.SIGTERM, fallbackToLeader: false);
                    UnixProcessGroupSignals.SignalGroup(
                        childPid, UnixProcessGroupSignals.SIGKILL, fallbackToLeader: false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Managed process-group kill fallback failed (pid={Pid})", childPid);
                }
            }

            // 5) Synthetic ExitCode only after teardown if no Exit frame and child was ours.
            if (_exitCode is null && !skipChildTerminate)
            {
                _exitCode = -1;
                _exitedTcs.TrySetResult();
            }
            else if (skipChildTerminate)
            {
                _exitedTcs.TrySetResult();
            }

            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Output pump ended during PtyHostProcess disposal");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing PtyHostProcess");
        }
        finally
        {
            await _output.DisposeAsync().ConfigureAwait(false);
            await _input.DisposeAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
            _writeGate.Dispose();
        }
    }

    internal static Channel<byte[]> CreateOutputChannel(int capacity = DefaultOutputChannelCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        return Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    private static string FormatError(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            return "unknown error";

        var code = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);
        var msgLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        var take = (int)Math.Min(msgLen, (uint)Math.Max(0, payload.Length - 8));
        var msg = take > 0
            ? System.Text.Encoding.UTF8.GetString(payload.Slice(8, take))
            : string.Empty;
        return $"code={code} {msg}".TrimEnd();
    }

    private sealed class WriteHolder
    {
        public PtyHostProcess? Target;

        public Task Write(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var target = Target
                ?? throw new InvalidOperationException("PTY host process not ready.");
            return target.WriteInputAsync(data, ct);
        }
    }

    /// <summary>Write-only stream that emits Input frames (≤ 64 KiB each).</summary>
    private sealed class InputFrameStream : Stream
    {
        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _write;
        private bool _disposed;

        public InputFrameStream(Func<ReadOnlyMemory<byte>, CancellationToken, Task> write) =>
            _write = write;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _write(buffer, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Read-only stream over bounded output channel (mirrors ProcessPtyFallback).</summary>
    private sealed class ChannelStream : Stream
    {
        private readonly ChannelReader<byte[]> _reader;
        private byte[]? _current;
        private int _offset;
        private bool _disposed;

        public ChannelStream(ChannelReader<byte[]> reader) => _reader = reader;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            while (true)
            {
                if (_current is not null && _offset < _current.Length)
                {
                    var n = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsSpan(_offset, n).CopyTo(buffer.Span);
                    _offset += n;
                    if (_offset >= _current.Length)
                    {
                        _current = null;
                        _offset = 0;
                    }

                    return n;
                }

                if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;

                if (!_reader.TryRead(out _current))
                    continue;
                _offset = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
