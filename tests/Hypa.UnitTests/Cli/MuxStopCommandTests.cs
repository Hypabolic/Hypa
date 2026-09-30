using System.Diagnostics;
using System.Text.Json;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxStopCommandTests
{
    [Fact]
    public void LooksLikeMux_RequiresMuxServeArgv_NotBareHypaOrRuntimeName()
    {
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", null));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa-runtime", null));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", "hypa -c ping"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", "hypa doctor"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", "hypa attach --session default"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa-runtime", "hypa-runtime --session x"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa-runtime", "hypa-runtime.dll --session x"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("dotnet", "dotnet exec hypa-runtime.dll --session x"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("dotnet", "dotnet /app/Hypa.AgentServer.dll --session x"));
        Assert.True(MuxProcessIdentity.LooksLikeMux("hypa", "hypa mux serve --session x"));
        Assert.True(MuxProcessIdentity.LooksLikeMux("hypa-runtime", "hypa-runtime mux serve --session x"));
        Assert.True(MuxProcessIdentity.LooksLikeMux(
            "hypa-runtime",
            "hypa-runtime mux serve --session x --cwd /tmp --state-dir /tmp/s"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa-runtime", "hypa-runtime attach --session x"));
        Assert.True(MuxProcessIdentity.LooksLikeMux("dotnet", "dotnet exec hypa.dll mux serve --session x"));
        Assert.True(MuxProcessIdentity.LooksLikeMux("dotnet", "dotnet /app/Hypa.AgentServer.dll mux serve --session x"));
        Assert.True(MuxProcessIdentity.LooksLikeMux(["/usr/local/bin/hypa", "mux", "serve", "--session", "x"]));
        Assert.False(MuxProcessIdentity.LooksLikeMux("sleep", "/bin/sleep 60"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("sleep", "sleep mux serve"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("init", null));
        Assert.False(MuxProcessIdentity.LooksLikeMux("dotnet", "dotnet test tests/Hypa.UnitTests"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("dotnet", "dotnet test --filter MuxServe"));
        Assert.False(MuxProcessIdentity.LooksLikeMux(null, null));
    }

    [Fact]
    public void LooksLikeMux_RejectsCompressionArgvThatContainsMuxServeWords()
    {
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", "hypa -c echo mux serve"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", "hypa -c 'echo mux serve'"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", "hypa -t mux serve"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", "hypa doctor mux serve"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa", "hypa attach mux serve"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("hypa-runtime", "hypa-runtime -c echo mux serve"));
        Assert.False(MuxProcessIdentity.LooksLikeMux("dotnet", "dotnet exec hypa.dll -c echo mux serve"));
        Assert.False(MuxProcessIdentity.LooksLikeMux(["hypa", "-c", "echo mux serve"]));
        Assert.False(MuxProcessIdentity.LooksLikeMux(["/usr/bin/hypa", "-c", "echo mux serve"]));
        Assert.False(MuxProcessIdentity.LooksLikeMux(["dotnet", "exec", "hypa.dll", "-c", "echo mux serve"]));
        Assert.False(MuxProcessIdentity.LooksLikeMux(["hypa-runtime", "-c", "echo mux serve"]));
        Assert.Equal(
            ["hypa", "-c", "echo mux serve"],
            MuxProcessIdentity.SplitArgs("hypa -c 'echo mux serve'"));
        Assert.Equal(
            ["hypa", "-c", "echo mux serve"],
            MuxProcessIdentity.SplitNulTerminated("hypa\0-c\0echo mux serve\0"u8.ToArray()));
    }

    [SkippableFact]
    public void TryReadArgv_PreservesDashCMuxServeArgument_AndRejectsMuxIdentity()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux argv identity; not proven on Windows.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-argv-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        Process? decoy = null;
        try
        {
            decoy = StartNamedDecoy(dir, "hypa", "-c", "echo mux serve");
            IReadOnlyList<string>? argv = null;
            for (var i = 0; i < 20 && argv is null; i++)
            {
                argv = MuxProcessIdentity.TryReadArgv(decoy.Id);
                if (argv is not { Count: >= 2 })
                {
                    argv = null;
                    Thread.Sleep(50);
                }
            }

            Assert.NotNull(argv);
            Assert.False(MuxProcessIdentity.LooksLikeMux(argv));
            Assert.Contains(argv, token => token.Contains("echo mux serve", StringComparison.Ordinal));
        }
        finally
        {
            TryKill(decoy, entireProcessTree: false);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void HasStableIdentity_WrongStartTime_FailsClosed()
    {
        using var sleeper = StartGenericSleeper();
        Assert.True(MuxProcessIdentity.TryReadStartTimeUtc(sleeper.Id, out var start));
        Assert.True(MuxProcessIdentity.HasStableIdentity(sleeper.Id, start));
        Assert.False(MuxProcessIdentity.HasStableIdentity(sleeper.Id, start.AddTicks(1)));
        Assert.False(MuxProcessIdentity.HasStableIdentity(-1, start));
        Assert.False(sleeper.HasExited);
    }

    [Fact]
    public void TryAcquireMuxServer_NonMuxPid_FailsClosed()
    {
        using var sleeper = StartGenericSleeper();
        Assert.False(MuxProcessIdentity.TryAcquireMuxServer(sleeper.Id, out var lease));
        Assert.Null(lease);
        Assert.False(sleeper.HasExited);
    }

    [Fact]
    public void TryReadStartTimeUtc_DeadOrInvalidPid_FailsClosed()
    {
        Assert.False(MuxProcessIdentity.TryReadStartTimeUtc(0, out _));
        Assert.False(MuxProcessIdentity.TryReadStartTimeUtc(-1, out _));
        Assert.False(MuxProcessIdentity.TryReadStartTimeUtc(int.MaxValue, out _));
    }

    [Fact]
    public async Task Stop_DeadPid_ReturnsNonZero_AndDoesNotKillUnrelatedProcess()
    {
        // Fail-closed when ping is null. Does not exercise IsMuxServerProcess.
        using var sleeper = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "timeout" : "sleep",
            ArgumentList = { OperatingSystem.IsWindows() ? "30" : "30" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(sleeper);
        var victimPid = sleeper!.Id;

        var dir = Path.Combine(Path.GetTempPath(), "hypa-stop-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "hypa.sock");
        var statusPath = Path.Combine(dir, "runtime.status.json");
        await File.WriteAllTextAsync(
            statusPath,
            $$"""{"session":"dead","socket":{{JsonSerializer.Serialize(socket)}},"cwd":{{JsonSerializer.Serialize(dir)}},"pid":{{victimPid}}}""");

        try
        {
            var exit = await MuxCommand.StopAsync("dead", socket);
            Assert.NotEqual(0, exit);
            Assert.False(sleeper.HasExited, "mux stop must not SIGTERM a non-mux recycled pid");
        }
        finally
        {
            try
            {
                if (!sleeper.HasExited)
                    sleeper.Kill();
            }
            catch
            {
                // best-effort
            }

            sleeper.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Stop_MissingStatus_ReturnsNonZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-stop-miss-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "hypa.sock");
        try
        {
            var exit = await MuxCommand.StopAsync("missing", socket);
            Assert.NotEqual(0, exit);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableFact]
    public async Task Stop_RecycledHypaNamedPid_ReturnsNonZero_AndLeavesVictimAlive()
    {
        // Fail-closed when ping is null. Does not exercise IsMuxServerProcess.
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux stop identity gate; not proven on Windows.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-stop-hypa-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var decoy = Path.Combine(dir, "hypa");
        File.WriteAllText(decoy, "#!/bin/sh\n/bin/sleep 30\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(decoy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var victim = Process.Start(new ProcessStartInfo
        {
            FileName = decoy,
            ArgumentList = { "30" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(victim);
        var victimPid = victim!.Id;

        var socket = Path.Combine(dir, "hypa.sock");
        var statusPath = Path.Combine(dir, "runtime.status.json");
        await File.WriteAllTextAsync(
            statusPath,
            $$"""{"session":"recycle","socket":{{JsonSerializer.Serialize(socket)}},"cwd":{{JsonSerializer.Serialize(dir)}},"pid":{{victimPid}}}""");

        try
        {
            var exit = await MuxCommand.StopAsync("recycle", socket);
            Assert.NotEqual(0, exit);
            Assert.False(victim.HasExited, "mux stop must not SIGTERM a recycled process named hypa");
        }
        finally
        {
            try
            {
                if (!victim.HasExited)
                    victim.Kill();
            }
            catch
            {
                // best-effort
            }

            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableFact]
    public async Task Stop_LiveNonMuxHypaCommand_DoesNotKill()
    {
        // Fail-closed when ping is null. Does not exercise IsMuxServerProcess.
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux stop identity gate; not proven on Windows.");

        var launch = TryFindHypaLaunch();
        Skip.If(launch is null, "hypa binary not found for non-mux identity stop test.");

        var psi = new ProcessStartInfo
        {
            FileName = launch.Value.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var prefix in launch.Value.Prefix)
            psi.ArgumentList.Add(prefix);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 30");

        using var victim = Process.Start(psi);
        Assert.NotNull(victim);

        var dir = Path.Combine(Path.GetTempPath(), "hypa-stop-live-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "hypa.sock");
        var statusPath = Path.Combine(dir, "runtime.status.json");
        await File.WriteAllTextAsync(
            statusPath,
            $$"""{"session":"live","socket":{{JsonSerializer.Serialize(socket)}},"cwd":{{JsonSerializer.Serialize(dir)}},"pid":{{victim!.Id}}}""");

        try
        {
            var exit = await MuxCommand.StopAsync("live", socket);
            Assert.NotEqual(0, exit);
            Assert.False(victim.HasExited, "mux stop must not SIGTERM a live non-mux hypa");
        }
        finally
        {
            try
            {
                if (!victim.HasExited)
                    victim.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }

            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableFact]
    public async Task Stop_LiveMux_RecycledNonMuxPid_ReturnsNonZero_VictimAndMuxStayUp()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux stop identity gate; not proven on Windows.");

        var launch = TryFindHypaLaunch();
        Skip.If(launch is null, "hypa binary required so mux stop identity hits a live ping.");
        Skip.If(TryFindGhosttyVt() is null, "libghostty-vt required so the mux starts.");

        var session = "sid-" + Guid.NewGuid().ToString("N")[..10];
        var dir = Path.Combine(Path.GetTempPath(), "hypa-stop-id-" + session);
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "hypa.sock");
        var statusPath = Path.Combine(dir, "runtime.status.json");

        Process? mux = null;
        Process? sleepVictim = null;
        Process? hypaVictim = null;
        Process? runtimeVictim = null;
        try
        {
            mux = StartMuxServe(launch.Value, session, dir, socket);
            await WaitForPingAsync(socket, TimeSpan.FromSeconds(20));
            Assert.True(File.Exists(statusPath), "mux did not write runtime.status.json");
            var originalStatus = await File.ReadAllTextAsync(statusPath);

            sleepVictim = StartSleepVictim();
            await WriteStatusPidAsync(statusPath, session, socket, dir, sleepVictim.Id);
            var sleepExit = await MuxCommand.StopAsync(session, socket);
            Assert.Equal(0, sleepExit);
            Assert.False(sleepVictim.HasExited, "mux stop must not SIGTERM a sleep pid after live ping");
            Assert.Null(await MuxControlPlane.TryPingAsync(socket, CancellationToken.None));

            _ = originalStatus;
        }
        finally
        {
            TryKill(sleepVictim, entireProcessTree: false);
            TryKill(hypaVictim, entireProcessTree: true);
            TryKill(runtimeVictim, entireProcessTree: false);
            TryKill(mux, entireProcessTree: true);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableFact]
    public async Task Stop_LiveMux_NonServingHypaRuntimePid_ReturnsNonZero_VictimAndMuxStayUp()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux stop identity gate; not proven on Windows.");

        var launch = TryFindHypaLaunch();
        Skip.If(launch is null, "hypa binary required so mux stop identity hits a live ping.");
        Skip.If(TryFindGhosttyVt() is null, "libghostty-vt required so the mux starts.");

        var session = "sid-" + Guid.NewGuid().ToString("N")[..10];
        var dir = Path.Combine(Path.GetTempPath(), "hypa-stop-rt-" + session);
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "hypa.sock");
        var statusPath = Path.Combine(dir, "runtime.status.json");

        Process? mux = null;
        Process? runtimeVictim = null;
        try
        {
            mux = StartMuxServe(launch.Value, session, dir, socket);
            await WaitForPingAsync(socket, TimeSpan.FromSeconds(20));
            Assert.True(File.Exists(statusPath), "mux did not write runtime.status.json");
            var originalStatus = await File.ReadAllTextAsync(statusPath);

            runtimeVictim = StartNamedDecoy(dir, "hypa-runtime");
            await WriteStatusPidAsync(statusPath, session, socket, dir, runtimeVictim.Id);
            var exit = await MuxCommand.StopAsync(session, socket);
            Assert.Equal(0, exit);
            Assert.False(
                runtimeVictim.HasExited,
                "live-ping mux stop must not SIGTERM hypa-runtime without mux serve argv");
            Assert.Null(await MuxControlPlane.TryPingAsync(socket, CancellationToken.None));

            _ = originalStatus;
        }
        finally
        {
            TryKill(runtimeVictim, entireProcessTree: false);
            TryKill(mux, entireProcessTree: true);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableFact]
    public async Task Stop_LiveMux_CompressionArgvContainingMuxServe_ReturnsNonZero_VictimAndMuxStayUp()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux stop identity gate; not proven on Windows.");

        var launch = TryFindHypaLaunch();
        Skip.If(launch is null, "hypa binary required so mux stop identity hits a live ping.");
        Skip.If(TryFindGhosttyVt() is null, "libghostty-vt required so the mux starts.");

        var session = "sid-" + Guid.NewGuid().ToString("N")[..10];
        var dir = Path.Combine(Path.GetTempPath(), "hypa-stop-c-mux-" + session);
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "hypa.sock");
        var statusPath = Path.Combine(dir, "runtime.status.json");

        Process? mux = null;
        Process? dashCVictim = null;
        Process? dotnetVictim = null;
        Process? aliasVictim = null;
        try
        {
            mux = StartMuxServe(launch.Value, session, dir, socket);
            await WaitForPingAsync(socket, TimeSpan.FromSeconds(20));
            Assert.True(File.Exists(statusPath), "mux did not write runtime.status.json");
            var originalStatus = await File.ReadAllTextAsync(statusPath);

            dashCVictim = StartHypaDashC(launch.Value, "echo mux serve; sleep 60", timeoutMs: 120_000);
            await WriteStatusPidAsync(statusPath, session, socket, dir, dashCVictim.Id);
            var dashCExit = await MuxCommand.StopAsync(session, socket);
            Assert.Equal(0, dashCExit);
            Assert.False(
                dashCVictim.HasExited,
                "mux stop must not SIGTERM hypa -c 'echo mux serve' after live ping");
            Assert.Null(await MuxControlPlane.TryPingAsync(socket, CancellationToken.None));

            _ = originalStatus;
            aliasVictim = StartNamedDecoy(dir, "hypa-runtime", "-c", "echo mux serve");
            Assert.False(
                aliasVictim.HasExited,
                "server.stop must not SIGTERM hypa-runtime -c 'echo mux serve'");
        }
        finally
        {
            TryKill(dashCVictim, entireProcessTree: true);
            TryKill(dotnetVictim, entireProcessTree: true);
            TryKill(aliasVictim, entireProcessTree: false);
            TryKill(mux, entireProcessTree: true);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableFact]
    public void TryAcquireMuxServer_DecoyMuxServe_HoldsStartTimeAndRefusesAfterDeath()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux process lease gate; not proven on Windows.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-lease-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        Process? decoy = null;
        Process? bystander = null;
        MuxProcessIdentity.MuxProcessLease? lease = null;
        try
        {
            decoy = StartNamedDecoy(dir, "hypa", "mux", "serve", "--session", "lease");
            var acquired = false;
            for (var i = 0; i < 20 && !acquired; i++)
            {
                acquired = MuxProcessIdentity.TryAcquireMuxServer(decoy.Id, out lease);
                if (!acquired)
                    Thread.Sleep(50);
            }

            Assert.True(acquired, "decoy hypa mux serve must be acquirable as a mux target");
            Assert.NotNull(lease);
            Assert.Equal(decoy.Id, lease!.Pid);
            Assert.True(lease.MatchesCapturedStartTime());
            if (OperatingSystem.IsLinux())
                Assert.True(lease.HoldsPidFd, "Linux mux lease must hold a pidfd through SIGTERM.");

            decoy.Kill(entireProcessTree: false);
            Assert.True(decoy.WaitForExit(2000), "decoy mux did not exit after test kill");
            decoy.Dispose();
            decoy = null;

            var goneDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (MuxProcessIdentity.IsAlive(lease.Pid) && DateTime.UtcNow < goneDeadline)
                Thread.Sleep(20);

            bystander = StartSleepVictim();
            Assert.False(lease.MatchesCapturedStartTime());
            var terminated = lease.TryTerminate(out var error);
            if (!lease.HoldsPidFd)
            {
                Assert.False(terminated);
                Assert.False(string.IsNullOrWhiteSpace(error));
            }

            Assert.False(bystander.HasExited, "a released or recycled pid must not be killed");
        }
        finally
        {
            lease?.Dispose();
            TryKill(decoy, entireProcessTree: false);
            TryKill(bystander, entireProcessTree: false);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableFact]
    public void TryAcquireMuxServer_LinuxPidFdOpenFailure_FailsClosed_NoSignal()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux process lease gate; not proven on Windows.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-pidfd-fail-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        Process? decoy = null;
        try
        {
            decoy = StartNamedDecoy(dir, "hypa", "mux", "serve", "--session", "pidfd-fail");
            var seen = false;
            for (var i = 0; i < 20 && !seen; i++)
            {
                var argv = MuxProcessIdentity.TryReadArgv(decoy.Id);
                seen = argv is not null && MuxProcessIdentity.LooksLikeMux(argv);
                if (!seen)
                    Thread.Sleep(50);
            }

            Assert.True(seen, "decoy hypa mux serve argv must be readable before the pidfd seam test");
            Assert.False(
                MuxProcessIdentity.TryAcquireMuxServer(
                    decoy.Id,
                    MuxProcessIdentity.MuxPidFdPolicy.LinuxRequiredUnavailable,
                    out var lease));
            Assert.Null(lease);
            Assert.False(decoy.HasExited, "pidfd-open failure must fail closed and send no signal");
        }
        finally
        {
            TryKill(decoy, entireProcessTree: false);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableFact]
    public void TryTerminate_RequiredPidFdMissing_FailsClosed_NoSignal()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux process lease gate; not proven on Windows.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-pidfd-term-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        Process? decoy = null;
        try
        {
            decoy = StartNamedDecoy(dir, "hypa", "mux", "serve", "--session", "pidfd-term");
            Assert.True(MuxProcessIdentity.TryReadStartTimeUtc(decoy.Id, out var start));
            var process = Process.GetProcessById(decoy.Id);
            using var lease = new MuxProcessIdentity.MuxProcessLease(
                process,
                decoy.Id,
                start,
                pidfd: -1,
                requirePidFd: true);

            Assert.False(lease.TryTerminate(out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.False(decoy.HasExited, "a Linux lease without a pidfd must not numeric-PID kill");
        }
        finally
        {
            TryKill(decoy, entireProcessTree: false);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ResolveSocketPath_SessionOnly_IgnoresEnvironment()
    {
        var prev = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", "/tmp/hypa-env-override.sock");
            var envPath = UnixSocketServer.ResolveSocketPath("alpha");
            Assert.Equal(Path.GetFullPath("/tmp/hypa-env-override.sock"), envPath);

            var sessionPath = UnixSocketServer.ResolveSocketPath("alpha", honorEnvironment: false);
            Assert.Contains(Path.Combine("runtime", "alpha", "hypa.sock"), sessionPath);
            Assert.NotEqual(envPath, sessionPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", prev);
        }
    }

    [Fact]
    public void ResolveStopSocket_EnvOnly_UsesEnvironment()
    {
        var prev = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", "/tmp/hypa-env-override.sock");
            var path = MuxCommand.ResolveStopSocket("default", socketOverride: null, sessionExplicit: false);
            Assert.Equal(Path.GetFullPath("/tmp/hypa-env-override.sock"), path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", prev);
        }
    }

    [Fact]
    public void ResolveStopSocket_ExplicitSessionMismatch_Throws()
    {
        var prev = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", "/tmp/hypa-env-override.sock");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MuxCommand.ResolveStopSocket("other", socketOverride: null, sessionExplicit: true));
            Assert.Contains("HYPA_RUNTIME_SOCKET", ex.Message, StringComparison.Ordinal);
            Assert.Contains("other", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", prev);
        }
    }

    [Fact]
    public void ResolveStopSocket_SocketOverride_Wins()
    {
        var prev = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", "/tmp/hypa-env-override.sock");
            var path = MuxCommand.ResolveStopSocket(
                "default",
                socketOverride: "/tmp/explicit.sock",
                sessionExplicit: true);
            Assert.Equal(Path.GetFullPath("/tmp/explicit.sock"), path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", prev);
        }
    }

    /// <summary>
    /// The mux requires libghostty-vt. Use <c>HYPA_GHOSTTY_VT</c>, else the
    /// library that scripts/build-libghostty-vt.sh wrote for this RID.
    /// </summary>
    private static string? TryFindGhosttyVt()
    {
        var env = Environment.GetEnvironmentVariable("HYPA_GHOSTTY_VT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        var name = OperatingSystem.IsMacOS() ? "libghostty-vt.dylib" : "libghostty-vt.so";
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var path = Path.Combine(dir, "native", "runtimes", rid, "native", name);
            if (File.Exists(path))
                return path;
            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    private static (string FileName, string[] Prefix)? TryFindHypaLaunch()
    {
        var nativeName = OperatingSystem.IsWindows() ? "hypa.exe" : "hypa";
        var testhostNative = Path.Combine(AppContext.BaseDirectory, nativeName);
        if (File.Exists(testhostNative))
            return (testhostNative, []);
        var testhostDll = Path.Combine(AppContext.BaseDirectory, "hypa.dll");
        if (File.Exists(testhostDll))
            return ("dotnet", ["exec", testhostDll]);

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            foreach (var config in new[] { "Release", "Debug" })
            {
                var bin = Path.Combine(dir, "src", "Hypa.Cli", "bin", config, "net10.0");
                var native = Path.Combine(bin, nativeName);
                if (File.Exists(native))
                    return (native, []);
                var dll = Path.Combine(bin, "hypa.dll");
                if (File.Exists(dll))
                    return ("dotnet", ["exec", dll]);
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    private static Process StartMuxServe(
        (string FileName, string[] Prefix) launch,
        string session,
        string dir,
        string socket)
    {
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
        if (TryFindGhosttyVt() is { } ghostty)
            psi.Environment["HYPA_GHOSTTY_VT"] = ghostty;

        var mux = Process.Start(psi);
        Assert.NotNull(mux);
        _ = mux!.StandardOutput.ReadToEndAsync();
        _ = mux.StandardError.ReadToEndAsync();
        mux.StandardInput.Close();
        return mux;
    }

    private static Process StartGenericSleeper()
    {
        var victim = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "timeout" : "sleep",
            ArgumentList = { OperatingSystem.IsWindows() ? "30" : "30" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(victim);
        return victim!;
    }

    private static Process StartSleepVictim()
    {
        var victim = Process.Start(new ProcessStartInfo
        {
            FileName = "sleep",
            ArgumentList = { "30" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(victim);
        return victim!;
    }

    private static Process StartNamedDecoy(string dir, string name, params string[] args)
    {
        var decoy = Path.Combine(dir, name);
        File.WriteAllText(decoy, "#!/bin/sh\n/bin/sleep 30\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(decoy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo
        {
            FileName = decoy,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var victim = Process.Start(psi);
        Assert.NotNull(victim);
        return victim!;
    }

    private static Process StartHypaDashCSleep((string FileName, string[] Prefix) launch) =>
        StartHypaDashC(launch, "sleep 30");

    private static Process StartHypaDashC(
        (string FileName, string[] Prefix) launch,
        string command,
        int? timeoutMs = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var prefix in launch.Prefix)
            psi.ArgumentList.Add(prefix);
        if (timeoutMs is int ms)
        {
            psi.ArgumentList.Add("--timeout-ms");
            psi.ArgumentList.Add(ms.ToString());
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        var victim = Process.Start(psi);
        Assert.NotNull(victim);
        _ = victim!.StandardOutput.ReadToEndAsync();
        _ = victim.StandardError.ReadToEndAsync();
        return victim;
    }

    private static (string FileName, string[] Prefix)? TryFindDotnetExecHypaLaunch()
    {
        var testhostDll = Path.Combine(AppContext.BaseDirectory, "hypa.dll");
        if (File.Exists(testhostDll))
            return ("dotnet", ["exec", testhostDll]);

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            foreach (var config in new[] { "Release", "Debug" })
            {
                var dll = Path.Combine(dir, "src", "Hypa.Cli", "bin", config, "net10.0", "hypa.dll");
                if (File.Exists(dll))
                    return ("dotnet", ["exec", dll]);
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    private static async Task WriteStatusPidAsync(
        string statusPath,
        string session,
        string socket,
        string cwd,
        int pid)
    {
        await File.WriteAllTextAsync(
            statusPath,
            $$"""{"session":{{JsonSerializer.Serialize(session)}},"socket":{{JsonSerializer.Serialize(socket)}},"cwd":{{JsonSerializer.Serialize(cwd)}},"pid":{{pid}}}""");
    }

    private static async Task WaitForPingAsync(string socket, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ping = await MuxControlPlane.TryPingAsync(socket, CancellationToken.None);
            if (ping is not null)
                return;

            await Task.Delay(50);
        }

        var last = await MuxControlPlane.TryPingAsync(socket, CancellationToken.None);
        Assert.True(last is not null, "mux did not become ready for identity stop test");
    }

    private static void TryKill(Process? process, bool entireProcessTree)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree);
        }
        catch
        {
            // best-effort
        }

        process.Dispose();
    }
}
