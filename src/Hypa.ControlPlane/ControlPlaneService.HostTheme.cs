using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    private HostTerminalTheme _hostTheme = HostTerminalTheme.Empty;
    private long _hostThemeVersion;
    private bool _hostThemeFanoutDirty;

    internal HostTerminalTheme HostTheme
    {
        get
        {
            lock (_gate)
                return _hostTheme;
        }
    }

    internal Task<JsonElement> HandleClientHostThemeSetAsync(
        HostThemeSetParams p,
        CancellationToken ct)
    {
        _ = ct;
        var incoming = FromParams(p);
        HostTerminalTheme stored;
        IPaneRuntime[] runtimes = [];
        IPaneRuntime[] pending = [];
        IPaneRuntime? popup = null;
        var apply = false;
        var version = 0L;
        lock (_gate)
        {
            var next = _hostTheme.Merge(incoming);
            stored = next;
            if (next != _hostTheme || _hostThemeFanoutDirty)
            {
                _hostTheme = next;
                _hostThemeVersion++;
                version = _hostThemeVersion;
                apply = true;
                runtimes = _runtimes.Values.ToArray();
                pending = _pendingRuntimes.Values.ToArray();
                popup = _popup?.Runtime;
            }
        }

        if (apply)
        {
            // terminal runtime, then request_generic. Do not emit terminal.output.
            // Isolate each pane so a disposed VT cannot abort the rest.
            // Replacement occupants live in _pendingRuntimes until StartAsync swaps.
            // Versioned apply rejects a stale snapshot if a newer set completed first.
            var ok = true;
            foreach (var runtime in runtimes)
                ok &= TryApplyHostThemeAndRequestPaint(runtime, stored, version);
            foreach (var runtime in pending)
                ok &= TryApplyHostThemeAndRequestPaint(runtime, stored, version);
            if (popup is not null)
                ok &= TryApplyHostThemeAndRequestPaint(popup, stored, version);
            lock (_gate)
            {
                if (version == _hostThemeVersion)
                    _hostThemeFanoutDirty = !ok;
            }
        }

        return Task.FromResult(OkTyped(ToResult(stored), ProtocolJsonContext.Default.HostThemeSetResult));
    }

    private HostTerminalTheme SnapshotHostTheme()
    {
        lock (_gate)
            return _hostTheme;
    }

    private (HostTerminalTheme Theme, long Version) SnapshotHostThemeVersioned()
    {
        lock (_gate)
            return (_hostTheme, _hostThemeVersion);
    }

    private void ApplyThemeBeforeStart(IPaneRuntime runtime)
    {
        var (theme, version) = SnapshotHostThemeVersioned();
        runtime.ApplyHostTerminalTheme(theme, version);
    }

    private bool TryApplyHostThemeAndRequestPaint(
        IPaneRuntime runtime,
        HostTerminalTheme theme,
        long version)
    {
        try
        {
            runtime.ApplyHostTerminalTheme(theme, version);
            // RequestPty only stores pending; RequestOriginPaint arms the tick.
            if (runtime is IPaneVtSnapshot snap)
                _renderCoalescer.RequestPty(runtime.Id.Value, snap.LastFeedPaintDecision.FeedGeneration);
            _renderCoalescer.RequestOriginPaint(runtime.Id.Value);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Host theme apply failed for {PaneId}", runtime.Id);
            return false;
        }
    }

    internal bool TryPeekRenderPending(string paneId, out long feedGeneration) =>
        _renderCoalescer.TryPeekPending(paneId, out _, out feedGeneration);

    internal Task FlushCoalescedPaintForTestsAsync(string paneId) =>
        _renderCoalescer.FlushPaneNowAsync(paneId);

    internal static HostTerminalTheme FromParams(HostThemeSetParams p)
    {
        HostRgb? fg = null;
        HostRgb? bg = null;
        HostAppearance? appearance = null;
        if (TryRgb(p.Fg, out var parsedFg))
            fg = parsedFg;
        if (TryRgb(p.Bg, out var parsedBg))
            bg = parsedBg;
        if (string.Equals(p.Appearance, "dark", StringComparison.Ordinal))
            appearance = HostAppearance.Dark;
        else if (string.Equals(p.Appearance, "light", StringComparison.Ordinal))
            appearance = HostAppearance.Light;
        return new HostTerminalTheme(fg, bg, appearance)
        {
            Palette = FromPalette(p.Palette),
        };
    }

    internal static HostThemeSetResult ToResult(HostTerminalTheme theme) => new()
    {
        Fg = ToRgb(theme.Foreground),
        Bg = ToRgb(theme.Background),
        Appearance = theme.Appearance switch
        {
            HostAppearance.Dark => "dark",
            HostAppearance.Light => "light",
            _ => null,
        },
        Palette = ToPalette(theme.Palette),
    };

    private static bool TryRgb(HostThemeRgb? rgb, out HostRgb color)
    {
        color = default;
        if (rgb?.R is not { } r || rgb.G is not { } g || rgb.B is not { } b)
            return false;
        if (r is < 0 or > 255 || g is < 0 or > 255 || b is < 0 or > 255)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "fg/bg components must be 0-255");
        }

        color = new HostRgb((byte)r, (byte)g, (byte)b);
        return true;
    }

    private static HostThemeRgb? ToRgb(HostRgb? color) =>
        color is { } c
            ? new HostThemeRgb { R = c.R, G = c.G, B = c.B }
            : null;

    private static HostPalette FromPalette(HostThemePaletteEntry[]? entries)
    {
        if (entries is not { Length: > 0 })
            return default;
        if (entries.Length > HostThemeSetParams.MaxPaletteEntries)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                $"palette must have at most {HostThemeSetParams.MaxPaletteEntries} entries");
        }

        var palette = default(HostPalette);
        foreach (var entry in entries)
        {
            if (entry.I is not { } i || entry.R is not { } r || entry.G is not { } g || entry.B is not { } b)
                continue;
            if (i is < 0 or > 255 || r is < 0 or > 255 || g is < 0 or > 255 || b is < 0 or > 255)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    "palette components must be 0-255");
            }

            palette = palette.WithColor((byte)i, new HostRgb((byte)r, (byte)g, (byte)b));
        }

        return palette;
    }

    private static HostThemePaletteEntry[]? ToPalette(HostPalette palette)
    {
        List<HostThemePaletteEntry>? entries = null;
        for (var i = 0; i < HostPalette.Size; i++)
        {
            if (palette[i] is not { } color)
                continue;
            entries ??= [];
            entries.Add(new HostThemePaletteEntry
            {
                I = i,
                R = color.R,
                G = color.G,
                B = color.B,
            });
        }

        return entries?.ToArray();
    }
}
