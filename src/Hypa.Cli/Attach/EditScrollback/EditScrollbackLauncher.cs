using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.EditScrollback;

/// <summary>
/// Client launch for edit-scrollback. Server supplies text via
/// <c>pane.read</c> <c>source=recent</c>. The source pane is not paused.
/// </summary>
internal static class EditScrollbackLauncher
{
    public const string OverlayOpenError = "scrollback editor already open";

    public const string NoPaneError = "no focused pane";

    public const string SplitFailedError = "scrollback split missing pane_id";

    public const string ReadFailedError = "edit scrollback failed";

    public const string Label = "scrollback";

    public static async Task LaunchAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (live.EditScrollback is not null)
            throw new InvalidOperationException(OverlayOpenError);

        var source = live.Dispatcher.PaneId ?? live.PaneId;
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException(NoPaneError);

        LeaveCopyMode(live);

        var env = live.AttachEnvironment ?? new SystemAttachConfigEnvironment();
        if (!EditorCommandResolver.IsSet(env))
            throw new InvalidOperationException(EditorCommandResolver.UnsetError);

        var files = live.ScrollbackFiles ?? new ScrollbackHistoryFile(env);
        var text = await ReadRecentAsync(live, control, source, ct).ConfigureAwait(false);
        var path = files.WriteUnique(text);
        if (!EditorCommandResolver.TryResolve(env, path, out var editor, out var error))
        {
            files.TryDelete(path);
            throw new InvalidOperationException(error ?? EditorCommandResolver.UnsetError);
        }

        string? editorPane = null;
        try
        {
            var argNodes = new JsonNode?[editor.Args.Count];
            for (var i = 0; i < editor.Args.Count; i++)
                argNodes[i] = JsonValue.Create(editor.Args[i]);
            var created = await control.CallAsync(
                    ProtocolMethods.PaneSplit,
                    new JsonObject
                    {
                        ["pane_id"] = source,
                        ["direction"] = "down",
                        ["command"] = editor.Command,
                        ["args"] = new JsonArray(argNodes),
                        ["label"] = Label,
                    },
                    ct)
                .ConfigureAwait(false);
            editorPane = TryString(created, "pane_id");
            if (string.IsNullOrWhiteSpace(editorPane))
                throw new InvalidOperationException(SplitFailedError);

            await control.CallAsync(
                    ProtocolMethods.PaneZoom,
                    new JsonObject
                    {
                        ["pane_id"] = editorPane,
                        ["mode"] = "on",
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            if (editorPane is { Length: > 0 })
                await CloseQuietAsync(control, editorPane, ct).ConfigureAwait(false);
            files.TryDelete(path);
            throw;
        }

        var previousZoomed = live.ChromeSeed?.Zoomed ?? live.Chrome?.Zoomed ?? false;
        var previousZoomedPane = previousZoomed
            ? live.ChromeSeed?.ZoomedPaneId ?? source
            : null;
        live.EditScrollback = new EditScrollbackSession(
            source,
            editorPane,
            source,
            previousZoomed,
            previousZoomedPane,
            path);
        live.Dispatcher.FocusPane(editorPane);
    }

    public static async Task CompleteAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        bool alreadyClosed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var session = live.EditScrollback;
        if (session is null)
            return;
        live.EditScrollback = null;
        var acceptedEditor = live.Engine.HasConfirmAccept
            && string.Equals(
                AttachSession.FirstNonEmpty(
                    live.Engine.PendingClosePaneTarget,
                    live.PendingClosePaneId,
                    live.Dispatcher.PaneId,
                    live.PaneId),
                session.EditorPaneId,
                StringComparison.Ordinal);
        if (acceptedEditor)
        {
            live.CompletedEditScrollbackPaneId = session.EditorPaneId;
            live.EditScrollbackRestorePaneId = session.SourcePaneId;
        }
        else
            DismissConfirmCloseForEditor(live, session.EditorPaneId);

        if (!alreadyClosed)
            await CloseQuietAsync(control, session.EditorPaneId, ct).ConfigureAwait(false);

        await RestoreZoomAsync(control, session, ct).ConfigureAwait(false);

        var files = live.ScrollbackFiles ?? new ScrollbackHistoryFile(live.AttachEnvironment);
        files.TryDelete(session.TempPath);

        if (!acceptedEditor && !string.IsNullOrWhiteSpace(session.SourcePaneId))
            live.Dispatcher.FocusPane(session.SourcePaneId);
    }

