using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Keys;

/// <summary>Lists active rows from the live table. Filter is first-class.</summary>
public sealed class KeybindHelpModel
{
    public const string EqualPathsLine =
        "Keyboard and mouse reach the same layout and focus actions.";

    public const string StolenFooterPrefix = "Stolen chords:";

    public static readonly string[] StolenCtrlAlt =
    [
        "ctrl+alt+arrows",
        "ctrl+alt+t",
        "ctrl+alt+l",
        "ctrl+alt+a",
        "ctrl+alt+s",
        "ctrl+alt+u",
        "ctrl+alt+f1",
        "ctrl+alt+f2",
        "ctrl+alt+f3",
        "ctrl+alt+f4",
        "ctrl+alt+f5",
        "ctrl+alt+f6",
        "ctrl+alt+f7",
        "ctrl+alt+f8",
        "ctrl+alt+f9",
        "ctrl+alt+f10",
        "ctrl+alt+f11",
        "ctrl+alt+f12",
    ];

    private readonly List<KeybindHelpRow> _all;
    private readonly bool _showStolenFooter;
    private string _filter = "";

    private KeybindHelpModel(IReadOnlyList<KeybindHelpRow> rows, bool showStolenFooter)
    {
        _all = [.. rows];
        _showStolenFooter = showStolenFooter;
    }

    public bool ShowsStolenFooter => _showStolenFooter;

    public string StolenFooterLine =>
        StolenFooterPrefix + " " + string.Join(", ", StolenCtrlAlt) + ".";

    public bool FilterFocused { get; private set; }

    public string Filter => _filter;

    public IReadOnlyList<KeybindHelpRow> AllRows => _all;

    public IReadOnlyList<KeybindHelpRow> VisibleRows
    {
        get
        {
            var filter = Filter;
            if (filter.Length == 0)
                return _all;
            var list = new List<KeybindHelpRow>();
            foreach (var row in _all)
            {
                if (row.Action.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || row.Chord.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(row);
                }
            }

            return list;
        }
    }

    public string Intro => EqualPathsLine;

    public static KeybindHelpModel FromTable(KeyBindingTable table, bool includeRemote = false)
    {
        ArgumentNullException.ThrowIfNull(table);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<KeybindHelpRow>();
        var showStolenFooter = false;
        AddPrefixRow(table, rows, seen);
        foreach (var row in table.ActiveRows)
        {
            if (row.Action is KeyActionId.Prefix)
                continue;
            if (!includeRemote && KeyActionNames.IsRemoteOnly(row.Action))
                continue;
            if (row.Mode is AttachClientMode.Resize && row.Spec is "h" or "j" or "k" or "l"
                or "up" or "down" or "left" or "right")
            {
                continue;
            }

            if (row.Mode is AttachClientMode.Terminal && row.Chord.Ctrl && row.Chord.Alt)
                showStolenFooter = true;

            var action = FormatAction(table, row);
            var chord = FormatChord(row);
            var key = action + "\t" + chord;
            if (!seen.Add(key))
                continue;
            rows.Add(new KeybindHelpRow(action, chord, row.Mode));
        }

        return new KeybindHelpModel(rows, showStolenFooter);
    }

    private static void AddPrefixRow(
        KeyBindingTable table,
        List<KeybindHelpRow> rows,
        HashSet<string> seen)
    {
        var action = KeyActionNames.Prefix;
        var chord = table.PrefixChord.Format();
        var key = action + "\t" + chord;
        if (!seen.Add(key))
            return;
        rows.Add(new KeybindHelpRow(action, chord, AttachClientMode.Terminal));
    }

    public void FocusFilter() => FilterFocused = true;

    public void AppendFilter(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        FilterFocused = true;
        _filter += text;
    }

    public void Backspace()
    {
        if (_filter.Length == 0)
            return;
        _filter = _filter[..^1];
    }

    public void ClearFilter() => _filter = "";

    public void Reset()
    {
        FilterFocused = false;
        _filter = "";
    }

    public IReadOnlyList<string> RenderLines(int cols)
    {
        var width = Math.Max(1, cols);
        var lines = new List<string>
        {
            Fit(EqualPathsLine, width),
            Fit(FilterFocused ? $"filter: {Filter}_" : "filter: /", width),
        };
        foreach (var row in VisibleRows)
            lines.Add(Fit($"{row.Chord,-22} {row.Action}", width));
        if (_showStolenFooter)
            lines.Add(Fit(StolenFooterLine, width));
        return lines;
    }

    private static string FormatAction(KeyBindingTable table, KeyBindingRow row)
    {
        if (row.Action is not KeyActionId.Command)
            return row.Action.WireName();
        if (row.Index is { } index
            && index >= 0
            && index < table.Commands.Count
            && !string.IsNullOrWhiteSpace(table.Commands[index].Description))
        {
            return table.Commands[index].Description!;
        }

        return "custom command";
    }

    private static string FormatChord(KeyBindingRow row)
    {
        if (row.Mode is AttachClientMode.Prefix)
            return "prefix+" + row.Chord.Format();
        return row.Chord.Format();
    }

    private static string Fit(string text, int cols) =>
        SafeDisplayText.Clip(text, cols);
}

public sealed record KeybindHelpRow(string Action, string Chord, AttachClientMode Mode);
