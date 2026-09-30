using System.Diagnostics;
using System.Text;

namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>Runs the built <c>hypa</c> client from the test output directory.</summary>
internal static class HypaCliProcess
{
    public static Task<(int Code, string Stdout, string Stderr)> RunAsync(params string[] args) =>
        RunCoreAsync(environment: null, args);

    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        IReadOnlyDictionary<string, string?> environment,
        params string[] args)
    {
        var (code, stdout, stderr) = await RunCoreAsync(environment, args).ConfigureAwait(false);
        return (code, stdout, stderr);
    }

    public static async Task<(int Code, byte[] Stdout, string Stderr)> RunBytesAsync(params string[] args)
    {
        var (code, stdout, stderr) = await RunCoreBytesAsync(environment: null, args).ConfigureAwait(false);
        return (code, stdout, stderr);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCoreAsync(
        IReadOnlyDictionary<string, string?>? environment,
        string[] args)
    {
        var (code, stdout, stderr) = await RunCoreBytesAsync(environment, args).ConfigureAwait(false);
        return (code, Encoding.UTF8.GetString(stdout), stderr);
    }

    private static async Task<(int Code, byte[] Stdout, string Stderr)> RunCoreBytesAsync(
        IReadOnlyDictionary<string, string?>? environment,
        string[] args)
    {
        var psi = StartInfo(args, environment);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("hypa did not start");
        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        await copy.ConfigureAwait(false);
        return (process.ExitCode, stdout.ToArray(), await stderrTask.ConfigureAwait(false));
    }

    private static ProcessStartInfo StartInfo(
        string[] args,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var dir = AppContext.BaseDirectory;
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "hypa.exe" : "hypa");
        var dll = Path.Combine(dir, "hypa.dll");
        ProcessStartInfo psi;
        if (File.Exists(exe))
        {
            psi = new ProcessStartInfo(exe);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
        }
        else if (File.Exists(dll))
        {
            psi = new ProcessStartInfo("dotnet");
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(dll);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
        }
        else
        {
            throw new InvalidOperationException("hypa was not found in " + dir);
        }

        psi.UseShellExecute = false;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;
            }
        }

        return psi;
    }

    private static readonly Lazy<string?> Launcher = new(WriteLauncher);

    /// <summary>
    /// Pane shells start with a reduced environment, so an apphost run from a
    /// pane cannot find .NET. The launcher exports DOTNET_ROOT and runs the
    /// apphost.
    /// </summary>
    private static string? WriteLauncher()
    {
        if (OperatingSystem.IsWindows())
            return null;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var exe = Path.Combine(AppContext.BaseDirectory, "hypa");
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(exe))
            return null;
        var path = Path.Combine(
            Path.GetTempPath(),
            "hypa-test-launch-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(
            path,
            "#!/bin/sh\nexport DOTNET_ROOT='" + root.Replace("'", "'\\''") + "'\nexec '" + exe.Replace("'", "'\\''") + "' \"$@\"\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public static string CliPath()
    {
        var dir = AppContext.BaseDirectory;
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "hypa.exe" : "hypa");
        if (File.Exists(exe))
            return Launcher.Value ?? exe;
        var dll = Path.Combine(dir, "hypa.dll");
        if (File.Exists(dll))
            return dll;
        throw new InvalidOperationException("hypa was not found in " + dir);
    }
}
