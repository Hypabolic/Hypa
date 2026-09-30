using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal const string PopupNotOpenMessage = "popup_not_open";
    internal const string PopupUiBusyMessage = "ui_busy";
    internal const string PopupRuntimeKey = "popup";

    private static readonly PaneId PopupInternalId = new(PopupRuntimeKey);

    private readonly SemaphoreSlim _popupGate = new(1, 1);
    /// <summary>
    /// Test hook: after the pre-gate ui_busy check, before <c>_popupGate</c>.
    /// </summary>
    internal Func<Task>? AfterPopupOpenBusyCheckAsync { get; set; }
    private PopupSession? _popup;
    /// <summary>Assigned popup generation. Second <c>popup.open</c> stays <c>ui_busy</c>.</summary>
    private long _popupGeneration;
    /// <summary>
    /// Thread that is inside <c>StartAsync</c> on the open path. Synchronous
    /// <c>Exited</c> from that call must not publish <c>opened</c> after teardown.
    /// Wait-loop exit is another thread and still emits opened then closed.
    /// </summary>
    private int _popupStartThreadId;
    private long _popupExitDuringStartGeneration;
    private readonly Dictionary<string, string> _attachClientModes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publishedAttachClientModes = new(StringComparer.Ordinal);

    internal Task<JsonElement> HandlePopupCloseAsync(
        EmptyParams _,
        IClientConnection? connection,
        CancellationToken ct) =>
        PopupCloseAsync(connection, ct);

    internal Task<JsonElement> HandlePopupOpenAsync(
        PopupOpenParams p, IClientConnection? connection, CancellationToken ct) =>
        PopupOpenAsync(p, connection, ct);

    internal Task<JsonElement> HandlePopupSendKeysAsync(
        PopupSendKeysParams p, IClientConnection? connection, CancellationToken ct) =>
        PopupSendKeysAsync(p, connection, ct);

    internal Task<JsonElement> HandlePopupResizeAsync(
        PopupResizeParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        PopupResizeAsync(p, connection, ct);

    internal bool PopupIsOpen
    {
        get
        {
            lock (_gate)
                return _popup is not null;
        }
    }

    internal IPaneRuntime? PeekPopupRuntime()
    {
        lock (_gate)
            return _popup?.Runtime;
    }

    private async Task<JsonElement> PopupOpenAsync(
        PopupOpenParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (IsShuttingDown)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.ServerShuttingDown,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));
        }

        EnsureNotFrozenForMutation("popup.open");
        var command = RequireField(p.Command, "command");
        if (PopupUiBusy(p.ClientMode, connection))
            throw UiBusy();

        var afterBusy = AfterPopupOpenBusyCheckAsync;
        if (afterBusy is not null)
            await afterBusy().ConfigureAwait(false);

        var areaCols = p.AreaCols is > 0 ? p.AreaCols.Value : VtFloorDefaults.DefaultCols;
        var areaRows = p.AreaRows is > 0 ? p.AreaRows.Value : VtFloorDefaults.DefaultRows;
        var geometry = PopupGeometry.TryResolve(areaCols, areaRows, p.Width, p.Height)
            ?? throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "area too small for popup");

        var cwd = ResolvePopupCwd(p.Cwd);
        var args = p.Args ?? [];

        IPaneRuntime? runtime = null;
        var assigned = false;
        var generation = 0L;

        // Hold the gate across StartAsync so a wait-loop Exited cannot take
        // the singleton before opened. Fast commands emit opened then closed.
        // Synchronous Exited from StartAsync skips opened (stale after teardown).
        await _popupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "popup.open");
            EnsureNotFrozenForMutation("popup.open");
            if (IsShuttingDown)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.ServerShuttingDown,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));
            }

            if (PopupUiBusy(p.ClientMode, connection))
                throw UiBusy();
            lock (_gate)
            {
                if (_popup is not null)
                    throw UiBusy();
            }

            var options = new PaneSpawnOptions
            {
                Id = PopupInternalId,
                Cwd = cwd,
                Command = command,
                Args = args,
                Env = WithPopupChildEnv(p.Env),
                StripPaneIdEnv = true,
                Cols = Math.Max(1, geometry.InnerCols),
                Rows = Math.Max(1, geometry.InnerRows),
                ScrollbackLimitBytes = AttachConfig.Advanced.ScrollbackLimitBytes,
                Terminal = AttachConfig.Terminal,
                HostTheme = SnapshotHostTheme(),
            };

            runtime = _paneFactory.Create(options);
            runtime.OutputReceived += OnPopupOutput;
            runtime.Exited += OnPopupExited;

            var session = new PopupSession
            {
                State = new PopupState
                {
                    Command = command,
                    Args = args,
                    Cwd = cwd,
                    AreaCols = areaCols,
                    AreaRows = areaRows,
                    InnerCols = geometry.InnerCols,
                    InnerRows = geometry.InnerRows,
                    StartedAt = _time.GetUtcNow(),
                },
                Runtime = runtime,
                RequestedWidth = p.Width ?? PopupGeometry.DefaultSize,
                RequestedHeight = p.Height ?? PopupGeometry.DefaultSize,
                Geometry = geometry,
            };

            lock (_gate)
            {
                if (IsShuttingDown)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.ServerShuttingDown,
                        ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));
                }

                if (_popup is not null)
                    throw UiBusy();
                if (PopupUiBusy(p.ClientMode, connection))
                    throw UiBusy();
                if (IsAttachController(connection?.ConnectionId))
                    NoteAttachClientMode(connection, p.ClientMode);
                generation = ++_popupGeneration;
                _popup = session with { Generation = generation };
                assigned = true;
            }

            // Assign first
            // so a concurrent client.host_theme.set snapshots this runtime.
            ApplyThemeBeforeStart(runtime);

            try
            {
                Volatile.Write(ref _popupStartThreadId, Environment.CurrentManagedThreadId);
                try
                {
                    await runtime.StartAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _popupStartThreadId, 0);
                }
            }
            catch (ControlPlaneException)
            {
                await FailPopupStartAsync(runtime, assigned).ConfigureAwait(false);
                runtime = null;
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Popup failed to start");
                await FailPopupStartAsync(runtime, assigned).ConfigureAwait(false);
                runtime = null;
                throw PopupStartFailed();
            }

            if (IsShuttingDown)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.ServerShuttingDown,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));
            }

            bool stillOwned;
            bool exitedDuringStart;
            lock (_gate)
            {
                stillOwned = _popup is { } current
                    && ReferenceEquals(current.Runtime, runtime)
                    && current.Generation == generation;
                exitedDuringStart = stillOwned
                    && _popupExitDuringStartGeneration == generation;
            }

            if (!stillOwned)
            {
                runtime = null;
                throw PopupStartFailed();
            }

            if (exitedDuringStart)
                throw PopupStartFailed();

            MarkAttachControllersTerminal();
            try
            {
                await EmitPopupLifecycleAsync("opened", ct).ConfigureAwait(false);
                await PostPopupSnapshotAsync().ConfigureAwait(false);
            }
            catch
            {
                await FailPopupStartAsync(runtime, assigned: true).ConfigureAwait(false);
                runtime = null;
                try
                {
                    await EmitPopupLifecycleAsync("closed", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Popup opened-fail closed emit failed");
                }

                throw;
            }

            return OkTyped(
                new PopupOpenResult
                {
                    Ok = true,
                    Cols = geometry.InnerCols,
                    Rows = geometry.InnerRows,
                    OuterCols = geometry.OuterCols,
                    OuterRows = geometry.OuterRows,
                },
                ProtocolJsonContext.Default.PopupOpenResult);
        }
        catch (Exception) when (runtime is not null)
        {
            await FailPopupStartAsync(runtime, assigned).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _popupGate.Release();
        }
    }

    private async Task<JsonElement> PopupCloseAsync(IClientConnection? connection, CancellationToken ct)
    {
        EnsureNotFrozenForMutation("popup.close");
        await _popupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "popup.close");
            EnsureNotFrozenForMutation("popup.close");
            var taken = await TakePopupAndFenceAsync().ConfigureAwait(false);
            if (taken is null)
                throw PopupNotOpen();

            await FinishPopupCloseAsync(taken, ct).ConfigureAwait(false);
            return OkTyped(
                new PopupCloseResult { Ok = true },
                ProtocolJsonContext.Default.PopupCloseResult);
        }
        finally
        {
            _popupGate.Release();
        }
    }

    private async Task<JsonElement> PopupSendKeysAsync(
        PopupSendKeysParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("popup.send_keys");
        AuthorizePopupInput(p.LeaseId, connection?.ConnectionId);
        var data = RequireField(p.Data, "data");
        var encoding = (EmptyToNull(p.Encoding) ?? "base64").ToLowerInvariant();
        byte[] bytes;
        try
        {
            bytes = encoding switch
            {
                "base64" => Convert.FromBase64String(data),
                "utf8" or "text" => Encoding.UTF8.GetBytes(data),
                _ => throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    $"Unsupported encoding: {encoding}"),
            };
        }
        catch (FormatException)
        {
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "Invalid base64 data");
        }

        await _popupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "popup.send_keys");
            EnsureNotFrozenForMutation("popup.send_keys");
            var session = PeekPopupSession();
            if (session is null)
                throw PopupNotOpen();
            await session.Runtime.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _popupGate.Release();
        }

        return OkTyped(
            new PopupSendKeysResult { Ok = true, AcceptedBytes = bytes.Length },
            ProtocolJsonContext.Default.PopupSendKeysResult);
    }

    private async Task<JsonElement> PopupResizeAsync(
        PopupResizeParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("popup.resize");
        var areaCols = p.AreaCols ?? 0;
        var areaRows = p.AreaRows ?? 0;
        if (areaCols < 1 || areaRows < 1)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "area_cols and area_rows are required");
        }

        await _popupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "popup.resize");
            EnsureNotFrozenForMutation("popup.resize");
            var session = PeekPopupSession() ?? throw PopupNotOpen();
            var geometry = PopupGeometry.TryResolve(
                    areaCols,
                    areaRows,
                    session.RequestedWidth,
                    session.RequestedHeight)
                ?? throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    "area too small for popup");

            await session.Runtime.ResizeAsync(geometry.InnerCols, geometry.InnerRows, ct)
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (_popup is { } current && ReferenceEquals(current.Runtime, session.Runtime))
                {
                    _popup = current with
                    {
                        Geometry = geometry,
                        State = current.State with
                        {
                            AreaCols = areaCols,
                            AreaRows = areaRows,
                            InnerCols = geometry.InnerCols,
                            InnerRows = geometry.InnerRows,
                        },
                    };
                }
            }

            await PostPopupSnapshotAsync().ConfigureAwait(false);
            return OkTyped(
                new PopupResizeResult
                {
                    Ok = true,
                    Cols = geometry.InnerCols,
                    Rows = geometry.InnerRows,
                    OuterCols = geometry.OuterCols,
                    OuterRows = geometry.OuterRows,
                },
                ProtocolJsonContext.Default.PopupResizeResult);
        }
        finally
        {
            _popupGate.Release();
        }
    }

    private void OnPopupOutput(IPaneRuntime runtime, ReadOnlyMemory<byte> data)
    {
        lock (_gate)
        {
            if (_popup is null || !ReferenceEquals(_popup.Runtime, runtime))
                return;
        }

        if (data.IsEmpty)
            return;

        // Copy before async emit. A handler must not retain the reader slice.
        var copy = data.ToArray();
        long feedGeneration = 0L;
        var requestPaint = true;
        if (runtime is IPaneVtSnapshot snap)
        {
            var decision = snap.LastFeedPaintDecision;
            feedGeneration = decision.FeedGeneration;
            requestPaint = decision.RequestPaint;
            // thread before the next PTY read. Do not wait for emit.
            if (requestPaint)
                _renderCoalescer.RequestPty(PopupRuntimeKey, feedGeneration);
        }

        BeginOutputEmit(PopupRuntimeKey);
        _ = EmitPopupRenderAsync(runtime, copy, feedGeneration, requestPaint);
    }

    private void OnPopupExited(IPaneRuntime runtime, int exitCode)
    {
        var startThread = Volatile.Read(ref _popupStartThreadId);
        if (startThread != 0 && startThread == Environment.CurrentManagedThreadId)
        {
            lock (_gate)
            {
                if (_popup is { } current && ReferenceEquals(current.Runtime, runtime))
                    _popupExitDuringStartGeneration = current.Generation;
            }
        }

        _ = ClosePopupFromExitAsync(runtime, exitCode);
    }

    private async Task ClosePopupFromExitAsync(IPaneRuntime runtime, int exitCode)
    {
        _ = exitCode;
        await _popupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var taken = await TakePopupIfRuntimeAndFenceAsync(runtime).ConfigureAwait(false);
            if (taken is null)
                return;

            await FinishPopupCloseAsync(taken, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popup exit close failed");
        }
        finally
        {
            _popupGate.Release();
        }
    }

    private async Task EmitPopupRenderAsync(
        IPaneRuntime runtime,
        ReadOnlyMemory<byte> data,
        long feedGeneration,
        bool requestPaint)
    {
        try
        {
            var delay = DelayOutputEmitAsync;
            if (delay is not null)
                await delay(runtime).ConfigureAwait(false);

            var paneGate = GetPaneEmitGate(PopupRuntimeKey);
            await paneGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_popup is null || !ReferenceEquals(_popup.Runtime, runtime))
                        return;
                }

                if (_subscriptions is null)
                    return;

                // Grid snapshot posting is independent of journal/redact emptiness:
                // the VT grid already advanced on Feed.
                var redacted = _redactor.RedactTerminalBytes(PopupRuntimeKey, data);

                // Drop Render when this Feed is already in a posted VT snapshot.
                var dropRender = feedGeneration > 0
                    && _paneSnapshotEpoch.TryGetValue(PopupRuntimeKey, out var epoch)
                    && feedGeneration <= epoch;
                if (dropRender)
                    return;

                // Snapshot-capable popups post the server grid. Capture, pack,
                // or post failure retries origin paint.
                // Do not post raw data. A stale-feed skip does not write bytes.
                if (runtime is IPaneVtSnapshot)
                {
                    // zero renders; closer requests one. Use the captured
                    // pair.
                    // a pending closer when CSI ?2026h advances the live
                    // FeedGeneration. A closer is a normal coalescer request.
                    // RetryPopupSnapshotPaint stays capture-fail only.
                    if (requestPaint)
                    {
                        var due = _renderCoalescer.Request(PopupRuntimeKey, feedGeneration);
                        if (due is { } decision)
                            FlushCoalescedPaneUnderGate(
                                decision.PaneId,
                                decision.FeedGeneration,
                                force: false);
                    }
                }
                else if (!redacted.IsEmpty)
                    WritePopupTerminalRenderCore(runtime, redacted);
            }
            finally
            {
                paneGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popup render emit failed");
        }
        finally
        {
            EndOutputEmit(PopupRuntimeKey);
        }
    }

    /// <summary>
    /// Failed capture, pack, or post for an <see cref="IPaneVtSnapshot"/>
    /// popup. Keep the epoch unpublished. Re-arm origin Full like
    // / <c>RetrySnapshotPaint</c>.
    /// writes only Frame. Do not post raw data.
    /// </summary>
    private void RetryPopupSnapshotPaint()
    {
        if (IsShuttingDown || _subscriptions is null)
            return;
        lock (_gate)
        {
            if (_popup is null)
                return;
        }

        _renderCoalescer.RequestOriginPaint(PopupRuntimeKey);
    }

    private void WritePopupTerminalRenderCore(IPaneRuntime runtime, ReadOnlyMemory<byte> redacted)
    {
        if (redacted.IsEmpty || _subscriptions is null)
            return;

        foreach (var part in TerminalRenderChunker.Split(redacted.Span))
        {
            if (part.Length == 0)
                continue;

            lock (_gate)
            {
                if (_popup is null || !ReferenceEquals(_popup.Runtime, runtime))
                    return;
            }

            var seq = _paneRenderSeq.AddOrUpdate(PopupRuntimeKey, 1L, static (_, prev) => prev + 1);
            var b64 = Convert.ToBase64String(part);
            var payload = RuntimeEventPayloadJson.WritePopupTerminalRender(b64, part.Length);
            var rec = new RuntimeEventRecord
            {
                Seq = seq,
                Class = EventClass.Render,
                Reliability = EventReliability.Render,
                Type = ProtocolEventTypes.TerminalRender,
                OccurredAt = _time.GetUtcNow(),
                PayloadJson = payload,
            };
            _subscriptions.PostLive(rec);
        }
    }

    private async Task PostPopupSnapshotAsync()
    {
        var paneGate = GetPaneEmitGate(PopupRuntimeKey);
        await paneGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            PostPopupSnapshotCore();
        }
        finally
        {
            paneGate.Release();
        }
    }

    /// <summary>
    /// Capture, pack, and post popup <c>terminal.render</c> kind=snapshot slices.
    /// Returns true when the feed epoch was published after every slice posted.
    /// An older capture feed generation cannot replace a newer epoch.
    /// </summary>
    private bool PostPopupSnapshotCore()
    {
        if (_subscriptions is null)
            return false;

        PopupSession? session;
        lock (_gate)
            session = _popup;
        if (session is null)
            return false;

        VtAttachSnapshot? typedPopup = null;
        long feedGeneration;
        try
        {
            if (session.Runtime is not IPaneVtSnapshot snap
                || !snap.TryCaptureAttachSnapshot(out typedPopup, out feedGeneration)
                || typedPopup is null)
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popup snapshot capture failed");
            return false;
        }

        // Same fence as TryPostVtSnapshotCore: an older or equal capture
        // must not replace a newer posted popup feed generation.
        if (_paneSnapshotEpoch.TryGetValue(PopupRuntimeKey, out var epoch)
            && feedGeneration <= epoch)
        {
            _logger.LogDebug(
                "Popup snapshot skipped: feed {Feed} behind epoch {Epoch}",
                feedGeneration, epoch);
            return true;
        }

        IReadOnlyList<byte[]> payloads;
        try
        {
            if (Interlocked.Exchange(ref _failNextSnapshotPack, 0) == 1)
                throw new InvalidOperationException("snapshot pack failed");
            var generation = _paneAttachSnapshotGeneration.AddOrUpdate(
                PopupRuntimeKey, 1L, static (_, prev) => prev + 1);
            payloads = AttachSnapshotPacker.Pack(
                PopupRuntimeKey,
                typedPopup,
                _redactor,
                generation: generation);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popup snapshot pack failed");
            return false;
        }

        try
        {
            foreach (var payload in payloads)
            {
                lock (_gate)
                {
                    if (_popup is null || !ReferenceEquals(_popup.Runtime, session.Runtime))
                        return false;
                }

                var partJson = Encoding.UTF8.GetString(payload);
                var redacted = _redactor.RedactJsonPayload(ProtocolEventTypes.TerminalRender, partJson);
                byte[] payloadUtf8 = string.Equals(redacted, partJson, StringComparison.Ordinal)
                    ? payload
                    : Encoding.UTF8.GetBytes(redacted);
                var seq = _paneRenderSeq.AddOrUpdate(PopupRuntimeKey, 1L, static (_, prev) => prev + 1);
                var rec = new RuntimeEventRecord
                {
                    Seq = seq,
                    Class = EventClass.Render,
                    Reliability = EventReliability.Render,
                    Type = ProtocolEventTypes.TerminalRender,
                    OccurredAt = _time.GetUtcNow(),
                    PayloadJson = redacted,
                    PayloadUtf8 = payloadUtf8,
                };
                if (!_subscriptions.PostLive(rec))
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popup snapshot post failed");
            return false;
        }

        _paneSnapshotEpoch[PopupRuntimeKey] = feedGeneration;
        return true;
    }

    /// <summary>Live-only. Popup state is not journaled or restored.</summary>
    private async Task EmitPopupLifecycleAsync(string state, CancellationToken ct)
    {
        PopupGeometryResult? geometry = null;
        PopupSize? width = null;
        PopupSize? height = null;
        int? areaCols = null;
        int? areaRows = null;
        if (string.Equals(state, "opened", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (_popup is { } session)
                {
                    geometry = session.Geometry;
                    width = session.RequestedWidth;
                    height = session.RequestedHeight;
                    areaCols = session.State.AreaCols;
                    areaRows = session.State.AreaRows;
                }
            }
        }

        var payload = RuntimeEventPayloadJson.WritePopupLifecycle(
            state, geometry, width, height, areaCols, areaRows);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PopupLifecycle, payload);
        if (_subscriptions is null)
            return;

        var paneGate = GetPaneEmitGate(PopupRuntimeKey);
        await paneGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seq = _paneRenderSeq.AddOrUpdate(PopupRuntimeKey, 1L, static (_, prev) => prev + 1);
            var rec = new RuntimeEventRecord
            {
                Seq = seq,
                Class = EventClass.Lifecycle,
                Reliability = EventReliability.Reliable,
                Type = ProtocolEventTypes.PopupLifecycle,
                OccurredAt = _time.GetUtcNow(),
                PayloadJson = payload,
            };
            _subscriptions.PostLive(rec);
            NotifyPluginEvent(ProtocolEventTypes.PopupLifecycle, payload);
        }
        finally
        {
            paneGate.Release();
        }
    }

    private async Task FailPopupStartAsync(IPaneRuntime runtime, bool assigned)
    {
        if (assigned)
        {
            var taken = await TakePopupIfRuntimeAndFenceAsync(runtime).ConfigureAwait(false);
            if (taken is null)
                return;

            await WaitOutputEmitsIdleAsync(PopupRuntimeKey).ConfigureAwait(false);
            await DisposePopupSessionAsync(taken).ConfigureAwait(false);
            return;
        }

        runtime.OutputReceived -= OnPopupOutput;
        runtime.Exited -= OnPopupExited;
        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popup start-fail dispose failed");
        }
    }

    private async Task DisposePopupSessionAsync(PopupSession session)
    {
        session.Runtime.OutputReceived -= OnPopupOutput;
        session.Runtime.Exited -= OnPopupExited;
        try
        {
            await session.Runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popup dispose failed");
        }

        try
        {
            _ = _redactor.FlushTerminalStream(PopupRuntimeKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popup redact flush failed");
        }
    }

    private PopupSession? PeekPopupSession()
    {
        lock (_gate)
            return _popup;
    }

    private PopupSession? TakePopupUnlocked()
    {
        lock (_gate)
        {
            var taken = _popup;
            _popup = null;
            return taken;
        }
    }

    private void TakePopupIfRuntime(IPaneRuntime runtime)
    {
        lock (_gate)
        {
            if (_popup is not null && ReferenceEquals(_popup.Runtime, runtime))
                _popup = null;
        }
    }

    private async Task<PopupSession?> TakePopupAndFenceAsync()
    {
        var paneGate = GetPaneEmitGate(PopupRuntimeKey);
        await paneGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var taken = TakePopupUnlocked();
            if (taken is not null)
                FencePopupRendersUnlocked();
            return taken;
        }
        finally
        {
            paneGate.Release();
        }
    }

    private async Task<PopupSession?> TakePopupIfRuntimeAndFenceAsync(IPaneRuntime runtime)
    {
        var paneGate = GetPaneEmitGate(PopupRuntimeKey);
        await paneGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            PopupSession? taken;
            lock (_gate)
            {
                if (_popup is null || !ReferenceEquals(_popup.Runtime, runtime))
                    return null;
                taken = _popup;
                _popup = null;
            }

            FencePopupRendersUnlocked();
            return taken;
        }
        finally
        {
            paneGate.Release();
        }
    }

    /// <summary>
    /// Drop queued popup renders and the VT snapshot epoch for <see cref="PopupRuntimeKey"/>.
    /// Caller holds the popup emit gate after taking the current popup runtime.
    /// A replacement popup restarts feed generation at zero; leaving the old epoch
    /// would reject its first snapshots as stale (no byte Remap fallback on VT).
    /// </summary>
    private void FencePopupRendersUnlocked()
    {
        _ = _paneSnapshotEpoch.TryRemove(PopupRuntimeKey, out _);
        _renderCoalescer.Forget(PopupRuntimeKey);
        _subscriptions?.FenceAttachedPaneRender(PopupRuntimeKey, PeekNextRenderSeq(PopupRuntimeKey));
    }

    private async Task FinishPopupCloseAsync(PopupSession session, CancellationToken ct)
    {
        await DisposePopupSessionAsync(session).ConfigureAwait(false);
        await EmitPopupLifecycleAsync("closed", ct).ConfigureAwait(false);
    }

    private async Task DisposePopupOnShutdownAsync()
    {
        await _popupGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var taken = await TakePopupAndFenceAsync().ConfigureAwait(false);
            if (taken is null)
                return;

            await DisposePopupSessionAsync(taken).ConfigureAwait(false);
            try
            {
                await EmitPopupLifecycleAsync("closed", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Popup shutdown closed emit failed");
            }
        }
        finally
        {
            _popupGate.Release();
        }
    }

    private JsonObject? PopupSnapshotJson()
    {
        lock (_gate)
        {
            if (_popup is null)
                return null;
            var obj = new JsonObject
            {
                ["open"] = true,
                ["cols"] = _popup.State.InnerCols,
                ["rows"] = _popup.State.InnerRows,
                ["outer_cols"] = _popup.Geometry.OuterCols,
                ["outer_rows"] = _popup.Geometry.OuterRows,
                ["area_cols"] = _popup.State.AreaCols,
                ["area_rows"] = _popup.State.AreaRows,
            };
            if (_popup.RequestedWidth is { } width)
                obj["width"] = PopupSizeNode(width);
            if (_popup.RequestedHeight is { } height)
                obj["height"] = PopupSizeNode(height);
            return obj;
        }
    }

    private string ResolvePopupCwd(string? cwd)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
            return ResolveWorkspaceCwd(cwd);

        var focused = _state.Snapshot().FocusedWorkspaceId;
        if (focused is { } id && _state.GetWorkspace(id) is { } ws)
            return ws.Cwd;
        return Environment.CurrentDirectory;
    }

    internal static Dictionary<string, string> StripPaneIdEnv(IReadOnlyDictionary<string, string>? source)
        => PaneIdEnvironment.Strip(source);

    private IReadOnlyDictionary<string, string> WithPopupChildEnv(
        IReadOnlyDictionary<string, string>? source)
    {
        var map = source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source, StringComparer.Ordinal);
        ApplyPaneBaseEnv(map);
        return StripPaneIdEnv(map);
    }

    internal static bool IsBusyClientMode(string? clientMode)
    {
        if (string.IsNullOrWhiteSpace(clientMode))
            return false;
        return !string.Equals(clientMode.Trim(), "terminal", StringComparison.OrdinalIgnoreCase);
    }

    internal void ClearAttachClientMode(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;
        lock (_gate)
        {
            _attachClientModes.Remove(connectionId);
            _publishedAttachClientModes.Remove(connectionId);
        }
    }

    internal void NoteAttachClientMode(IClientConnection? connection, string? clientMode)
    {
        if (connection is null || string.IsNullOrWhiteSpace(connection.ConnectionId))
            return;
        if (string.IsNullOrWhiteSpace(clientMode))
            return;
        lock (_gate)
        {
            _attachClientModes[connection.ConnectionId] = clientMode.Trim().ToLowerInvariant();
            _publishedAttachClientModes.Add(connection.ConnectionId);
        }
    }

    internal void NoteAttachClientMode(string? connectionId, string? clientMode)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(clientMode))
            return;
        lock (_gate)
        {
            _attachClientModes[connectionId] = clientMode.Trim().ToLowerInvariant();
            _publishedAttachClientModes.Add(connectionId);
        }
    }

    private bool PopupUiBusy(string? clientMode, IClientConnection? connection)
    {
        if (OverlayReservationExists())
            return true;
        if (connection is not null && _overlay.HasReservation(connection.ConnectionId))
            return true;
        if (IsBusyClientMode(clientMode))
            return true;

        var connId = connection?.ConnectionId;
        if (IsAttachController(connId) && string.IsNullOrWhiteSpace(clientMode))
            return true;

        if (AttachControllerModeUnpublished())
            return true;

        lock (_gate)
        {
            foreach (var mode in _attachClientModes.Values)
            {
                if (IsBusyClientMode(mode))
                    return true;
            }
        }

        return false;
    }

    private bool AttachControllerModeUnpublished()
    {
        foreach (var attachment in _attachments.ListAll())
        {
            if (!string.Equals(attachment.Mode, AttachmentModes.Control, StringComparison.Ordinal))
                continue;
            lock (_gate)
            {
                if (!_attachClientModes.ContainsKey(attachment.ConnectionId))
                    return true;
            }
        }

        return false;
    }

    private bool IsAttachController(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return false;
        if (ConnectionHoldsInputLease(connectionId))
            return true;
        foreach (var attachment in _attachments.ListAll())
        {
            if (string.Equals(attachment.ConnectionId, connectionId, StringComparison.Ordinal)
                && string.Equals(attachment.Mode, AttachmentModes.Control, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool ConnectionHoldsInputLease(string connectionId)
    {
        foreach (var pane in _state.Snapshot().Panes.Values)
        {
            var lease = _leases.GetActive(pane.Id.Value, LeaseScopes.Input);
            if (lease is not null
                && string.Equals(lease.HolderId, connectionId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void MarkAttachControllersTerminal()
    {
        lock (_gate)
        {
            foreach (var key in _attachClientModes.Keys.ToArray())
                _attachClientModes[key] = "terminal";
        }
    }

    private void AuthorizePopupInput(string? leaseId, string? connectionId)
    {
        if (!string.IsNullOrWhiteSpace(leaseId))
        {
            var lease = _leases.Get(leaseId);
            if (lease is null)
                throw new ControlPlaneException(ProtocolErrorCodes.LeaseRequired, "Controller lease required");
            if (_state.GetPane(new PaneId(lease.PaneId)) is not null)
            {
                AuthorizeInput(lease.PaneId, leaseId, connectionId);
                return;
            }

            ThrowForAuthorize(AuthorizeOrphanInputLease(lease, connectionId));
            return;
        }

        var focused = FocusedPaneIdOrNull();
        if (!string.IsNullOrWhiteSpace(focused))
        {
            AuthorizeInput(focused, leaseId, connectionId);
            return;
        }

        throw new ControlPlaneException(ProtocolErrorCodes.LeaseRequired, "Controller lease required");
    }

    private LeaseAuthorizeResult AuthorizeOrphanInputLease(LeaseState lease, string? connectionId)
    {
        if (!string.Equals(lease.Scope, LeaseScopes.Input, StringComparison.Ordinal))
            return LeaseAuthorizeResult.Missing();
        if (lease.State != LeaseStates.Granted)
        {
            return lease.State == LeaseStates.Expired
                ? LeaseAuthorizeResult.Expired(lease)
                : LeaseAuthorizeResult.Missing();
        }

        var now = _time.GetUtcNow();
        if (now > lease.GraceEndsAt)
            return LeaseAuthorizeResult.Expired(lease);
        if (!string.IsNullOrWhiteSpace(connectionId)
            && !string.Equals(lease.HolderId, connectionId, StringComparison.Ordinal))
        {
            return LeaseAuthorizeResult.WrongHolder(lease);
        }

        return LeaseAuthorizeResult.Authorized(lease);
    }

    private string? FocusedPaneIdOrNull()
    {
        var snap = _state.Snapshot();
        if (snap.FocusedWorkspaceId is { } wsId
            && snap.Workspaces.TryGetValue(wsId.Value, out var ws)
            && ws.FocusedTabId is { } tabId
            && snap.Tabs.TryGetValue(tabId.Value, out var tab)
            && tab.FocusedPaneId is { } paneId)
        {
            return paneId.Value;
        }

        return null;
    }

    private static JsonNode PopupSizeNode(PopupSize size) =>
        size.IsPercent
            ? JsonValue.Create(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size.Value}%"))
            : JsonValue.Create(size.Value);

    private static ControlPlaneException PopupNotOpen() =>
        new(ProtocolErrorCodes.NotFound, PopupNotOpenMessage);

    private static ControlPlaneException UiBusy() =>
        new(ProtocolErrorCodes.InvalidState, PopupUiBusyMessage);

    private static ControlPlaneException PopupStartFailed() =>
        new(
            ProtocolErrorCodes.PaneStartFailed,
            ProtocolErrors.MeaningOf(ProtocolErrorCodes.PaneStartFailed));

    private sealed record PopupSession
    {
        public required PopupState State { get; init; }

        public required IPaneRuntime Runtime { get; init; }

        public PopupSize? RequestedWidth { get; init; }

        public PopupSize? RequestedHeight { get; init; }

        public required PopupGeometryResult Geometry { get; init; }

        public long Generation { get; init; }
    }
}
