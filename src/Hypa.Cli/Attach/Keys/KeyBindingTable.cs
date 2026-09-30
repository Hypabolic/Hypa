using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach.Keys;

/// <summary>Compiled lookup: (mode, chord) → <see cref="KeyActionId"/> + optional index.</summary>
public sealed class KeyBindingTable
{
    private readonly Dictionary<(AttachClientMode Mode, string Chord), BoundAction> _map;
    private readonly List<KeyBindingRow> _rows;

    private KeyBindingTable(
        KeyChord prefixChord,
        byte[] prefixBytes,
        Dictionary<(AttachClientMode Mode, string Chord), BoundAction> map,
        List<KeyBindingRow> rows,
        IReadOnlyList<KeyCommandBinding> commands)
    {
        PrefixChord = prefixChord;
        PrefixBytes = prefixBytes;
        _map = map;
        _rows = rows;
        Commands = commands;
    }

    public KeyChord PrefixChord { get; }

    public byte[] PrefixBytes { get; }

    public IReadOnlyList<KeyCommandBinding> Commands { get; }

    public byte PrefixByte => PrefixBytes.Length > 0 ? PrefixBytes[0] : (byte)0x02;

    public IReadOnlyList<KeyBindingRow> ActiveRows => _rows;

    public static Result<KeyBindingTable, KeyCompileError> Compile(KeysConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Prefix.IsUnset || config.Prefix.Primary is null)
        {
            return Result<KeyBindingTable, KeyCompileError>.Fail(
                new KeyCompileError(KeyCompileError.MissingPrefix, "keys.prefix is required"));
        }

        var prefixResult = KeyChord.TryParse(config.Prefix.Primary);
        if (!prefixResult.IsOk || prefixResult.Value.Prefix)
        {
            return Result<KeyBindingTable, KeyCompileError>.Fail(
                new KeyCompileError(KeyCompileError.InvalidChord, "keys.prefix is not a valid chord"));
        }

        var prefixChord = prefixResult.Value;
        var map = new Dictionary<(AttachClientMode, string), BoundAction>();
        var rows = new List<KeyBindingRow>();

        foreach (var (action, spec) in config.EnumerateActions())
        {
            if (spec.IsUnset)
                continue;

            if (!spec.TryExpand(out var expanded, out var expandError))
            {
                return Result<KeyBindingTable, KeyCompileError>.Fail(
                    new KeyCompileError(KeyCompileError.InvalidChord, $"{action.WireName()}: {expandError}"));
            }

            for (var i = 0; i < expanded.Count; i++)
            {
                var raw = expanded[i];
                var parsed = KeyChord.TryParse(raw);
                if (!parsed.IsOk)
                {
                    return Result<KeyBindingTable, KeyCompileError>.Fail(
                        new KeyCompileError(KeyCompileError.InvalidChord, $"{action.WireName()}: {parsed.Error}"));
                }

                var chord = parsed.Value;
                var index = NeedsIndex(action, spec) ? IndexFromChord(chord, i) : (int?)null;
                var error = Bind(map, rows, action, chord, raw, index);
                if (error is not null)
                    return Result<KeyBindingTable, KeyCompileError>.Fail(error);
            }
        }

        for (var c = 0; c < config.Commands.Count; c++)
        {
            var command = config.Commands[c];
            if (command.Key.IsUnset)
                continue;
            if (!command.Key.TryExpand(out var commandExpanded, out var commandExpandError))
            {
                return Result<KeyBindingTable, KeyCompileError>.Fail(
                    new KeyCompileError(KeyCompileError.InvalidChord, $"command: {commandExpandError}"));
            }

            foreach (var raw in commandExpanded)
            {
                var parsed = KeyChord.TryParse(raw);
                if (!parsed.IsOk)
                {
                    return Result<KeyBindingTable, KeyCompileError>.Fail(
                        new KeyCompileError(KeyCompileError.InvalidChord, $"command: {parsed.Error}"));
                }

                var error = Bind(map, rows, KeyActionId.Command, parsed.Value, raw, c);
                if (error is not null)
                    return Result<KeyBindingTable, KeyCompileError>.Fail(error);
            }
        }

        AddNavigateAlias(map, rows, "left", KeyActionId.NavigatePaneLeft);
        AddNavigateAlias(map, rows, "right", KeyActionId.NavigatePaneRight);
        AddResizeLocals(map, rows);

