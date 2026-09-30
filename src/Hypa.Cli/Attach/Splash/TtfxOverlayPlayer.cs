using HypaCube;
using Ttfx;

namespace Hypa.Cli.Attach.Splash;

/// <summary>
/// Pre-renders a Hypa-TTFX effect, then blits the current frame onto the cube overlay.
/// Copied from HyperCubeDemo Cube.Cli.TtfxOverlayPlayer.
/// </summary>
internal sealed class TtfxOverlayPlayer
{
    private readonly List<OverlayBuffer> _frames;
    private readonly OverlayAnchor _anchor;
    private readonly bool _loop;
    private readonly bool _holdFinal;
    private int _index;
    private bool _finished;
    private double _animClock;
    private const double SourceFps = 60.0;

    private TtfxOverlayPlayer(
        List<OverlayBuffer> frames,
        OverlayAnchor anchor,
        bool loop,
        bool holdFinal)
    {
        _frames = frames;
        _anchor = anchor;
        _loop = loop;
        _holdFinal = holdFinal;
    }

    public int FrameCount => _frames.Count;
    public bool Finished => _finished;

    public static TtfxOverlayPlayer? TryCreate(
        string effect,
        string text,
        ulong? seed,
        bool loop,
        bool holdFinal,
        OverlayAnchor anchor,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(effect))
            return null;

        if (!TextEffects.Exists(effect))
        {
            error = "unknown Hypa-TTFX effect";
            return null;
        }

        var input = text.EndsWith('\n') ? text : text + "\n";
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "overlay text is empty";
            return null;
        }

        IReadOnlyList<string> ansiFrames;
        try
        {
            ansiFrames = TextEffects.Render(
                effect,
                input,
                new TextEffectOptions
                {
                    Seed = seed ?? 42,
                    FrameRate = 60,
                    IgnoreTerminalDimensions = true,
                    CanvasWidth = -1,
                    CanvasHeight = -1,
                    AnchorText = Ttfx.Engine.Anchor.C,
                    AnchorCanvas = Ttfx.Engine.Anchor.C,
                },
                maxFrames: 180);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }

        var parsed = new List<OverlayBuffer>(ansiFrames.Count);
        foreach (var frame in ansiFrames)
            parsed.Add(AnsiFrameParser.Parse(frame));

        if (parsed.Count == 0)
        {
            error = "Hypa-TTFX produced zero frames";
            return null;
        }

        return new TtfxOverlayPlayer(parsed, anchor, loop, holdFinal);
    }

    public void Apply(CubeEngine engine, double dt = 0)
    {
        engine.Overlay.Clear();
        if (_frames.Count == 0)
            return;

        if (dt > 0 && !engine.Options.Paused)
            Advance(dt);

        if (_finished && !_loop && !_holdFinal)
            return;

        var idx = Math.Clamp(_index, 0, _frames.Count - 1);
        var src = _frames[idx];
        var compact = engine.Cells.Cols < 86 || engine.Cells.Rows < 26;
        var hudTop = engine.Options.Hud ? 1 : 0;
        var hudBottom = engine.Options.Hud ? (compact ? 1 : 3) : 0;
        var (ox, oy) = AnsiFrameParser.AnchorOrigin(
            _anchor,
            engine.Cells.Cols,
            engine.Cells.Rows,
            src.Cols,
            src.Rows,
            hudTop,
            hudBottom);
        AnsiFrameParser.Blit(src, engine.Overlay, ox, oy);
    }

    private void Advance(double dt)
    {
        if (_finished && !_loop)
            return;

        _animClock += Math.Max(0, dt);
        var target = (int)(_animClock * SourceFps);
        if (_loop)
        {
            _index = target % _frames.Count;
            return;
        }

        if (target >= _frames.Count)
        {
            _index = _frames.Count - 1;
            _finished = true;
            return;
        }

        _index = target;
    }
}
