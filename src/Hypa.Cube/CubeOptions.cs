namespace HypaCube;

public sealed class CubeOptions
{
    public RenderMode Mode { get; set; } = RenderMode.Braille;
    public Theme Theme { get; set; } = Themes.All[0];
    public bool AutoRotate { get; set; } = true;
    public bool Faces { get; set; } = false;
    public bool Hud { get; set; } = true;
    public bool Help { get; set; }
    public bool Paused { get; set; }
    public float Explode { get; set; } = 0.85f;
    public float Persist { get; set; } = 0.25f;
    public float Zoom { get; set; } = 1f;
    public float Fps { get; set; } = 60f;
}
