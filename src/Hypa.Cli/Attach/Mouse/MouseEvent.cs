namespace Hypa.Cli.Attach.Mouse;

public enum MouseButton
{
    None = 0,
    Left,
    Middle,
    Right,
    WheelUp,
    WheelDown,
}

public enum MouseAction
{
    Press,
    Release,
    Drag,
    Wheel,
    Move,
}

public sealed record MouseEvent(
    MouseButton Button,
    MouseAction Action,
    int Col,
    int Row,
    bool Shift = false,
    bool Alt = false,
    bool Ctrl = false)
{
    public bool IsWheel =>
        Action is MouseAction.Wheel
        || Button is MouseButton.WheelUp or MouseButton.WheelDown;

    public bool HasModifier(string? name) =>
        name?.Trim().ToLowerInvariant() switch
        {
            "shift" => Shift,
            "alt" => Alt,
            "ctrl" or "control" => Ctrl,
            _ => false,
        };
}
