using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// One-shot selection handoff for remote sessions.
/// </summary>
public static class SelectionHandoff
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <c>$XDG_RUNTIME_DIR</c> when set, else the system temp dir, plus the uid.
    /// </summary>
    public static string DefaultHandoffPath()
    {
        var baseDirectory = ResolveRuntimeBase();
        return Path.Combine(baseDirectory, $"hypa-annotate-{CurrentUserId()}", "selection");
    }

    /// <summary>
    /// Return fresh, non-blank handed-off text and remove the file whether fresh or stale.
    /// </summary>
    public static Result<string?, string> TakeHandoff(
        string filePath,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        if (!File.Exists(filePath))
            return Result<string?, string>.Ok(null);

        try
        {
            var metadata = new FileInfo(filePath);
            if (!metadata.Exists || metadata.Attributes.HasFlag(FileAttributes.Directory))
            {
                TryRemove(filePath);
                return Result<string?, string>.Ok(null);
            }

            var age = now.UtcDateTime - metadata.LastWriteTimeUtc;
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;
            var fresh = age <= maxAge;
            string? text = null;
            if (fresh)
            {
                var bytes = File.ReadAllBytes(filePath);
                text = System.Text.Encoding.UTF8.GetString(bytes);
            }

            TryRemove(filePath);
            if (text is null || AnnotationParser.JavascriptTrim(text).Length == 0)
                return Result<string?, string>.Ok(null);

            return Result<string?, string>.Ok(text);
        }
        catch (IOException error)
        {
            return Result<string?, string>.Fail(error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return Result<string?, string>.Fail(error.Message);
        }
    }

    public static Result<string?, string> TakeDefaultHandoff(DateTimeOffset now) =>
        TakeHandoff(DefaultHandoffPath(), now, MaxAge);

    /// <summary>
    // / Write handed-off selection text.
    /// Blank text writes no file.
    /// </summary>
    public static Result<Unit, string> WriteHandoff(string? text, string? filePath = null)
    {
        if (AnnotationParser.JavascriptTrim(text ?? "").Length == 0)
            return Result<Unit, string>.Ok(default);

        filePath ??= DefaultHandoffPath();
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

    public static Result<Unit, string> WriteDefaultHandoff(string? text) =>
        WriteHandoff(text, DefaultHandoffPath());

    public static bool IsBlankHandoffText(string? text) =>
        AnnotationParser.JavascriptTrim(text ?? "").Length == 0;

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

        using var stream = new FileStream(filePath, options);
        stream.Write(bytes);
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

    private static void TryRemove(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (FileNotFoundException)
        {
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
