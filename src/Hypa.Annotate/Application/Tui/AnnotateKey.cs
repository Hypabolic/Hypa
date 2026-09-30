namespace Hypa.Annotate.Application.Tui;

public enum AnnotateKeyCode
{
    Char,
    Esc,
    Enter,
    Backspace,
    Delete,
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    Tab,
    Other,
}

public readonly record struct AnnotateKey(
    AnnotateKeyCode Code,
    char Character,
    bool Control,
    bool Alt,
    bool Super);
