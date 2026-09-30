using HypaCube;
using Ttfx;

namespace Hypa.Cli.Attach.Splash;

internal sealed class CubeSplashComposer
{
    public const string FallbackEffect = "wipe";
    public const string LogoResource = "Hypa.Cli.Attach.Splash.hypa-logo.txt";

    private readonly CubeEngine _engine = new();
    private TtfxOverlayPlayer? _overlay;
    private bool _overlayResolved;

    public CubeSplashComposer()
    {
        _engine.Options.Mode = RenderMode.Braille;
        _engine.Options.Theme = Themes.ById("phosphor");
        _engine.Options.Faces = false;
        _engine.Options.AutoRotate = true;
        _engine.Options.Hud = false;
        _engine.Options.Explode = 0.85f;
        _engine.Options.Persist = 0.25f;
        _engine.Options.Zoom = 1f;
        _engine.Resize(80, 24);
    }

    public CubeEngine Engine => _engine;

    /// <summary>
    /// True when overlay load finished and the effect held, or when load failed.
    /// Missing overlay is not finished until <see cref="AttachOverlay"/> runs.
    /// </summary>
    public bool OverlayFinished => _overlayResolved && (_overlay?.Finished ?? true);

    public int OverlayFrameCount => _overlay?.FrameCount ?? 0;

    public void AttachOverlay(TtfxOverlayPlayer? overlay)
    {
        _overlay = overlay;
        _overlayResolved = true;
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == _engine.Cells.Cols && rows == _engine.Cells.Rows)
            return;
        _engine.Resize(cols, rows);
    }

    public void Tick(float dt)
    {
        if (dt < 0)
            dt = 0;
        if (dt > 0.05f)
            dt = 0.05f;
        _engine.Tick(dt);
        _overlay?.Apply(_engine, dt);
    }

    public static string LoadLogoText()
    {
        var assembly = typeof(CubeSplashComposer).Assembly;
        using var stream = assembly.GetManifestResourceStream(LogoResource);
        if (stream is null)
            return "HYPA\n";
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return string.IsNullOrWhiteSpace(text) ? "HYPA\n" : text;
    }

    public static string PickEffect(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var names = TextEffects.Names;
        if (names.Count == 0)
            return FallbackEffect;
        return names[random.Next(names.Count)];
    }

    public static TtfxOverlayPlayer? TryCreateLogoOverlay(out string? error)
    {
        var random = Random.Shared;
        var effect = PickEffect(random);
        var seed = unchecked((ulong)random.Next());
        var overlay = TryCreateLogoOverlay(effect, seed, out error);
        if (overlay is not null)
            return overlay;
        if (string.Equals(effect, FallbackEffect, StringComparison.Ordinal))
            return null;
        return TryCreateLogoOverlay(FallbackEffect, 42, out error);
    }

    public static TtfxOverlayPlayer? TryCreateLogoOverlay(string effect, ulong seed, out string? error) =>
        TtfxOverlayPlayer.TryCreate(
            effect,
            LoadLogoText(),
            seed,
            loop: false,
            holdFinal: true,
            OverlayAnchor.Center,
            out error);
}
