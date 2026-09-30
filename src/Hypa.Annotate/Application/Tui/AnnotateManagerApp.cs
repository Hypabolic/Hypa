using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application.Tui;

/// <summary>
// / Manager state.
/// </summary>
public sealed class AnnotateManagerApp
{
    public const int InnerCols = 98;

    public const int InnerRows = 28;

    public const string EmptyActive = "No active annotations.";

    public const string EmptyArchives = "No archived sets.";

    public const string ActiveFooter =
        "j/k · y copy · c all · Shift+C copy+archive · d delete · Shift+D clear · Tab archives · q";

    public const string ArchiveFooter =
        "j/k · y copy · u restore · d twice delete · Tab active · q";

    public const string ClearConfirmFooter =
        "Press Shift+D again to clear all active annotations · Esc cancel";

    public const string DeleteArchiveConfirmFooter =
        "Press d again to permanently delete this archive · Esc cancel";

    private readonly string _stateDirectory;
    private readonly IClipboardWriter _clipboard;
    private readonly IOsc52Emitter _osc52;
    private List<Annotation> _annotations = [];
    private List<ArchivedAnnotationSet> _archives = [];
    private int _activeSelected;
    private int _archiveSelected;
    private ManagerView _view = ManagerView.Active;
    private ManagerConfirmation _confirmation = ManagerConfirmation.None;
    private string _status = "";
    private string? _deleteArchiveId;