    public static bool IsEditorExit(JsonElement ev, string editorPaneId)
    {
        if (string.IsNullOrWhiteSpace(editorPaneId))
            return false;
        if (!TryEvent(ev, out var type, out var payload))
            return false;
        if (!string.Equals(type, ProtocolEventTypes.PaneLifecycle, StringComparison.Ordinal))
            return false;
        if (payload.ValueKind != JsonValueKind.Object)
            return false;
        if (!string.Equals(TryString(payload, "pane_id"), editorPaneId, StringComparison.Ordinal))
            return false;

        var state = TryString(payload, "state");
        var action = TryString(payload, "action");
        return IsExitToken(state) || IsExitToken(action);
    }

    private static bool IsExitToken(string? token) =>
        string.Equals(token, PaneLifecycle.Exited, StringComparison.Ordinal)
        || string.Equals(token, PaneLifecycle.Closed, StringComparison.Ordinal);

    private static void LeaveCopyMode(AttachLiveState live)
    {
        _ = live.Engine.CancelInteractiveMode();
        if (live.Engine.Copy.IsSeeded)
            live.Engine.Copy.Reset();
        live.Engine.CommitPaintMode();
    }

    private static void DismissConfirmCloseForEditor(AttachLiveState live, string editorPaneId)
    {
        var pending = live.PendingClosePaneId;
        var focused = live.Dispatcher.PaneId ?? live.PaneId;
        var target = string.IsNullOrWhiteSpace(pending) ? focused : pending;
        if (!string.Equals(target, editorPaneId, StringComparison.Ordinal))
            return;

        var dropLeftover = live.Engine.Mode is AttachClientMode.ConfirmClose;
        AttachSession.ClearPendingCloseTargets(live);
        _ = live.Engine.CancelConfirmClose();
        live.Engine.CommitPaintMode();
        if (dropLeftover)
            live.Engine.ArmDropLeftoverConfirmAccept();
    }

    private static async Task<string> ReadRecentAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        string paneId,
        CancellationToken ct)
    {
        try
        {
            var cols = 80;
            if (live.TryGetPaneFrame(paneId, out var snap) && snap is { Cols: > 0 })
                cols = snap.Cols;
            else if (live.LastComplete is { Cols: > 0 } last)
                cols = last.Cols;
            var result = await control.CallAsync(
                    ProtocolMethods.PaneRead,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["source"] = "recent",
                        ["lines"] = CopyModeBuffer.ResolveSeedRecentLines(cols),
                    },
                    ct)
                .ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? "";
            }
        }
        catch (ControlPlaneException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }

        throw new InvalidOperationException(ReadFailedError);
    }

    private static async Task RestoreZoomAsync(
        IAttachCommandPort control,
        EditScrollbackSession session,
        CancellationToken ct)
    {
        try
        {
            if (session.PreviousZoomed
                && !string.IsNullOrWhiteSpace(session.PreviousZoomedPaneId))
            {
                await control.CallAsync(
                        ProtocolMethods.PaneZoom,
                        new JsonObject
                        {
                            ["pane_id"] = session.PreviousZoomedPaneId,
                            ["mode"] = "on",
                        },
                        ct)
                    .ConfigureAwait(false);
                return;
            }

            var target = session.SourcePaneId;
            if (string.IsNullOrWhiteSpace(target))
                target = session.EditorPaneId;
            await control.CallAsync(
                    ProtocolMethods.PaneZoom,
                    new JsonObject
                    {
                        ["pane_id"] = target,
                        ["mode"] = "off",
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task CloseQuietAsync(
        IAttachCommandPort control,
        string paneId,
        CancellationToken ct)
    {
        try
        {
            await control.CallAsync(
                    ProtocolMethods.PaneClose,
                    new JsonObject { ["pane_id"] = paneId },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool TryEvent(JsonElement ev, out string? type, out JsonElement payload)
    {
        type = null;
        payload = default;
        if (ev.ValueKind != JsonValueKind.Object)
            return false;
        if (ev.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (!p.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                return false;
            type = t.GetString();
            if (p.TryGetProperty("payload", out var pay))
                payload = pay;
            return !string.IsNullOrEmpty(type);
        }

        if (!ev.TryGetProperty("type", out var direct) || direct.ValueKind != JsonValueKind.String)
            return false;
        type = direct.GetString();
        if (ev.TryGetProperty("payload", out var body))
            payload = body;
        return !string.IsNullOrEmpty(type);
    }

    private static string? TryString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