        if (!KeyEventDecoder.TryEncode(prefixChord, out var prefixBytes) || prefixBytes.Length == 0)
        {
            return Result<KeyBindingTable, KeyCompileError>.Fail(
                new KeyCompileError(
                    KeyCompileError.InvalidChord,
                    "keys.prefix cannot be encoded as a TTY sequence"));
        }

        return Result<KeyBindingTable, KeyCompileError>.Ok(
            new KeyBindingTable(prefixChord, prefixBytes, map, rows, config.Commands));
    }

    public static KeyBindingTable CompileOrThrow(KeysConfig config)
    {
        var result = Compile(config);
        if (!result.IsOk)
            throw new InvalidOperationException(result.Error.ToString());
        return result.Value;
    }

    public bool TryLookup(AttachClientMode mode, KeyChord chord, out BoundAction bound)
    {
        var key = LookupKey(chord.WithoutPrefix());
        return _map.TryGetValue((mode, key), out bound);
    }

    public bool TryLookup(AttachClientMode mode, bool prefixArmed, KeyChord chord, out BoundAction bound)
    {
        var lookupMode = prefixArmed ? AttachClientMode.Prefix : mode;
        return TryLookup(lookupMode, chord, out bound);
    }

    public bool HasAction(KeyActionId action)
    {
        foreach (var row in _rows)
        {
            if (row.Action == action)
                return true;
        }

        return false;
    }

    public IReadOnlyList<KeyBindingRow> RowsFor(KeyActionId action)
    {
        var list = new List<KeyBindingRow>();
        foreach (var row in _rows)
        {
            if (row.Action == action)
                list.Add(row);
        }

        return list;
    }

    private static KeyCompileError? Bind(
        Dictionary<(AttachClientMode, string), BoundAction> map,
        List<KeyBindingRow> rows,
        KeyActionId action,
        KeyChord chord,
        string spec,
        int? index)
    {
        if (KeyActionNames.IsNavigate(action))
        {
            var navError = ValidateNavigate(action, chord);
            if (navError is not null)
                return navError;
            return BindWithShiftDigitAlias(
                map, rows, AttachClientMode.Navigate, chord.WithoutPrefix(), action, spec, index);
        }

        if (chord.Prefix)
        {
            return BindWithShiftDigitAlias(
                map,
                rows,
                AttachClientMode.Prefix,
                chord.WithoutPrefix(),
                action,
                spec,
                index);
        }

        return BindWithShiftDigitAlias(map, rows, AttachClientMode.Terminal, chord, action, spec, index);
    }

    private static KeyCompileError? BindWithShiftDigitAlias(
        Dictionary<(AttachClientMode, string), BoundAction> map,
        List<KeyBindingRow> rows,
        AttachClientMode mode,
        KeyChord chord,
        KeyActionId action,
        string spec,
        int? index)
    {
        var error = Put(map, rows, mode, chord, action, spec, index);
        if (error is not null)
            return error;
        if (!TryUsShiftDigitGlyph(chord, out var glyph))
            return null;
        return Put(map, rows, mode, glyph, action, spec, index);
    }

    // Stock TTY sends Shift+1 as '!'. Bind the US glyph with Shift cleared.
    private static bool TryUsShiftDigitGlyph(KeyChord chord, out KeyChord glyph)
    {
        glyph = chord;
        if (!chord.Shift || chord.Key.Length != 1)
            return false;
        var digit = chord.Key[0];
        if (digit is < '1' or > '9')
            return false;
        glyph = chord with { Shift = false, Key = UsShiftDigitGlyphs[digit - '1'].ToString() };
        return true;
    }

    private static KeyCompileError? Put(
        Dictionary<(AttachClientMode, string), BoundAction> map,
        List<KeyBindingRow> rows,
        AttachClientMode mode,
        KeyChord chord,
        KeyActionId action,
        string spec,
        int? index)
    {
        var key = (mode, LookupKey(chord));
        if (map.TryGetValue(key, out var existing)
            && (existing.Action != action || action == KeyActionId.Command))
        {
            return new KeyCompileError(
                KeyCompileError.DuplicateBinding,
                $"{spec} already binds {existing.Action.WireName()}");
        }

        map[key] = new BoundAction(action, index);
        rows.Add(new KeyBindingRow(action, chord, mode, index, spec));
        return null;
    }

    private static void AddNavigateAlias(
        Dictionary<(AttachClientMode, string), BoundAction> map,
        List<KeyBindingRow> rows,
        string key,
        KeyActionId action)
    {
        var chord = new KeyChord(false, false, false, false, key);
        var lookup = (AttachClientMode.Navigate, LookupKey(chord));
        if (map.ContainsKey(lookup))
            return;
        map[lookup] = new BoundAction(action, null);
        rows.Add(new KeyBindingRow(action, chord, AttachClientMode.Navigate, null, key));
    }

    private static void AddResizeLocals(
        Dictionary<(AttachClientMode, string), BoundAction> map,
        List<KeyBindingRow> rows)
    {
        BindResize(map, rows, "h", KeyActionId.ResizePaneLeft);
        BindResize(map, rows, "left", KeyActionId.ResizePaneLeft);
        BindResize(map, rows, "j", KeyActionId.ResizePaneDown);
        BindResize(map, rows, "down", KeyActionId.ResizePaneDown);
        BindResize(map, rows, "k", KeyActionId.ResizePaneUp);
        BindResize(map, rows, "up", KeyActionId.ResizePaneUp);
        BindResize(map, rows, "l", KeyActionId.ResizePaneRight);
        BindResize(map, rows, "right", KeyActionId.ResizePaneRight);
    }

    private static void BindResize(
        Dictionary<(AttachClientMode, string), BoundAction> map,
        List<KeyBindingRow> rows,
        string key,
        KeyActionId action)
    {
        var chord = new KeyChord(false, false, false, false, key);
        var lookup = (AttachClientMode.Resize, LookupKey(chord));
        if (map.ContainsKey(lookup))
            return;
        map[lookup] = new BoundAction(action, null);
        rows.Add(new KeyBindingRow(action, chord, AttachClientMode.Resize, null, key));
    }

    private static KeyCompileError? ValidateNavigate(KeyActionId action, KeyChord chord)
    {
        if (chord.Prefix)
        {
            return new KeyCompileError(
                KeyCompileError.NavigateForbidden,
                $"{action.WireName()} must not use prefix+");
        }

        if (chord.IsEscape)
        {
            return new KeyCompileError(
                KeyCompileError.NavigateForbidden,
                $"{action.WireName()} must not use esc");
        }

        if (chord.IsEnter)
        {
            return new KeyCompileError(
                KeyCompileError.NavigateForbidden,
                $"{action.WireName()} must not use enter");
        }

        if (chord.IsTab || chord.IsShiftTab)
        {
            return new KeyCompileError(
                KeyCompileError.NavigateForbidden,
                $"{action.WireName()} must not use tab");
        }

        if (chord.IsUnmodifiedDigit)
        {
            return new KeyCompileError(
                KeyCompileError.NavigateForbidden,
                $"{action.WireName()} must not use unmodified 1-9");
        }

        if (chord.IsLeftOrRightArrow
            && action is KeyActionId.NavigateWorkspaceUp or KeyActionId.NavigateWorkspaceDown)
        {
            return new KeyCompileError(
                KeyCompileError.NavigateForbidden,
                $"{action.WireName()} cannot steal left/right arrows");
        }

        return null;
    }

    private static bool NeedsIndex(KeyActionId action, BindingSpec spec)
    {
        if (action is KeyActionId.SwitchTab
            or KeyActionId.SwitchWorkspace
            or KeyActionId.IndexedTabs
            or KeyActionId.IndexedWorkspaces
            or KeyActionId.IndexedAgents
            or KeyActionId.FocusAgent)
        {
            return true;
        }

        foreach (var item in spec.Specs)
        {
            if (AttachKeySpec.IsCompleteDigitRange(item))
                return true;
        }

        return false;
    }

    private static int IndexFromChord(KeyChord chord, int fallback)
    {
        if (chord.Key.Length == 1 && chord.Key[0] is >= '1' and <= '9')
            return chord.Key[0] - '0';
        return fallback + 1;
    }

    private static string LookupKey(KeyChord chord) =>
        $"{(chord.Ctrl ? "c" : "")}{(chord.Alt ? "a" : "")}{(chord.Shift ? "s" : "")}:{chord.Key}";

    private const string UsShiftDigitGlyphs = "!@#$%^&*(";
}
