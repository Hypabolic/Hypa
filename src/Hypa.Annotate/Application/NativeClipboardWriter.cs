using System.ComponentModel;
using System.Diagnostics;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Per-platform clipboard adapters.
/// A missing command must return a failed result so OSC 52 can still land.
/// </summary>
public class NativeClipboardWriter : IClipboardWriter
{
    private readonly record struct ClipboardCommand(string Command, IReadOnlyList<string> Args);

    public Result<bool, string> TryWrite(string text)
    {
        foreach (var candidate in WriteCommands())
        {
            if (TryWriteWithCommand(candidate, text))
                return Result<bool, string>.Ok(true);
        }

        return Result<bool, string>.Fail("No supported clipboard writer is available");
    }

    private static bool TryWriteWithCommand(ClipboardCommand candidate, string text)
    {
        Process? process = null;
        try
        {
            process = StartProcess(candidate, redirectInput: true);
            if (process is null)
                return false;

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (IsUnavailableCommand(ex))
        {
            return false;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (IsUnavailableCommand(ex))
                {
                }
            }

            process?.Dispose();
        }
    }

    private static Process? StartProcess(ClipboardCommand candidate, bool redirectInput)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = candidate.Command,
                Arguments = candidate.Args.Count == 0 ? string.Empty : string.Join(' ', candidate.Args),
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (OperatingSystem.IsWindows())
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            return Process.Start(startInfo);
        }
        catch (Exception ex) when (IsUnavailableCommand(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// <c>Process.Start</c> raises <see cref="Win32Exception"/> when the binary is
    // / absent.
    /// </summary>
    private static bool IsUnavailableCommand(Exception ex) =>
        ex is IOException or Win32Exception;

    private static IReadOnlyList<ClipboardCommand> WriteCommands()
    {
        if (OperatingSystem.IsMacOS())
            return [new ClipboardCommand("pbcopy", [])];

        if (OperatingSystem.IsWindows())
        {
            return
            [
                new ClipboardCommand(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", "$input | Set-Clipboard"]),
            ];
        }

        return
        [
            new ClipboardCommand("wl-copy", []),
            new ClipboardCommand("xclip", ["-selection", "clipboard", "-in"]),
            new ClipboardCommand("xsel", ["--clipboard", "--input"]),
        ];
    }
}
