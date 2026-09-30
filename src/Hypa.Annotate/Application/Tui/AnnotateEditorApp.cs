using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application.Tui;

/// <summary>
// / Editor state.
/// </summary>
public sealed class AnnotateEditorApp
{
    public const string FooterHint = "Ctrl+S save  ·  Esc cancel  ·  Enter new line";

    public string SelectionTitle { get; init; } = "Selected text";

    public string CommentTitle { get; init; } = "Comment";

    public string Hint { get; init; } = "Ctrl+S save  ·  Esc cancel  ·  Enter new line";

    public string EmptyCommentMessage { get; init; } = "Write a comment before saving.";

    public const string SavedStatus = "Saved.";

    public const string EmptyCommentStatus = "Write a comment before saving.";

    public const int InnerCols = 86;

    public const int InnerRows = 22;

    private readonly PendingAnnotation _pending;
    private readonly List<char> _comment = [];

    public AnnotateEditorApp(PendingAnnotation pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        _pending = pending;
    }

    public PendingAnnotation Pending => _pending;

    public string CommentText => new(_comment.ToArray());

    public int Cursor { get; private set; }

    public string Status { get; private set; } = "";

    public bool Quit { get; internal set; }

    public bool Saved { get; private set; }

    public bool Accepted { get; private set; }

    /// <summary>
    /// Handle one key. Returns true when a save should be attempted.
    /// </summary>
    public bool HandleKey(AnnotateKey key)
    {
        Status = "";
        if (key.Control && key.Code == AnnotateKeyCode.Char && key.Character == 'c')
        {
            Quit = true;
            return false;
        }

        if (key.Control && key.Code == AnnotateKeyCode.Char && key.Character == 's')
            return true;

        var action = EditKeys.Resolve(key);
        if (action is { } edit)
        {
            ApplyEdit(edit);
            return false;
        }

        switch (key.Code)
        {
            case AnnotateKeyCode.Esc:
                Quit = true;
                break;
            case AnnotateKeyCode.Backspace:
                if (Cursor > 0)
                {
                    Cursor--;
                    _comment.RemoveAt(Cursor);
                }

                break;
            case AnnotateKeyCode.Delete:
                if (Cursor < _comment.Count)
                    _comment.RemoveAt(Cursor);
                break;
            case AnnotateKeyCode.Left:
                Cursor = Math.Max(0, Cursor - 1);
                break;
            case AnnotateKeyCode.Right:
                Cursor = Math.Min(_comment.Count, Cursor + 1);
                break;
            case AnnotateKeyCode.Up:
                MoveCursorVertical(-1);
                break;
            case AnnotateKeyCode.Down:
                MoveCursorVertical(1);
                break;
            case AnnotateKeyCode.Home:
                while (Cursor > 0 && _comment[Cursor - 1] != '\n')
                    Cursor--;
                break;
            case AnnotateKeyCode.End:
                while (Cursor < _comment.Count && _comment[Cursor] != '\n')
                    Cursor++;
                break;
            case AnnotateKeyCode.Enter:
                Insert('\n');
                break;
            case AnnotateKeyCode.Char when !key.Control && !key.Alt && key.Character != '\0':
                Insert(key.Character);
                break;
        }

        return false;
    }

    public bool TryAcceptComment(out string comment)
    {
        comment = AnnotationParser.JavascriptTrim(CommentText);
        if (Quit && !Accepted)
        {
            comment = "";
            return false;
        }

        if (comment.Length == 0)
        {
            Status = EmptyCommentMessage;
            return false;
        }

        Accepted = true;
        return true;
    }

    public bool TrySave(string stateDirectory)
    {
        if (Quit && !Accepted)
            return false;

        var value = AnnotationParser.JavascriptTrim(CommentText);
        if (value.Length == 0)
        {
            Status = EmptyCommentStatus;
            return false;
        }

        var annotation = new Annotation
        {
            SelectedText = _pending.SelectedText,
            CapturedAt = _pending.CapturedAt,
            Context = _pending.Context,
            Id = Guid.NewGuid().ToString(),
            Comment = value,
            CreatedAt = AnnotateTimestamps.NowIso(),
        };
        var saved = AnnotationStore.AppendAnnotation(stateDirectory, annotation);
        if (!saved.IsOk)
        {
            Status = saved.Error;
            return false;
        }

        Status = SavedStatus;
        Saved = true;
        return true;
    }

