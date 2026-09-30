using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach;

/// <summary>
/// Host write for the one-shot selection handoff.
/// The plugin process owns the read.
/// </summary>
public static class SelectionHandoffFiles
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    public static string DefaultPath()
    {
        var baseDirectory = ResolveRuntimeBase();
        return Path.Combine(baseDirectory, "hypa-annotate-" + CurrentUserId(), "selection");
    }

    public static Result<Unit, string> WriteDefault(string? text) =>
        Write(text, DefaultPath());

    public static Result<Unit, string> Write(string? text, string? filePath = null)
    {
        if (IsBlank(text))
            return Result<Unit, string>.Ok(default);

        filePath ??= DefaultPath();
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
                return Result<Unit, string>.Fail("Invalid handoff path");

            EnsurePrivateDirectory(directory);
            WritePrivateFile(filePath, text!);
            return Result<Unit, string>.Ok(default);
        }
        catch (IOException error)
        {
            return Result<Unit, string>.Fail(error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return Result<Unit, string>.Fail(error.Message);
        }
    }

    public static bool IsBlank(string? text)
    {
        var value = text ?? "";
        var start = 0;
        while (start < value.Length && IsTrimChar(value[start]))
            start++;

        return start == value.Length;
    }

    private static bool IsTrimChar(char character) =>
        character == '\uFEFF' || char.IsWhiteSpace(character);

    private static void EnsurePrivateDirectory(string directory)
    {
        if (Directory.Exists(directory))
            return;

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        Directory.CreateDirectory(directory);
    }

    private static void WritePrivateFile(string filePath, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.Read,
        };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(filePath, options))
            stream.Write(bytes);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string ResolveRuntimeBase()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtime))
            return runtime;

        return Path.GetTempPath();
    }

    private static string CurrentUserId()
    {
        if (OperatingSystem.IsWindows())
            return "user";

        try
        {
            return NativeMethods.GetUnixUserId().ToString();
        }
        catch
        {
            return "user";
        }
    }

    private static class NativeMethods
    {
        internal static int GetUnixUserId()
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
                return Interop.GetEffectiveUserId();

            return 0;
        }

        private static class Interop
        {
            [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
            internal static extern uint geteuid();

            internal static int GetEffectiveUserId() => (int)geteuid();
        }
    }
}
