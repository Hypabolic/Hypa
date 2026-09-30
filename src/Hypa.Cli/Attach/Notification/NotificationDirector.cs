using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Notify;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach.Notification;

/// <summary>
/// Attach-side notify policy. Status change is the trigger. Delivery paint
/// stays on the client.
/// </summary>
public sealed class NotificationDirector
{
    public const int BlockedToastSeconds = 8;
    public const int DoneToastSeconds = 5;
    public const int NoticeToastSeconds = 3;

    private AttachUiConfig _ui;
    private readonly TimeProvider _time;
    private readonly INotificationSoundPlayer? _sound;
    private readonly ISystemNotifier? _system;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _paneTab = new(StringComparer.Ordinal);
    private readonly HashSet<string> _clientUnseen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingToast> _pending = new(StringComparer.Ordinal);
    private readonly List<LiveToast> _live = [];
    private string? _focusedTabId;
    private string? _focusedPaneId;
    private string? _lastTargetPaneId;
    private DateTimeOffset _lastClipboardAt;
    private string? _lastClipboardText;

    public NotificationDirector(
        AttachUiConfig ui,
        TimeProvider? time = null,
        INotificationSoundPlayer? sound = null,
        ISystemNotifier? system = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _time = time ?? TimeProvider.System;
        _sound = sound;
        _system = system;
    }

    public static NotificationDirector CreateProduction(
        AttachUiConfig ui,
        TimeProvider? time = null) =>
        new(ui, time, new ProcessSoundPlayer(), new UnixSystemNotifier());

    public AttachUiConfig Ui
    {
        get { lock (_gate) return _ui; }
    }

    public void Rebind(AttachUiConfig ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        lock (_gate)
            _ui = ui;
    }

    public INotificationSoundPlayer? Sound => _sound;

    public ISystemNotifier? System => _system;

    public string? LastTargetPaneId
    {
        get { lock (_gate) return _lastTargetPaneId; }
    }

    public IReadOnlyList<string> Osc9 { get; } = new List<string>();

    public IReadOnlyList<(string Kind, string? Path)> SoundsPlayed { get; } = new List<(string, string?)>();

    public IReadOnlyList<(string Title, string Body)> SystemShows { get; } = new List<(string, string)>();

    public IReadOnlyList<string> DrainOsc9()
    {
        lock (_gate)
        {
            var pending = (List<string>)Osc9;
            if (pending.Count == 0)
                return [];
            var copy = pending.ToArray();
            pending.Clear();
            return copy;
        }
    }

    public void SetFocus(string? tabId, string? paneId)
    {
        lock (_gate)
        {
            _focusedTabId = tabId;
            _focusedPaneId = paneId;
            if (!string.IsNullOrWhiteSpace(paneId))
                _clientUnseen.Remove(paneId);
            if (!string.IsNullOrWhiteSpace(tabId))
            {
                foreach (var (pane, paneTab) in _paneTab)
                {
                    if (string.Equals(paneTab, tabId, StringComparison.Ordinal))
                        _clientUnseen.Remove(pane);
                }
            }
        }
    }