    public AnnotateManagerApp(
        string stateDirectory,
        IClipboardWriter clipboard,
        IOsc52Emitter osc52)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(osc52);
        _stateDirectory = stateDirectory;
        _clipboard = clipboard;
        _osc52 = osc52;
        ReloadActive();
        ReloadArchives();
    }

    public bool Quit { get; internal set; }

    public string Status => _status;

    public ManagerView View => _view;

    public void HandleKey(AnnotateKey key)
    {
        if (key.Control && key.Code == AnnotateKeyCode.Char && key.Character == 'c')
        {
            Quit = true;
            return;
        }

        if (key.Code == AnnotateKeyCode.Esc)
        {
            if (_confirmation == ManagerConfirmation.None)
            {
                Quit = true;
            }
            else
            {
                _confirmation = ManagerConfirmation.None;
                _status = "";
            }

            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'q' && !key.Control)
        {
            Quit = true;
            return;
        }

        if (key.Code == AnnotateKeyCode.Tab)
        {
            SwitchView();
            return;
        }

        _status = "";
        if (_view == ManagerView.Active)
            HandleActiveKey(key);
        else
            HandleArchiveKey(key);
    }

    public IReadOnlyList<string> Render(int cols, int rows)
    {
        cols = Math.Max(50, cols);
        rows = Math.Max(14, rows);
        var grid = CellGrid.Blank(cols, rows);
        var listWidth = Math.Clamp((cols * 36) / 100, 22, 36);
        var detailLeft = listWidth + 2;
        var detailWidth = Math.Max(1, cols - detailLeft - 2);
        var listRows = Math.Max(1, rows - 4);
        for (var row = 1; row < rows - 1; row++)
            grid.Write(listWidth, row, "│", 1);

        if (_view == ManagerView.Active)
            DrawActive(grid, rows, listWidth, detailLeft, detailWidth, listRows);
        else
            DrawArchives(grid, rows, listWidth, detailLeft, detailWidth, listRows);

        grid.Write(1, rows - 1, TerminalWidth.TruncateToWidth(FooterText(), cols - 3), cols - 2);
        return grid.Rows;
    }

    private void DrawActive(
        CellGrid grid,
        int rows,
        int listWidth,
        int detailLeft,
        int detailWidth,
        int listRows)
    {
        grid.Write(1, 0, $"Annotations ({_annotations.Count})  newest first", listWidth - 1);
        if (_annotations.Count == 0)
        {
            grid.Write(1, 2, EmptyActive, listWidth - 1);
            return;
        }

        var first = FirstVisible(_activeSelected, _annotations.Count, listRows);
        for (var index = 0; index < listRows && first + index < _annotations.Count; index++)
        {
            var absolute = first + index;
            var prefix = absolute == _activeSelected ? "› " : "  ";
            var line = prefix + AnnotateTerminalText.ClipForList(
                _annotations[absolute].SelectedText,
                Math.Max(1, listWidth - 4));
            grid.Write(1, 1 + index, line, listWidth - 1);
        }

        var current = _annotations[_activeSelected];
        grid.Write(detailLeft, 1, "Selected text", detailWidth);
        var selectedLines = TerminalWidth.WrapText(
            AnnotateTerminalText.Sanitize(current.SelectedText),
            detailWidth);
        var shownSelection = Math.Min(7, selectedLines.Count);
        for (var index = 0; index < shownSelection; index++)
            grid.Write(detailLeft, 2 + index, selectedLines[index], detailWidth);
        var commentRow = 3 + Math.Max(3, shownSelection);
        grid.Write(detailLeft, commentRow, "Comment", detailWidth);
        var commentLines = TerminalWidth.WrapText(
            AnnotateTerminalText.Sanitize(current.Comment),
            detailWidth);
        var commentRows = Math.Max(1, rows - commentRow - 4);
        for (var index = 0; index < Math.Min(commentRows, commentLines.Count); index++)
            grid.Write(detailLeft, commentRow + 1 + index, commentLines[index], detailWidth);

        var sourceParts = new List<string>();
        if (!string.IsNullOrEmpty(current.Context.WorkspaceLabel))
            sourceParts.Add(current.Context.WorkspaceLabel);
        if (!string.IsNullOrEmpty(current.Context.TabLabel))
            sourceParts.Add(current.Context.TabLabel);
        var source = string.Join(" / ", sourceParts);
        var metadataParts = new List<string>();
        if (source.Length > 0)
            metadataParts.Add(source);
        metadataParts.Add(FormatTimestamp(current.CreatedAt));
        var metadata = string.Join("  ·  ", metadataParts);
        if (metadata.Length > 0)
            grid.Write(detailLeft, rows - 3, TerminalWidth.TruncateToWidth(metadata, detailWidth), detailWidth);
    }

    private void DrawArchives(
        CellGrid grid,
        int rows,
        int listWidth,
        int detailLeft,
        int detailWidth,
        int listRows)
    {
        grid.Write(1, 0, $"Archives ({_archives.Count})  newest first", listWidth - 1);
        if (_archives.Count == 0)
        {
            grid.Write(1, 2, EmptyArchives, listWidth - 1);
            return;
        }

        var first = FirstVisible(_archiveSelected, _archives.Count, listRows);
        for (var index = 0; index < listRows && first + index < _archives.Count; index++)
        {
            var absolute = first + index;
            var prefix = absolute == _archiveSelected ? "› " : "  ";
            var archive = _archives[absolute];
            var label = FormatTimestamp(archive.ArchivedAt) + " · " + CountLabel(archive.Annotations.Count);
            var line = prefix + AnnotateTerminalText.ClipForList(label, Math.Max(1, listWidth - 4));
            grid.Write(1, 1 + index, line, listWidth - 1);
        }

        var current = _archives[_archiveSelected];
        grid.Write(detailLeft, 1, "Archived set", detailWidth);
        grid.Write(detailLeft, 2, FormatTimestamp(current.ArchivedAt), detailWidth);
        grid.Write(detailLeft, 4, CountLabel(current.Annotations.Count), detailWidth);
        var preview = AnnotationStore.NewestFirst(current.Annotations);
        var previewRows = Math.Max(1, rows - 8);
        for (var index = 0; index < Math.Min(previewRows, preview.Count); index++)
        {
            grid.Write(
                detailLeft,
                5 + index,
                AnnotateTerminalText.ClipForList(
                    $"{index + 1}. {preview[index].SelectedText}",
                    detailWidth),
                detailWidth);
        }

        if (preview.Count > previewRows)
        {
            grid.Write(
                detailLeft,
                rows - 3,
                $"… {preview.Count - previewRows} more",
                detailWidth);
        }
    }

    private void HandleActiveKey(AnnotateKey key)
    {
        if (key.Code == AnnotateKeyCode.Char && key.Character == 'D')
        {
            if (_confirmation == ManagerConfirmation.ClearActive)
            {
                ClearActive();
            }
            else
            {
                _confirmation = ManagerConfirmation.ClearActive;
            }

            return;
        }

        _confirmation = ManagerConfirmation.None;
        if (key.Code is AnnotateKeyCode.Up || (key.Code == AnnotateKeyCode.Char && key.Character == 'k'))
        {
            _activeSelected = Math.Max(0, _activeSelected - 1);
            return;
        }

        if (key.Code is AnnotateKeyCode.Down || (key.Code == AnnotateKeyCode.Char && key.Character == 'j'))
        {
            _activeSelected = Math.Min(Math.Max(0, _annotations.Count - 1), _activeSelected + 1);
            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'C')
        {
            CopyAndArchive();
            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'y')
        {
            if (_activeSelected < _annotations.Count)
                Copy([_annotations[_activeSelected]]);
            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'c')
        {
            Copy(_annotations);
            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'd')
        {
            DeleteSelectedAnnotation();
            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'r' && ReloadActive())
            _status = "Reloaded.";
    }

    private void HandleArchiveKey(AnnotateKey key)
    {
        if (key.Code == AnnotateKeyCode.Char && key.Character == 'd')
        {
            if (_archives.Count == 0)
            {
                _confirmation = ManagerConfirmation.None;
                _status = "No archive selected.";
                return;
            }

            var archiveId = _archives[_archiveSelected].Id;
            if (_confirmation == ManagerConfirmation.DeleteArchive
                && string.Equals(_deleteArchiveId, archiveId, StringComparison.Ordinal))
            {
                DeleteSelectedArchive(archiveId);
            }
            else
            {
                _confirmation = ManagerConfirmation.DeleteArchive;
                _deleteArchiveId = archiveId;
            }

            return;
        }

        _confirmation = ManagerConfirmation.None;
        _deleteArchiveId = null;
        if (key.Code is AnnotateKeyCode.Up || (key.Code == AnnotateKeyCode.Char && key.Character == 'k'))
        {
            _archiveSelected = Math.Max(0, _archiveSelected - 1);
            return;
        }

        if (key.Code is AnnotateKeyCode.Down || (key.Code == AnnotateKeyCode.Char && key.Character == 'j'))
        {
            _archiveSelected = Math.Min(Math.Max(0, _archives.Count - 1), _archiveSelected + 1);
            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'y')
        {
            if (_archiveSelected < _archives.Count)
                Copy(AnnotationStore.NewestFirst(_archives[_archiveSelected].Annotations));
            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'u')
        {
            RestoreSelectedArchive();
            return;
        }

        if (key.Code == AnnotateKeyCode.Char && key.Character == 'r' && ReloadArchives())
            _status = "Reloaded.";
    }

    private void Copy(IReadOnlyList<Annotation> items)
    {
        var outcome = AnnotateManagerCopy.CopyAnnotations(
            items,
            text => PaneClipboard.Write(text, _clipboard, _osc52));
        if (outcome.Close)
            Quit = true;
        else
            _status = outcome.Message ?? "Nothing to copy.";
    }

    private void CopyAndArchive()
    {
        var outcome = AnnotateArchiveWorkflow.CopyAndArchive(new CopyAndArchiveRequest
        {
            LoadActive = () => AnnotationStore.LoadAnnotations(_stateDirectory),
            WriteClipboard = text => PaneClipboard.Write(text, _clipboard, _osc52),
            SaveArchive = archive => AnnotationStore.AppendArchivedSet(_stateDirectory, archive),
            RemoveActive = ids => AnnotationStore.RemoveAnnotationsById(_stateDirectory, ids),
            CreateArchiveId = () => Guid.NewGuid().ToString(),
            NowIso = AnnotateTimestamps.NowIso,
        });
        switch (outcome.Kind)
        {
            case CopyAndArchiveKind.Close:
                Quit = true;
                break;
            case CopyAndArchiveKind.ArchivedActiveRetained:
                ReloadArchives();
                _status = "Copied and archived, but active annotations remain: " + outcome.Message;
                break;
            default:
                _status = outcome.Message ?? "Copy and archive failed.";
                break;
        }
    }

    private void DeleteSelectedAnnotation()
    {
        if (_activeSelected >= _annotations.Count)
            return;
        var target = _annotations[_activeSelected];
        var removed = AnnotationStore.RemoveAnnotationsById(_stateDirectory, [target.Id]);
        if (!removed.IsOk)
        {
            _status = removed.Error;
            return;
        }

        _status = "Annotation deleted.";
        ReloadActive();
    }

    private void ClearActive()
    {
        _confirmation = ManagerConfirmation.None;
        var loaded = AnnotationStore.LoadAnnotations(_stateDirectory);
        if (!loaded.IsOk)
        {
            _status = loaded.Error;
            return;
        }

        var ids = loaded.Value.Select(item => item.Id).ToArray();
        var removed = AnnotationStore.RemoveAnnotationsById(_stateDirectory, ids);
        _status = removed.IsOk ? "All active annotations cleared." : removed.Error;
        ReloadActive();
        ReloadArchives();
    }

    private void RestoreSelectedArchive()
    {
        if (_archiveSelected >= _archives.Count)
        {
            _status = "No archive selected.";
            return;
        }

        var target = _archives[_archiveSelected];
        var outcome = AnnotateArchiveWorkflow.RestoreArchivedSet(
            target,
            new RestoreArchiveRequest
            {
                MergeActive = items => AnnotationStore.MergeAnnotations(_stateDirectory, items),
                RemoveArchive = archiveId => AnnotationStore.RemoveArchivedSet(_stateDirectory, archiveId),
            });
        ReloadActive();
        ReloadArchives();
        _status = outcome.Kind switch
        {
            RestoreArchivedSetKind.RestoredArchiveRetained =>
                "Annotations restored, but the archive remains: " + outcome.Message,
            RestoreArchivedSetKind.Restored when outcome.RestoredCount == 0 =>
                "Archive removed; its annotations were already active.",
            RestoreArchivedSetKind.Restored => CountLabel(outcome.RestoredCount) + " restored.",
            _ => outcome.Message ?? "Restore failed.",
        };
    }

    private void DeleteSelectedArchive(string archiveId)
    {
        var removed = AnnotationStore.RemoveArchivedSet(_stateDirectory, archiveId);
        _confirmation = ManagerConfirmation.None;
        _deleteArchiveId = null;
        if (!removed.IsOk)
        {
            _status = removed.Error;
            return;
        }

        ReloadArchives();
        _status = "Archive permanently deleted.";
    }

    private void SwitchView()
    {
        _confirmation = ManagerConfirmation.None;
        _status = "";
        if (_view == ManagerView.Active)
        {
            _view = ManagerView.Archives;
            ReloadArchives();
        }
        else
        {
            _view = ManagerView.Active;
            ReloadActive();
        }
    }

    private bool ReloadActive()
    {
        var loaded = AnnotationStore.LoadAnnotations(_stateDirectory);
        if (!loaded.IsOk)
        {
            _status = loaded.Error;
            return false;
        }

        _annotations = AnnotationStore.NewestFirst(loaded.Value).ToList();
        _activeSelected = Math.Min(_activeSelected, Math.Max(0, _annotations.Count - 1));
        return true;
    }

    private bool ReloadArchives()
    {
        var loaded = AnnotationStore.LoadArchivedSets(_stateDirectory);
        if (!loaded.IsOk)
        {
            _status = loaded.Error;
            return false;
        }

        _archives = AnnotationStore.NewestFirstArchives(loaded.Value).ToList();
        _archiveSelected = Math.Min(_archiveSelected, Math.Max(0, _archives.Count - 1));
        return true;
    }

    private string FooterText()
    {
        if (_confirmation == ManagerConfirmation.ClearActive)
            return ClearConfirmFooter;
        if (_confirmation == ManagerConfirmation.DeleteArchive)
            return DeleteArchiveConfirmFooter;
        if (!string.IsNullOrEmpty(_status))
            return _status;
        return _view == ManagerView.Active ? ActiveFooter : ArchiveFooter;
    }

    private static int FirstVisible(int selected, int length, int visibleRows)
    {
        if (length <= visibleRows)
            return 0;
        return Math.Min(Math.Max(0, selected - (visibleRows / 2)), length - visibleRows);
    }

    private static string CountLabel(int count) =>
        count + (count == 1 ? " annotation" : " annotations");

    private static string FormatTimestamp(string value)
    {
        if (!DateTimeOffset.TryParse(value, out var parsed))
            return "Invalid Date";
        return parsed.ToUniversalTime().ToString("MM/dd/yyyy, hh:mm:ss tt");
    }
}

public enum ManagerView
{
    Active,
    Archives,
}

internal enum ManagerConfirmation
{
    None,
    ClearActive,
    DeleteArchive,
}
