namespace Hypa.Cli.Attach.Mouse;

public interface IMouseForwarder
{
    bool OwnsContent(string? mouseMode);
}

public sealed class ChildMouseForwarder : IMouseForwarder
{
    public static ChildMouseForwarder Instance { get; } = new();

    public bool OwnsContent(string? mouseMode) =>
        !string.IsNullOrEmpty(mouseMode)
        && !string.Equals(mouseMode, "none", StringComparison.OrdinalIgnoreCase);
}

public sealed class ChromeMouseForwarder : IMouseForwarder
{
    public static ChromeMouseForwarder Instance { get; } = new();

    public bool OwnsContent(string? mouseMode)
    {
        _ = mouseMode;
        return false;
    }
}
