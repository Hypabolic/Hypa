using System.Diagnostics;

namespace Hypa.Annotate.Application;

/// <summary>
/// Read text from the first clipboard adapter available on the current platform.
/// </summary>
public sealed class NativeClipboardReader : IClipboardReader
{
    public string ReadText()
    {
        foreach (var candidate in ReadCommands())
        {
            try
            {
                var startInfo = new ProcessStartInfo(candidate.Command)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in candidate.Args)
                    startInfo.ArgumentList.Add(arg);

                using var process = Process.Start(startInfo);
                if (process is null)
                    continue;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                    return output;
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<ClipboardCommand> ReadCommands()
    {
        if (OperatingSystem.IsMacOS())
        {
            return [new ClipboardCommand("pbpaste", [])];
        }

        if (OperatingSystem.IsWindows())
        {
            return
            [
                new ClipboardCommand(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", "Get-Clipboard -Raw"]),
            ];
        }

        return
        [
            new ClipboardCommand("wl-paste", ["--no-newline"]),
            new ClipboardCommand("xclip", ["-selection", "clipboard", "-out"]),
            new ClipboardCommand("xsel", ["--clipboard", "--output"]),
        ];
    }

    private sealed record ClipboardCommand(string Command, IReadOnlyList<string> Args);
}
