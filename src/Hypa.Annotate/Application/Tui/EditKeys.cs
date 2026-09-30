namespace Hypa.Annotate.Application.Tui;

/// <summary>
// / Word and line edit actions.
/// </summary>
public enum EditAction
{
    WordLeft,
    WordRight,
    LineStart,
    LineEnd,
    DeleteWord,
    DeleteLine,
}

public static class EditKeys
{
    public static bool IsJavascriptWhitespace(char character) =>
        character is >= '\u0009' and <= '\u000d'
            or '\u0020'
            or '\u00a0'
            or '\u1680'
            or (>= '\u2000' and <= '\u200a')
            or '\u2028'
            or '\u2029'
            or '\u202f'
            or '\u205f'
            or '\u3000'
            or '\ufeff';

    public static int WordStart(IReadOnlyList<char> chars, int cursor)
    {
        var index = Math.Min(cursor, chars.Count);
        while (index > 0 && !IsWordChar(chars, index - 1) && !IsNewline(chars, index - 1))
            index--;
        if (index > 0 && IsNewline(chars, index - 1) && index == cursor)
            return index - 1;
        while (index > 0 && IsWordChar(chars, index - 1))
            index--;
        return index;
    }

    public static int WordEnd(IReadOnlyList<char> chars, int cursor)
    {
        var index = cursor;
        while (index < chars.Count && !IsWordChar(chars, index) && !IsNewline(chars, index))
            index++;
        if (index < chars.Count && IsNewline(chars, index) && index == cursor)
            return index + 1;
        while (index < chars.Count && IsWordChar(chars, index))
            index++;
        return index;
    }

    public static int LineStart(IReadOnlyList<char> chars, int cursor)
    {
        var index = cursor;
        while (index > 0 && !IsNewline(chars, index - 1))
            index--;
        return index;
    }

    public static int LineEnd(IReadOnlyList<char> chars, int cursor)
    {
        var index = cursor;
        while (index < chars.Count && !IsNewline(chars, index))
            index++;
        return index;
    }

    public static EditAction? Resolve(AnnotateKey key)
    {
        if (key.Control && key.Code == AnnotateKeyCode.Char && key.Character == 'u')
            return EditAction.DeleteLine;
        if (key.Control && key.Code == AnnotateKeyCode.Char && key.Character == 'w')
            return EditAction.DeleteWord;
        if (key.Alt && key.Code == AnnotateKeyCode.Backspace)
            return EditAction.DeleteWord;
        if (key.Control && key.Code == AnnotateKeyCode.Char && key.Character == 'a')
            return EditAction.LineStart;
        if (key.Control && key.Code == AnnotateKeyCode.Char && key.Character == 'e')
            return EditAction.LineEnd;
        if (key.Alt && key.Code == AnnotateKeyCode.Char && key.Character == 'b')
            return EditAction.WordLeft;
        if (key.Alt && key.Code == AnnotateKeyCode.Char && key.Character == 'f')
            return EditAction.WordRight;
        if (key.Super && key.Code == AnnotateKeyCode.Left)
            return EditAction.LineStart;
        if (key.Super && key.Code == AnnotateKeyCode.Right)
            return EditAction.LineEnd;
        if ((key.Alt || key.Control) && key.Code == AnnotateKeyCode.Left)
            return EditAction.WordLeft;
        if ((key.Alt || key.Control) && key.Code == AnnotateKeyCode.Right)
            return EditAction.WordRight;
        return null;
    }

    private static bool IsWordChar(IReadOnlyList<char> chars, int index)
    {
        if (index < 0 || index >= chars.Count)
            return false;
        var character = chars[index];
        return character != '\n' && !IsJavascriptWhitespace(character);
    }

    private static bool IsNewline(IReadOnlyList<char> chars, int index) =>
        index >= 0 && index < chars.Count && chars[index] == '\n';
}