    public IReadOnlyList<string> Render(int cols, int rows) => RenderGrid(cols, rows).Rows;

    /// <summary>
    /// </summary>
    internal CellGrid RenderGrid(int cols, int rows)
    {
        cols = Math.Max(20, cols);
        rows = Math.Max(10, rows);
        var grid = CellGrid.Blank(cols, rows);
        var left = 2;
        var innerWidth = Math.Max(1, cols - 4);
        var selectionRows = Math.Clamp((rows - 6) / 2, 3, 7);
        var editorRows = Math.Max(1, rows - selectionRows - 5);
        var wrappedSelection = TerminalWidth.WrapText(
            AnnotateTerminalText.Sanitize(_pending.SelectedText),
            innerWidth);
        grid.Write(left, 1, SelectionTitle, innerWidth, CellPaintStyle.Bold);
        for (var index = 0; index < Math.Min(selectionRows, wrappedSelection.Count); index++)
            grid.Write(left, 2 + index, wrappedSelection[index], innerWidth, CellPaintStyle.Dim);
        if (wrappedSelection.Count > selectionRows)
            grid.Write(left + innerWidth - 1, 1 + selectionRows, "…", 1, CellPaintStyle.Dim);

        var commentTitleRow = 2 + selectionRows;
        grid.Write(left, commentTitleRow, CommentTitle, innerWidth, CellPaintStyle.Bold);
        var editing = TerminalWidth.LayoutComment(_comment, Cursor, innerWidth);
        var editorStart = Math.Max(0, editing.CursorRow - (editorRows - 1));
        for (var index = 0; index < editorRows && editorStart + index < editing.Lines.Count; index++)
            grid.Write(left, commentTitleRow + 1 + index, editing.Lines[editorStart + index], innerWidth);

        var footer = string.IsNullOrEmpty(Status) ? Hint : Status;
        grid.Write(
            left,
            rows - 1,
            TerminalWidth.TruncateToWidth(footer, innerWidth),
            innerWidth,
            CellPaintStyle.Dim);

        var visualRow = editing.CursorRow - editorStart;
        if (editing.CursorRow >= editorStart && visualRow < editorRows)
            grid.SetCursor(left + editing.CursorCol, commentTitleRow + 1 + visualRow);
        return grid;
    }

    private void ApplyEdit(EditAction action)
    {
        switch (action)
        {
            case EditAction.WordLeft:
                Cursor = EditKeys.WordStart(_comment, Cursor);
                break;
            case EditAction.WordRight:
                Cursor = EditKeys.WordEnd(_comment, Cursor);
                break;
            case EditAction.LineStart:
                Cursor = EditKeys.LineStart(_comment, Cursor);
                break;
            case EditAction.LineEnd:
                Cursor = EditKeys.LineEnd(_comment, Cursor);
                break;
            case EditAction.DeleteWord:
                {
                    var start = EditKeys.WordStart(_comment, Cursor);
                    _comment.RemoveRange(start, Cursor - start);
                    Cursor = start;
                    break;
                }
            case EditAction.DeleteLine:
                {
                    var start = EditKeys.LineStart(_comment, Cursor);
                    _comment.RemoveRange(start, Cursor - start);
                    Cursor = start;
                    break;
                }
        }
    }

    private void Insert(char character)
    {
        _comment.Insert(Cursor, character);
        Cursor++;
    }

    private void MoveCursorVertical(int delta)
    {
        var before = new string(_comment.Take(Cursor).ToArray());
        var lines = new string(_comment.ToArray()).Split('\n');
        var row = before.Split('\n').Length - 1;
        var col = TerminalWidth.StringWidth(before.Split('\n')[^1]);
        var targetRow = Math.Clamp(row + delta, 0, Math.Max(0, lines.Length - 1));
        var next = 0;
        for (var i = 0; i < targetRow; i++)
            next += lines[i].Length + 1;
        var used = 0;
        foreach (var character in lines[targetRow])
        {
            var width = TerminalWidth.CharWidth(character);
            if (used + width > col)
                break;
            used += width;
            next++;
        }

        Cursor = next;
    }
}
