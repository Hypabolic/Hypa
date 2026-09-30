namespace Hypa.AgentRuntime.Domain.AttachConfig;

public enum ChromeGlyphPreset
{
    Unicode,
    Ascii,
    Double,
}

/// <summary>
/// Box and chrome glyphs for one preset. Unicode line set is
/// <c>─│┌┐└┘┬┴├┤┼</c>. Double line set is <c>═║╔╗╚╝╦╩╠╣╬</c>.
/// Ascii line set is <c>-|+++++|</c>.
/// </summary>
public sealed record ChromeGlyphSet(
    ChromeGlyphPreset Preset,
    char H,
    char V,
    char Tl,
    char Tr,
    char Bl,
    char Br,
    char TTee,
    char BTee,
    char LTee,
    char RTee,
    char Cross,
    char SidebarEdge,
    char Close,
    char Scrollbar,
    char ToggleExpand,
    char ToggleCollapse,
    string SelectionMarker,
    string TreeBranch,
    string TreeLast,
    string TreeBar,
    char ScrollUp = '▲',
    char ScrollDown = '▼',
    char ScrollThumb = '█')
{
    public static ChromeGlyphSet Unicode { get; } = new(
        ChromeGlyphPreset.Unicode,
        H: '─',
        V: '│',
        Tl: '┌',
        Tr: '┐',
        Bl: '└',
        Br: '┘',
        TTee: '┬',
        BTee: '┴',
        LTee: '├',
        RTee: '┤',
        Cross: '┼',
        SidebarEdge: '│',
        Close: 'x',
        Scrollbar: '│',
        ToggleExpand: '▸',
        ToggleCollapse: '▾',
        SelectionMarker: "",
        TreeBranch: "├─",
        TreeLast: "└─",
        TreeBar: "│");

    public static ChromeGlyphSet Double { get; } = new(
        ChromeGlyphPreset.Double,
        H: '═',
        V: '║',
        Tl: '╔',
        Tr: '╗',
        Bl: '╚',
        Br: '╝',
        TTee: '╦',
        BTee: '╩',
        LTee: '╠',
        RTee: '╣',
        Cross: '╬',
        SidebarEdge: '║',
        Close: '■',
        Scrollbar: '░',
        ToggleExpand: '►',
        ToggleCollapse: '▼',
        SelectionMarker: "",
        TreeBranch: "╠═",
        TreeLast: "╚═",
        TreeBar: "║",
        ScrollUp: '▲',
        ScrollDown: '▼',
        ScrollThumb: '█');

    public static ChromeGlyphSet Ascii { get; } = new(
        ChromeGlyphPreset.Ascii,
        H: '-',
        V: '|',
        Tl: '+',
        Tr: '+',
        Bl: '+',
        Br: '+',
        TTee: '+',
        BTee: '+',
        LTee: '+',
        RTee: '+',
        Cross: '+',
        SidebarEdge: '|',
        Close: 'x',
        Scrollbar: '|',
        ToggleExpand: '>',
        ToggleCollapse: 'v',
        SelectionMarker: ">",
        TreeBranch: "|-",
        TreeLast: "`-",
        TreeBar: "|",
        ScrollUp: '^',
        ScrollDown: 'v',
        ScrollThumb: '#');

    public static ChromeGlyphSet For(ChromeGlyphPreset preset) =>
        preset switch
        {
            ChromeGlyphPreset.Ascii => Ascii,
            ChromeGlyphPreset.Double => Double,
            _ => Unicode,
        };

    /// <summary>
    // Unmatched
    /// or empty returns <c>\\0</c> so the painter skips the stamp.
    /// </summary>
    public char Resolve(bool up, bool down, bool left, bool right) =>
        (up, down, left, right) switch
        {
            (true, true, true, true) => Cross,
            (true, true, true, false) => RTee,
            (true, true, false, true) => LTee,
            (true, false, true, true) => BTee,
            (false, true, true, true) => TTee,
            (true, true, false, false) or (true, false, false, false) or (false, true, false, false) => V,
            (false, false, true, true) or (false, false, true, false) or (false, false, false, true) => H,
            (false, true, false, true) => Tl,
            (false, true, true, false) => Tr,
            (true, false, false, true) => Bl,
            (true, false, true, false) => Br,
            _ => '\0',
        };
}
