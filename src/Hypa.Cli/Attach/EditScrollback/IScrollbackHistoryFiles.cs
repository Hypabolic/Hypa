namespace Hypa.Cli.Attach.EditScrollback;

/// <summary>Owner-only temp file for the scrollback editor overlay.</summary>
internal interface IScrollbackHistoryFiles
{
    string WriteUnique(string text);

    void TryDelete(string path);
}
