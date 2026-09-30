using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.Continuity.Tests;

/// <summary>
/// Live mux serve for Continuity process tests. Ghostty is the only pane VT.
/// </summary>
internal static class LiveMuxLaunch
{
    public static string RequireGhosttyLibraryPath()
    {
        var path = TryResolveGhosttyLibraryPath();
        var required = string.Equals(
            Environment.GetEnvironmentVariable("HYPA_REQUIRE_GHOSTTY_TESTS"),
            "1",
            StringComparison.Ordinal);
        if (path is null && required)
        {
            Assert.Fail(
                "HYPA_REQUIRE_GHOSTTY_TESTS=1 requires libghostty-vt for live mux serve. Build with scripts/build-libghostty-vt.sh.");
        }

        Skip.If(path is null, "libghostty-vt required for live mux serve.");
        return path!;
    }

    public static string? TryResolveGhosttyLibraryPath()
    {
        var env = Environment.GetEnvironmentVariable("HYPA_GHOSTTY_VT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return Path.GetFullPath(env);

        if (!TryLibraryFileName(out var fileName))
            return null;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
            {
                var ridPath = Path.Combine(
                    dir.FullName,
                    "native",
                    "runtimes",
                    PortableRid(),
                    "native",
                    fileName);
                if (File.Exists(ridPath))
                    return Path.GetFullPath(ridPath);
                break;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static (Process Process, StringBuilder Stderr) StartServe(
        (string FileName, string[] Prefix) launch,
        string session,
        string dir,
        string socket,
        string cubeHome,
        string logPath)
    {
        var ghostty = RequireGhosttyLibraryPath();
        var psi = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = dir,
        };
        foreach (var prefix in launch.Prefix)
            psi.ArgumentList.Add(prefix);
        psi.ArgumentList.Add("mux");
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--session");
        psi.ArgumentList.Add(session);
        psi.ArgumentList.Add("--cwd");
        psi.ArgumentList.Add(dir);
        psi.ArgumentList.Add("--state-dir");
        psi.ArgumentList.Add(dir);
        psi.Environment["HYPA_RUNTIME_SOCKET"] = socket;
        psi.Environment["HYPA_RUNTIME_STATE_DIR"] = dir;
        psi.Environment["HYPA_CUBE_HOME"] = cubeHome;
        psi.Environment["HOME"] = cubeHome;
        psi.Environment["HYPA_PTY_PROVIDER"] = "process-io";
        psi.Environment["HYPA_VT_PROVIDER"] = "ghostty";
        psi.Environment["HYPA_GHOSTTY_VT"] = ghostty;
        psi.Environment["HYPA_MUX_LOG"] = logPath;
        psi.Environment["SHELL"] = "/bin/sh";

        var stderr = new StringBuilder();
        var mux = Process.Start(psi);
        Assert.NotNull(mux);
        mux!.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };
        mux.BeginErrorReadLine();
        _ = Task.Run(() => mux.StandardOutput.ReadToEndAsync());
        mux.StandardInput.Close();
        return (mux, stderr);
    }

    public static async Task WaitForPingAsync(
        string socket,
        TimeSpan timeout,
        Process mux,
        StringBuilder stderr)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (mux.HasExited)
            {
                throw new TimeoutException(
                    $"mux exited {mux.ExitCode} before ping on {socket}: {last}; stderr={stderr}");
            }

            try
            {
                await using var client = new ControlPlaneClient(socket, connectTimeout: TimeSpan.FromSeconds(1));
                await client.ConnectAsync();
                _ = await client.CallAsync(ProtocolMethods.Ping);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(200);
            }
        }

        throw new TimeoutException($"mux did not accept ping on {socket}: {last}; stderr={stderr}");
    }

    private static bool TryLibraryFileName(out string fileName)
    {
        if (OperatingSystem.IsMacOS())
        {
            fileName = "libghostty-vt.dylib";
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            fileName = "libghostty-vt.so";
            return true;
        }

        fileName = "";
        return false;
    }

    private static string PortableRid()
    {
        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
        }

        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-arm64"
            : "linux-x64";
    }
}
