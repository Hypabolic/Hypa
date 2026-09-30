using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>Chrome primitive sink. Live terminal mode stamps a host frame.</summary>
internal interface IHostCellSink
{
    void Write(
        int col,
        int row,
        string text,
        ThemeColor? fg,
        ThemeColor? bg,
        int maxCols = -1,
        bool inverse = false,
        bool bold = false,
        bool dim = false);

    /// <summary>
    /// host, then paints the modal. ANSI sinks no-op.
    /// </summary>
    void DimAll()
    {
    }
}
