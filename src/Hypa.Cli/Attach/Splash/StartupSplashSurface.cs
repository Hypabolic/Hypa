using System.Diagnostics;
using Hypa.Cli.Attach.Chrome;

namespace Hypa.Cli.Attach.Splash;

/// <summary>
/// Live attach splash. Ticks the cube, then stamps cells into the host frame.
/// </summary>
internal sealed class StartupSplashSurface
{
    private readonly object _gate = new();
    private readonly StartupSplashSession _session = new();
    private readonly CubeSplashComposer _composer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastTickMs;
    private bool _overlayLoadStarted;

    public CubeSplashComposer Composer
    {
        get
        {
            lock (_gate)
                return _composer;
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return !_session.ShouldDismiss(_clock.Elapsed, _composer.OverlayFinished);
        }
    }

    public static StartupSplashSurface Start(bool loadLogoOverlay = true)
    {
        var surface = new StartupSplashSurface();
        if (loadLogoOverlay)
            surface.EnsureOverlayLoad();
        return surface;
    }

    public void Skip()
    {
        lock (_gate)
            _session.Skip();
    }

    public void NoteMuxReady()
    {
        lock (_gate)
            _session.NoteMuxReady();
    }

    public void Tick(int cols, int rows)
    {
        lock (_gate)
        {
            _composer.Resize(cols, rows);
            var now = _clock.ElapsedMilliseconds;
            var dt = _lastTickMs == 0 ? 1f / 60f : (now - _lastTickMs) / 1000f;
            _lastTickMs = now;
            _composer.Tick(dt);
        }
    }

    public void Stamp(IHostCellSink sink, int cols, int rows)
    {
        lock (_gate)
        {
            _composer.Resize(cols, rows);
            if (_lastTickMs == 0)
            {
                _composer.Tick(0);
                _lastTickMs = _clock.ElapsedMilliseconds;
            }

            StartupSplashPainter.Stamp(sink, _composer, cols, rows);
        }
    }

    private void EnsureOverlayLoad()
    {
        if (_overlayLoadStarted)
            return;
        _overlayLoadStarted = true;
        _ = Task.Run(() =>
        {
            TtfxOverlayPlayer? overlay = null;
            try
            {
                overlay = CubeSplashComposer.TryCreateLogoOverlay(out _);
            }
            catch (Exception)
            {
                overlay = null;
            }

            lock (_gate)
                _composer.AttachOverlay(overlay);
        });
    }
}
