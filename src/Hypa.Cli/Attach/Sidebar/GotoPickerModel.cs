using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;

namespace Hypa.Cli.Attach.Sidebar;

/// <summary>Client-local goto overlay. Filter does not reach the child pane.</summary>
public sealed class GotoPickerModel
{
    private IReadOnlyList<GotoTarget> _catalog = [];
    private string _filter = "";
    private int _selected;

    public bool FilterFocused { get; private set; }

    public bool WorkspaceOnly { get; set; }

    public bool TransferMode { get; set; }

    public string CatalogReason { get; set; } = "";

    public string Filter => _filter;

    public IReadOnlyList<GotoMatch> Visible => GotoTargetMatcher.Match(_catalog, _filter);

    public GotoMatch? SelectedMatch
    {
        get
        {
            var rows = Visible;
            if (rows.Count == 0)
                return null;
            var i = Math.Clamp(_selected, 0, rows.Count - 1);
            return rows[i];
        }
    }

    public bool CatalogKeysEqual(IReadOnlyList<GotoTarget> catalog)
    {
        catalog ??= [];
        if (_catalog.Count != catalog.Count)
            return false;
        for (var i = 0; i < _catalog.Count; i++)
        {
            if (_catalog[i].Kind != catalog[i].Kind
                || !string.Equals(_catalog[i].Id, catalog[i].Id, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public void SetCatalog(IReadOnlyList<GotoTarget> catalog)
    {
        var keep = SelectedMatch?.Target;
        _catalog = catalog ?? [];
        _selected = IndexOfTarget(keep);
    }

    public void FocusFilter() => FilterFocused = true;

    public void AppendFilter(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        FilterFocused = true;
        _filter += text;
        _selected = 0;
    }

    public void Backspace()
    {
        if (_filter.Length == 0)
            return;
        _filter = _filter[..^1];
        _selected = 0;
    }

    public void ClearFilter()
    {
        _filter = "";
        _selected = 0;
        FilterFocused = false;
    }

    public void Move(int delta)
    {
        var count = Visible.Count;
        if (count == 0)
            return;
        var next = _selected + delta;
        if (next < 0)
            next = count - 1;
        else if (next >= count)
            next = 0;
        _selected = next;
    }

    public void Reset()
    {
        FilterFocused = false;
        _filter = "";
        _selected = 0;
        _catalog = [];
        WorkspaceOnly = false;
        TransferMode = false;
        CatalogReason = "";
    }

    public IReadOnlyList<string> RenderLines(int cols)
    {
        var width = Math.Max(1, cols);
        var header = TransferMode
            ? "TRANSFER  enter pick  esc leave"
            : WorkspaceOnly
                ? "WORKSPACE  enter jump  esc leave"
                : "GOTO  enter jump  esc leave";
        var lines = new List<string>
        {
            Fit(header, width),
            Fit(FilterFocused ? $"filter: {Filter}_" : "filter: /", width),
        };
        var rows = Visible;
        for (var i = 0; i < rows.Count; i++)
        {
            var mark = i == Math.Clamp(_selected, 0, Math.Max(0, rows.Count - 1)) ? ">" : " ";
            var row = rows[i];
            lines.Add(Fit(TransferMode ? TransferRow(mark, row.Target) : $"{mark} {row.Target.Kind,-10} {row.Target.Label} ({row.Target.Id})", width));
        }

        if (rows.Count == 0)
        {
            var empty = TransferMode && !string.IsNullOrWhiteSpace(CatalogReason)
                ? CatalogReason
                : "  no matches";
            lines.Add(Fit(empty, width));
        }

        return lines;
    }

    private static string TransferRow(string mark, GotoTarget target)
    {
        var name = string.IsNullOrWhiteSpace(target.Label) ? target.Id : target.Label;
        var kind = string.IsNullOrWhiteSpace(target.KindText) ? "" : "  " + target.KindText;
        var reach = string.IsNullOrWhiteSpace(target.ReachabilityText)
            ? ""
            : "  " + target.ReachabilityText;
        return $"{mark} {name}{kind}{reach}";
    }

    private int IndexOfTarget(GotoTarget? target)
    {
        if (target is null)
            return 0;
        var rows = Visible;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Target.Kind == target.Kind
                && string.Equals(rows[i].Target.Id, target.Id, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    private static string Fit(string text, int cols) =>
        SafeDisplayText.Clip(text, cols);
}
