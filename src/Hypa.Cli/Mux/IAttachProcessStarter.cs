using System.Diagnostics;

namespace Hypa.Cli.Mux;

/// <summary>
/// Process-start seam for Connect. Connect must not spawn a pane process.
/// </summary>
public interface IAttachProcessStarter
{
    int StartCount { get; }

    IReadOnlyList<string> StartedCommands { get; }

    Process? Start(ProcessStartInfo startInfo);
}

/// <summary>
/// Records <see cref="Process.Start"/> calls. The default start path refuses spawn.
/// </summary>
public sealed class ObservingProcessStarter : IAttachProcessStarter
{
    private readonly List<string> _started = [];
    private readonly Func<ProcessStartInfo, Process?> _start;

    public ObservingProcessStarter(Func<ProcessStartInfo, Process?>? start = null)
    {
        _start = start ?? ForbiddenStart;
    }

    public int StartCount => _started.Count;

    public IReadOnlyList<string> StartedCommands => _started;

    public Process? Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _started.Add(FormatCommand(startInfo));
        return _start(startInfo);
    }

    public static string FormatCommand(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var file = startInfo.FileName ?? string.Empty;
        if (startInfo.ArgumentList.Count > 0)
            return file + " " + string.Join(" ", startInfo.ArgumentList);
        if (!string.IsNullOrEmpty(startInfo.Arguments))
            return file + " " + startInfo.Arguments;
        return file;
    }

    private static Process? ForbiddenStart(ProcessStartInfo startInfo)
    {
        throw new InvalidOperationException("Connect does not spawn a pane process.");
    }
}
