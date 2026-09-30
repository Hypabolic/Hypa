namespace Hypa.Cli.Attach.EditScrollback;

/// <summary>Client overlay for one <c>prefix+e</c> editor pane.</summary>
internal sealed record EditScrollbackSession(
    string SourcePaneId,
    string EditorPaneId,
    string? PreviousFocusPaneId,
    bool PreviousZoomed,
    string? PreviousZoomedPaneId,
    string TempPath);
