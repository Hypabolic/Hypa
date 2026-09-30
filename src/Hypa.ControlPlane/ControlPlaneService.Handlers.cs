using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal Task<JsonElement> HandlePingAsync(EmptyParams _, CancellationToken ct) =>
        Task.FromResult(OkTyped(new PingResult { Ok = true, Protocol = ProtocolVersion.Current },
            ProtocolJsonContext.Default.PingResult));

    internal Task<JsonElement> HandleServerStopAsync(EmptyParams unused, CancellationToken ct)
    {
        _ = unused;
        _ = ct;
        return Task.FromResult(OkTyped(new ServerStopResult { Ok = true },
            ProtocolJsonContext.Default.ServerStopResult));
    }

    internal Task<JsonElement> HandleServerLiveHandoffAsync(EmptyParams unused, CancellationToken ct) =>
        HandleServerLiveHandoffAsync(
            unused,
            OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            ct);

    internal Task<JsonElement> HandleServerLiveHandoffAsync(
        EmptyParams unused,
        bool unix,
        CancellationToken ct)
    {
        _ = unused;
        _ = ct;
        if (!unix)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "server.live_handoff is Unix-only.",
                ProtocolErrors.InvalidState);
        }

        MarkLiveHandoffStarted();
        return Task.FromResult(OkTyped(new ServerLiveHandoffResult { Ok = true },
            ProtocolJsonContext.Default.ServerLiveHandoffResult));
    }

    internal async Task<JsonElement> HandleServerReloadConfigAsync(EmptyParams unused, CancellationToken ct)
    {
        _ = unused;
        var previous = _attachConfigRuntime.Current;
        var report = _attachConfigRuntime.ReloadFromDisk();
        var status = report.ToLiveWireStatus();
        if (!string.Equals(status, ConfigReloadStatuses.Applied, StringComparison.Ordinal)
            && !string.Equals(status, ConfigReloadStatuses.Failed, StringComparison.Ordinal))
        {
            status = ConfigReloadStatuses.Failed;
        }

        var result = new ConfigReloadResult
        {
            Status = status,
            Diagnostics = report.Diagnostics,
        };
        if (report.Status == ConfigReloadStatus.Applied
            && string.Equals(status, ConfigReloadStatuses.Applied, StringComparison.Ordinal))
        {
            PostLiveConfigReloaded(result);
            await EmitSettingsChangedAfterReloadAsync(previous, _attachConfigRuntime.Current, ct)
                .ConfigureAwait(false);
            if (!_attachConfigRuntime.Current.Experimental.PaneHistory)
                PersistPaneHistory();
            if (_attachConfigRuntime.Current.Update.ManifestCheck)
            {
                var catalog = _detector as IAgentManifestCatalog;
                if (catalog is not null)
                    _ = Task.Run(() => catalog.CheckRemoteUpdates(true));
            }
        }

        return OkTyped(result, ProtocolJsonContext.Default.ConfigReloadResult);
    }

    internal Task<JsonElement> HandleServerAgentManifestsAsync(EmptyParams unused, CancellationToken ct)
    {
        _ = unused;
        _ = ct;
        var catalog = _detector as IAgentManifestCatalog;
        var result = new AgentManifestStatusResult
        {
            LastCheckUnix = catalog?.LastCheckUnix,
            LastResult = catalog?.LastResult,
            Manifests = ToAgentManifestInfos(catalog?.ListSummaries()),
        };
        return Task.FromResult(OkTyped(result, ProtocolJsonContext.Default.AgentManifestStatusResult));
    }

    internal Task<JsonElement> HandleServerReloadAgentManifestsAsync(EmptyParams unused, CancellationToken ct)
    {
        _ = unused;
        _ = ct;
        var catalog = _detector as IAgentManifestCatalog;
        var summaries = catalog?.Reload() ?? [];
        // Do not restart panes.
        foreach (var pane in _state.ListPanes())
            _detectionScanner.Mark(pane.Id.Value, pane.OccupantGeneration);

        var result = new AgentManifestReloadResult
        {
            Manifests = ToAgentManifestInfos(summaries),
        };
        return Task.FromResult(OkTyped(result, ProtocolJsonContext.Default.AgentManifestReloadResult));
    }

    private static IReadOnlyList<AgentManifestInfo> ToAgentManifestInfos(
        IReadOnlyList<AgentManifestSummary>? summaries)
    {
        if (summaries is null || summaries.Count == 0)
            return [];

        var list = new List<AgentManifestInfo>(summaries.Count);
        foreach (var summary in summaries)
        {
            list.Add(new AgentManifestInfo
            {
                Agent = summary.Agent,
                Source = summary.Source,
                SourceKind = summary.SourceKind,
                ActiveVersion = summary.ActiveVersion,
                CachedRemoteVersion = summary.CachedRemoteVersion,
                LocalOverrideShadowingRemote = summary.LocalOverrideShadowingRemote,
                RemoteUpdateResult = summary.RemoteUpdateResult,
                RemoteUpdateError = summary.RemoteUpdateError,
                RemoteLastCheckedUnix = summary.RemoteLastCheckedUnix,
                Warning = summary.Warning,
            });
        }

        return list;
    }

    internal async Task<JsonElement> HandleSessionSnapshotAsync(
        EmptyParams _,
        IClientConnection? connection,
        CancellationToken ct)
    {
        await FlushExpiredMetadataAsync(ct).ConfigureAwait(false);
        return Ok(SessionSnapshot(connection));
    }

    internal async Task<JsonElement> HandleWorkspaceCreateAsync(
        WorkspaceCreateParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await WorkspaceCreateAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleWorkspaceListAsync(EmptyParams _, CancellationToken ct)
    {
        await FlushExpiredMetadataAsync(ct).ConfigureAwait(false);
        return Ok(WorkspaceList());
    }

    internal async Task<JsonElement> HandleWorkspaceGetAsync(WorkspaceGetParams p, CancellationToken ct)
    {
        await FlushExpiredMetadataAsync(ct).ConfigureAwait(false);
        return Ok(WorkspaceGet(p));
    }

    internal async Task<JsonElement> HandlePaneCreateAsync(
        PaneCreateParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneCreateAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneListAsync(EmptyParams _, CancellationToken ct)
    {
        await FlushExpiredMetadataForReadAsync(ct).ConfigureAwait(false);
        return Ok(PaneList());
    }

    internal async Task<JsonElement> HandlePaneGetAsync(PaneGetParams p, CancellationToken ct)
    {
        await FlushExpiredMetadataForReadAsync(ct).ConfigureAwait(false);
        return Ok(PaneGet(p));
    }

    internal async Task<JsonElement> HandlePaneSendTextAsync(
        PaneSendTextParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await PaneSendTextAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneSendKeysAsync(
        PaneSendKeysParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await PaneSendKeysAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneResizeAsync(
        PaneResizeParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await PaneResizeAsync(p, connection, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandlePaneReadAsync(PaneReadParams p, CancellationToken ct) =>
        Task.FromResult(Ok(PaneRead(p)));

    internal Task<JsonElement> HandlePaneScrollAsync(PaneScrollParams p, CancellationToken ct) =>
        Task.FromResult(PaneScroll(p));

    internal async Task<JsonElement> HandlePaneCloseAsync(
        PaneCloseParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneCloseAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneWaitForOutputAsync(
        PaneWaitForOutputParams p, CancellationToken ct) =>
        OkTyped(await PaneWaitForOutputTypedAsync(p, ct).ConfigureAwait(false),
            ProtocolJsonContext.Default.PaneWaitForOutputResult);

    internal async Task<JsonElement> HandleAgentListAsync(EmptyParams _, CancellationToken ct)
    {
        await FlushExpiredMetadataForReadAsync(ct).ConfigureAwait(false);
        return Ok(AgentList());
    }

    internal async Task<JsonElement> HandleAgentGetAsync(PaneGetParams p, CancellationToken ct)
    {
        await FlushExpiredMetadataForReadAsync(ct).ConfigureAwait(false);
        return Ok(AgentGet(p));
    }

    internal async Task<JsonElement> HandleAgentWaitAsync(AgentWaitParams p, CancellationToken ct) =>
        Ok(await AgentWaitAsync(p, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleAgentPromptAsync(
        AgentPromptParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await AgentPromptAsync(p, connection, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandleAgentReadAsync(PaneReadParams p, CancellationToken ct) =>
        Task.FromResult(Ok(AgentRead(p)));

    internal Task<JsonElement> HandleEventsSubscribeAsync(
        EventsSubscribeParams p, IClientConnection? connection, CancellationToken ct) =>
        Task.FromResult(Ok(EventsSubscribe(p, connection)));

    internal Task<JsonElement> HandleEventsUnsubscribeAsync(
        EventsUnsubscribeParams p, IClientConnection? connection, CancellationToken ct) =>
        Task.FromResult(Ok(EventsUnsubscribe(p, connection)));

    internal Task<JsonElement> HandleRuntimeHealthAsync(EmptyParams _, CancellationToken ct) =>
        Task.FromResult(Ok(RuntimeHealth()));

    internal async Task<JsonElement> HandleLeaseClaimAsync(
        LeaseClaimParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await RuntimeLeaseClaimAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleLeaseReleaseAsync(
        LeaseReleaseParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await RuntimeLeaseReleaseAsync(p, connection, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandleLeaseRenewAsync(
        LeaseRenewParams p, IClientConnection? connection, CancellationToken ct) =>
        Task.FromResult(Ok(RuntimeLeaseRenew(p, connection)));

    internal async Task<JsonElement> HandleTerminalObserveAsync(
        TerminalObserveParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await TerminalObserveAsync(p, connection, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandleTerminalVisibleSetAsync(
        TerminalVisibleSetParams p, IClientConnection? connection, CancellationToken ct)
    {
        _ = ct;
        return Task.FromResult(Ok(TerminalVisibleSet(p, connection)));
    }

    internal async Task<JsonElement> HandleTerminalControlAsync(
        TerminalControlParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await TerminalControlAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleBindingSetAsync(BindingSetParams p, CancellationToken ct) =>
        Ok(await RuntimeBindingSetAsync(p, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandleBindingGetAsync(BindingGetParams p, CancellationToken ct) =>
        Task.FromResult(Ok(RuntimeBindingGet(p)));

    internal async Task<JsonElement> HandleExportAckAsync(ExportAckParams p, CancellationToken ct) =>
        Ok(await EventsExportAckAsync(p, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleCheckpointPrepareAsync(
        CheckpointPrepareParams p, CancellationToken ct) =>
        Ok(await RuntimeCheckpointPrepareAsync(p, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleCheckpointExportAsync(
        CheckpointExportParams p, CancellationToken ct) =>
        Ok(await RuntimeCheckpointExportAsync(p, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleCheckpointAbortAsync(
        CheckpointAbortParams p, CancellationToken ct) =>
        Ok(await RuntimeCheckpointAbortAsync(p, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleHandoffExportAsync(
        HandoffExportParams p, IClientConnection? connection, CancellationToken ct) =>
        OkTyped(await RuntimeHandoffExportAsync(p, connection, ct).ConfigureAwait(false),
            ProtocolJsonContext.Default.HandoffExportResult);

    internal async Task<JsonElement> HandleHandoffAdoptAsync(
        HandoffAdoptParams p, IClientConnection? connection, CancellationToken ct) =>
        OkTyped(await RuntimeHandoffAdoptAsync(p, connection, ct).ConfigureAwait(false),
            ProtocolJsonContext.Default.HandoffAdoptResult);

    private async Task<PaneWaitForOutputResult> PaneWaitForOutputTypedAsync(
        PaneWaitForOutputParams p, CancellationToken ct)
    {
        var id = RequireId(p.PaneId, "pane_id");
        if (!string.IsNullOrEmpty(p.Match) && !string.IsNullOrEmpty(p.MatchRegex))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));
        }

        if (p.TimeoutMs is < 0)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));
        }

        Regex? regex = null;
        if (!string.IsNullOrEmpty(p.MatchRegex))
        {
            try
            {
                regex = new Regex(p.MatchRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));
            }
        }

        var source = ProtocolPaneReadSources.Normalize(
            string.IsNullOrWhiteSpace(p.Source) ? ProtocolPaneReadSources.Recent : p.Source);
        var lines = p.Lines is > 0 ? p.Lines.Value : 200;
        var runtime = GetRuntime(id);
        var before = ReadPaneSource(runtime, source, lines);
        if (TryMatchWait(before, before, p.Match, regex, out var already, out _))
        {
            if (already)
                await EmitOutputMatchedAsync(id, already, ct).ConfigureAwait(false);
            return new PaneWaitForOutputResult
            {
                PaneId = id,
                Changed = false,
                Matched = already,
                Text = before,
                TimedOut = false,
            };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (p.TimeoutMs is int timeoutMs)
            cts.CancelAfter(timeoutMs);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
                var now = ReadPaneSource(runtime, source, lines);
                if (TryMatchWait(now, before, p.Match, regex, out var matched, out var changed))
                {
                    if (matched)
                        await EmitOutputMatchedAsync(id, matched, ct).ConfigureAwait(false);
                    return new PaneWaitForOutputResult
                    {
                        PaneId = id,
                        Changed = changed,
                        Matched = matched,
                        Text = now,
                        TimedOut = false,
                    };
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Server match budget elapsed.
        }

        if (ct.IsCancellationRequested)
            ct.ThrowIfCancellationRequested();

        var final = ReadPaneSource(runtime, source, lines);
        if (TryMatchWait(final, before, p.Match, regex, out var finalMatched, out var finalChanged))
        {
            if (finalMatched)
                await EmitOutputMatchedAsync(id, finalMatched, ct).ConfigureAwait(false);
            return new PaneWaitForOutputResult
            {
                PaneId = id,
                Changed = finalChanged,
                Matched = finalMatched,
                Text = final,
                TimedOut = false,
            };
        }

        throw new ControlPlaneException(
            ProtocolErrorCodes.InvalidState,
            PaneWaitForOutputErrors.TimeoutMessage,
            PaneWaitForOutputErrors.Timeout);
    }

    /// <summary>
    /// A substring or regex matches text that is already present.
    /// With no predicate, the wait matches only when the snapshot changes.
    /// </summary>
    private static bool TryMatchWait(
        string now,
        string before,
        string? substring,
        Regex? regex,
        out bool matched,
        out bool changed)
    {
        changed = !string.Equals(now, before, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(substring))
        {
            matched = now.Contains(substring, StringComparison.Ordinal);
            return matched;
        }

        if (regex is not null)
        {
            matched = RegexMatchesAnyLine(regex, now);
            return matched;
        }

        matched = changed;
        return changed;
    }

    /// <summary>
    /// The pattern runs on each output line.
    /// A final newline does not add an extra empty line.
    /// </summary>
    private static bool RegexMatchesAnyLine(Regex regex, string text)
    {
        if (text.Length == 0)
            return false;

        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n')
                continue;

            var end = i;
            if (end > start && text[end - 1] == '\r')
                end--;
            var trailingEmpty = i == text.Length && end == start && start > 0;
            if (!trailingEmpty && regex.IsMatch(text[start..end]))
                return true;
            start = i + 1;
        }

        return false;
    }

    private async Task<HandoffExportResult> RuntimeHandoffExportAsync(
        HandoffExportParams p, IClientConnection? connection, CancellationToken ct)
    {
        var paneId = RequireId(p.PaneId, "pane_id");
        var leaseId = RequireId(p.LeaseId, "lease_id");
        AuthorizeInput(paneId, leaseId, connection?.ConnectionId);

        var runtime = GetRuntime(paneId);
        if (runtime is not IPaneHandoffControl handoff || !handoff.SupportsHandoff)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.CapabilityInvalid,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.CapabilityInvalid));
        }

        var pane = _state.GetPane(new PaneId(paneId))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "session/workspace/pane/occupant missing");
        var generation = pane.OccupantGeneration <= 0 ? 1 : pane.OccupantGeneration;
        var exported = await handoff.ExportHandoffAsync(
            _state.Snapshot().Id.Value, generation, ct).ConfigureAwait(false);
        if (!exported.IsOk)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.CapabilityInvalid,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.CapabilityInvalid));
        }

        return new HandoffExportResult
        {
            PaneId = paneId,
            HandoffPath = exported.Value.HandoffPath,
            Nonce = exported.Value.NonceHex,
            Generation = exported.Value.Generation,
        };
    }

    private async Task<HandoffAdoptResult> RuntimeHandoffAdoptAsync(
        HandoffAdoptParams p, IClientConnection? connection, CancellationToken ct)
    {
        var paneId = RequireId(p.PaneId, "pane_id");
        var leaseId = RequireId(p.LeaseId, "lease_id");
        var path = RequireId(p.HandoffPath, "handoff_path");
        var nonce = RequireId(p.Nonce, "nonce");
        var generation = p.Generation ?? 0;
        AuthorizeInput(paneId, leaseId, connection?.ConnectionId);

        var runtime = GetRuntime(paneId);
        if (runtime is not IPaneHandoffControl handoff || !handoff.SupportsHandoff)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.CapabilityInvalid,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.CapabilityInvalid));
        }

        var adopted = await handoff.AdoptHandoffAsync(path, nonce, generation, ct).ConfigureAwait(false);
        if (!adopted.IsOk)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.CapabilityInvalid,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.CapabilityInvalid));
        }

        return new HandoffAdoptResult
        {
            PaneId = paneId,
            Adopted = adopted.Value.Adopted,
            ChildPid = adopted.Value.ChildPid,
        };
    }

    private static string RequireId(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams))
            : value;

    private static JsonElement OkTyped<T>(T value, JsonTypeInfo<T> info) =>
        JsonSerializer.SerializeToElement(value, info);
}