    public IReadOnlyList<ToastHit> OnAgentStatusChanged(
        string paneId,
        string? tabId,
        string agentStatus,
        string? agent,
        string? message,
        int cols = 80,
        int rows = 24,
        bool seen = false)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(tabId))
                _paneTab[paneId] = tabId;

            var onActiveTab = !string.IsNullOrWhiteSpace(tabId)
                && string.Equals(tabId, _focusedTabId, StringComparison.Ordinal);
            if (onActiveTab || seen)
                _clientUnseen.Remove(paneId);
            else
                _clientUnseen.Add(paneId);

            var notify = false;
            var sound = NotificationSounds.None;
            if (!onActiveTab && string.Equals(agentStatus, "blocked", StringComparison.Ordinal))
            {
                notify = true;
                sound = NotificationSounds.Request;
            }
            else if (!onActiveTab
                && string.Equals(agentStatus, "done", StringComparison.Ordinal)
                && _clientUnseen.Contains(paneId))
            {
                notify = true;
                sound = NotificationSounds.Done;
            }

            if (!notify)
            {
                _pending.Remove(paneId);
                return SnapshotToastsUnlocked(cols, rows);
            }

            var title = string.Equals(agentStatus, "blocked", StringComparison.Ordinal)
                ? "Agent blocked"
                : "Agent done";
            var body = string.IsNullOrWhiteSpace(message)
                ? agent
                : message;
            EnqueueUnlocked(paneId, tabId, title, body ?? "", sound, agent, cols, rows);
            return SnapshotToastsUnlocked(cols, rows);
        }
    }

    public IReadOnlyList<ToastHit> OnNotificationShown(
        string title,
        string? body,
        string source,
        string sound,
        string? paneId,
        int cols = 80,
        int rows = 24)
    {
        _ = source;
        lock (_gate)
        {
            EnqueueUnlocked(paneId ?? "", tabId: null, title, body ?? "", sound, agent: null, cols, rows);
            return SnapshotToastsUnlocked(cols, rows);
        }
    }

    public IReadOnlyList<ToastHit> OnClipboardCopied(string text, int cols = 80, int rows = 24)
    {
        lock (_gate)
        {
            if (!_ui.Toast.ClipboardEnabled)
                return SnapshotToastsUnlocked(cols, rows);

            var now = _time.GetUtcNow();
            if (string.Equals(text, _lastClipboardText, StringComparison.Ordinal)
                && now - _lastClipboardAt < TimeSpan.FromMilliseconds(250))
            {
                return SnapshotToastsUnlocked(cols, rows);
            }

            _lastClipboardText = text;
            _lastClipboardAt = now;
            ShowUnlocked(
                paneId: "",
                title: "copied",
                body: "",
                sound: NotificationSounds.None,
                agent: null,
                cols,
                rows,
                clipboard: true);
            return SnapshotToastsUnlocked(cols, rows);
        }
    }

    public IReadOnlyList<ToastHit> Tick(int cols = 80, int rows = 24)
    {
        lock (_gate)
        {
            PromotePendingUnlocked(cols, rows);
            DropExpiredUnlocked();
            return SnapshotToastsUnlocked(cols, rows);
        }
    }

    public IReadOnlyList<ToastHit> SnapshotToasts(int cols = 80, int rows = 24)
    {
        lock (_gate)
            return SnapshotToastsUnlocked(cols, rows);
    }

    public static string MuteTableKey(string? agentKind)
    {
        if (string.IsNullOrWhiteSpace(agentKind))
            return "";
        return agentKind.Trim().ToLowerInvariant() switch
        {
            "opencode" or "open_code" => "open_code",
            "copilot" or "github_copilot" => "github_copilot",
            var other => other,
        };
    }

    private void EnqueueUnlocked(
        string paneId,
        string? tabId,
        string title,
        string body,
        string sound,
        string? agent,
        int cols,
        int rows)
    {
        _ = tabId;
        var delay = Math.Max(0, _ui.Toast.DelaySeconds);
        if (delay <= 0)
        {
            ShowUnlocked(paneId, title, body, sound, agent, cols, rows, clipboard: false);
            return;
        }

        _pending[paneId] = new PendingToast(
            paneId,
            title,
            body,
            sound,
            agent,
            _time.GetUtcNow().AddSeconds(delay));
    }

    private void PromotePendingUnlocked(int cols, int rows)
    {
        var now = _time.GetUtcNow();
        List<PendingToast>? due = null;
        foreach (var pending in _pending.Values)
        {
            if (pending.ReadyAt <= now)
            {
                due ??= [];
                due.Add(pending);
            }
        }

        if (due is null)
            return;
        foreach (var item in due)
        {
            _pending.Remove(item.PaneId);
            ShowUnlocked(item.PaneId, item.Title, item.Body, item.Sound, item.Agent, cols, rows, clipboard: false);
        }
    }

    private void ShowUnlocked(
        string paneId,
        string title,
        string body,
        string sound,
        string? agent,
        int cols,
        int rows,
        bool clipboard)
    {
        var delivery = _ui.Toast.Delivery;
        if (delivery == ToastDelivery.Off)
            return;

        // Per-agent mute is a full suppress: no toast, OSC 9, system notice, or sound.
        // Global sound.enabled=false still allows visual delivery.
        if (!clipboard && IsAgentMuted(agent))
            return;

        if (!string.IsNullOrWhiteSpace(paneId))
            _lastTargetPaneId = paneId;

        if (delivery == ToastDelivery.Hypa)
        {
            var position = clipboard ? _ui.Toast.ClipboardPosition : _ui.Toast.HypaPosition;
            var text = string.IsNullOrWhiteSpace(body) ? title : title + " " + body;
            var width = Math.Clamp(Math.Max(8, text.Length + 2), 1, Math.Max(1, cols));
            _live.RemoveAll(t => t.Clipboard == clipboard);
            _live.Add(new LiveToast(
                paneId,
                text,
                position,
                clipboard,
                _time.GetUtcNow() + Lifetime(sound, clipboard)));
            _ = width;
            _ = rows;
        }
        else if (delivery == ToastDelivery.Terminal)
        {
            var osc = $"\u001b]9;{title}{(string.IsNullOrEmpty(body) ? "" : ": " + body)}\u0007";
            ((List<string>)Osc9).Add(osc);
        }
        else if (delivery == ToastDelivery.System)
        {
            ((List<(string, string)>)SystemShows).Add((title, body));
            _ = _system?.ShowAsync(title, body);
        }

        if (delivery != ToastDelivery.Off
            && sound is NotificationSounds.Done or NotificationSounds.Request
            && _ui.Sound.Enabled
            && !IsAgentMuted(agent))
        {
            var path = sound == NotificationSounds.Done
                ? _ui.Sound.DonePath ?? _ui.Sound.Path
                : _ui.Sound.RequestPath ?? _ui.Sound.Path;
            ((List<(string, string?)>)SoundsPlayed).Add((sound, path));
            _ = _sound?.PlayAsync(sound, path);
        }
    }

    private bool IsAgentMuted(string? agent)
    {
        var key = MuteTableKey(agent);
        if (string.IsNullOrEmpty(key))
            return false;
        return _ui.Sound.Agents.TryGetValue(key, out var mode) && mode == SoundAgentMode.Off;
    }

    private IReadOnlyList<ToastHit> SnapshotToastsUnlocked(int cols, int rows)
    {
        PromotePendingUnlocked(cols, rows);
        DropExpiredUnlocked();
        if (_ui.Toast.Delivery != ToastDelivery.Hypa)
            return [];

        var hits = new List<ToastHit>(_live.Count);
        if (NarrowLayout.IsNarrow(cols, _ui.MobileWidthThreshold))
        {
            var header = NarrowLayout.HeaderRowsFor(rows);
            var contentRows = Math.Max(1, rows - header);
            var content = new CellRect(0, header, cols, contentRows);
            var banner = ToastHit.PlaceBanner(content);
            if (_live.Count > 0)
            {
                var latest = _live[^1];
                hits.Add(new ToastHit(latest.PaneId, banner, latest.Text));
            }

            return hits;
        }

        var stack = new Dictionary<ToastPosition, int>();
        foreach (var toast in _live)
        {
            stack.TryGetValue(toast.Position, out var index);
            stack[toast.Position] = index + 1;
            var rect = ToastHit.Place(
                toast.Position,
                cols,
                rows,
                Math.Clamp(toast.Text.Length + 2, 8, Math.Max(8, cols)),
                1,
                index);
            hits.Add(new ToastHit(toast.PaneId, rect, toast.Text));
        }

        return hits;
    }

    private void DropExpiredUnlocked()
    {
        var now = _time.GetUtcNow();
        _live.RemoveAll(t => t.ExpiresAt <= now);
    }

    private static TimeSpan Lifetime(string sound, bool clipboard)
    {
        if (clipboard)
            return TimeSpan.FromSeconds(NoticeToastSeconds);
        if (string.Equals(sound, NotificationSounds.Request, StringComparison.Ordinal))
            return TimeSpan.FromSeconds(BlockedToastSeconds);
        if (string.Equals(sound, NotificationSounds.Done, StringComparison.Ordinal))
            return TimeSpan.FromSeconds(DoneToastSeconds);
        return TimeSpan.FromSeconds(NoticeToastSeconds);
    }

    private sealed record PendingToast(
        string PaneId,
        string Title,
        string Body,
        string Sound,
        string? Agent,
        DateTimeOffset ReadyAt);

    private sealed record LiveToast(
        string PaneId,
        string Text,
        ToastPosition Position,
        bool Clipboard,
        DateTimeOffset ExpiresAt);
}
