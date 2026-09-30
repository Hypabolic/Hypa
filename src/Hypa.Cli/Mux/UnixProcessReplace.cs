using System.Collections;
using System.Runtime.InteropServices;

namespace Hypa.Cli.Mux;

/// <summary>
// / Replace this process image.
/// roles of one binary. Hypa keeps two OS processes and may exec a lean
/// attach image into the same pid so the profile tracks attach.pid.
/// </summary>
internal static class UnixProcessReplace
{
    /// <summary>
    /// Returns true only when the process image was replaced. <c>execve</c>
    /// does not return on success, so a true return is unreachable in
    /// practice. Failure returns false and writes errno to
    /// <paramref name="errno"/>.
    /// </summary>
    public static bool TryExec(string path, string[] args) =>
        TryExec(path, args, out _);

    internal static bool TryExec(string path, string[] args, out int errno)
    {
        errno = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return false;

        var argv = new string[args.Length + 2];
        argv[0] = path;
        if (args.Length > 0)
            Array.Copy(args, 0, argv, 1, args.Length);
        argv[^1] = null!;

        var env = Environment.GetEnvironmentVariables();
        var envp = new string[env.Count + 1];
        var i = 0;
        foreach (DictionaryEntry entry in env)
            envp[i++] = $"{entry.Key}={entry.Value}";
        envp[^1] = null!;

        var rc = Execve(path, argv, envp);
        errno = Marshal.GetLastPInvokeError();
        return rc == 0;
    }

    internal static string FormatExecFailure(string path, int errno)
    {
        string detail;
        try
        {
            detail = Marshal.GetPInvokeErrorMessage(errno) ?? $"errno {errno}";
        }
        catch (ArgumentException)
        {
            detail = $"errno {errno}";
        }

        return $"error: execve('{path}') failed (errno {errno}: {detail})";
    }

    [DllImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static extern int Execve(string path, string[] argv, string[] envp);
}
