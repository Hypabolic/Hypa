using System.Diagnostics;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Foreground-job agent naming for Linux and macOS argv.
/// </summary>
public class AgentProcessResolverTests
{
    private const string PowerShellShellIntegration =
        @"if ($null -eq $global:__HerdrOriginalPrompt) { $global:__HerdrOriginalPrompt = $function:prompt; function global:prompt { $out = @(& $global:__HerdrOriginalPrompt) -join ' '; $loc = $ExecutionContext.SessionState.Path.CurrentLocation; if ($loc.Provider.Name -eq 'FileSystem') { try { [Environment]::CurrentDirectory = $loc.ProviderPath } catch {}; $esc = [string][char]27; $out += $esc + ']9;9;' + $loc.ProviderPath + $esc + '\' }; $out } }";

    [Fact]
    public void IdentifyInJob_prefers_node_wrapped_codex()
    {
        var job = Job(
            123,
            Proc(1, "node", "node", "/path/to/bin/codex"),
            Proc(2, "bash", "bash"));

        AssertKind(job, "codex");
    }

    [Fact]
    public void IdentifyInJob_detects_node_wrapped_qwen()
    {
        string[][] argvs =
        [
            ["node", "/home/user/.fnm/bin/qwen"],
            [
                "node.exe",
                @"C:\Users\user\AppData\Roaming\npm\node_modules\@qwen-code\qwen-code\dist\index.js",
            ],
        ];

        foreach (var argv in argvs)
            AssertKind(Job(123, Proc(123, "MainThread", argv)), "qwen");
    }

    [Fact]
    public void IdentifyInJob_prefers_process_group_leader()
    {
        var job = Job(
            42,
            Proc(42, "claude", "claude"),
            Proc(43, "node", "node", "/tmp/mcp/bin/codex"));

        AssertKind(job, "claude");
    }

    [Fact]
    public void IdentifyInJob_falls_back_when_leader_is_not_an_agent()
    {
        var job = Job(
            42,
            Proc(42, "bash", "bash"),
            Proc(43, "node", "node", "/tmp/mcp/bin/codex"));

        AssertKind(job, "codex");
    }

    [Fact]
    public void IdentifyInJob_detects_python_wrapped_hermes()
    {
        var job = Job(
            123,
            Proc(
                123,
                "python3.12",
                "/nix/store/example/bin/python3.12",
                "/nix/store/example/bin/hermes",
                "--resume",
                "session-id"));

        AssertKind(job, "hermes");
    }

    [Fact]
    public void IdentifyInJob_detects_nix_wrapped_codex_from_argv()
    {
        var job = Job(
            123,
            Proc(1, ".codex-wrapped", "/etc/profiles/per-user/user/bin/codex", "--model", "gpt-5"));

        AssertKind(job, "codex");
    }

    [Fact]
    public void IdentifyInJob_canonicalizes_nix_wrapped_claude_code_alias()
    {
        var job = Job(
            123,
            Proc(1, ".claude-code-wrapped", "/nix/store/example/bin/claude-code"));

        AssertKind(job, "claude");
    }

    [Fact]
    public void IdentifyInJob_detects_shell_wrapped_pi()
    {
        var job = Job(123, Proc(1, "sh", "/bin/sh", "/tmp/test-bin/pi"));
        AssertKind(job, "pi");
    }

    [Fact]
    public void IdentifyInJob_detects_bun_wrapped_omp()
    {
        var job = Job(123, Proc(123, "bun", "bun", "/home/can/.bun/bin/omp"));
        AssertKind(job, "omp");
    }

    [Fact]
    public void IdentifyInJob_detects_node_wrapped_pi_package_cli()
    {
        var job = Job(
            123,
            Proc(
                123,
                "node.exe",
                "node.exe",
                @"C:\Users\herdr\AppData\Roaming\npm\node_modules\@earendil-works\pi-coding-agent\dist\cli.js"));

        AssertKind(job, "pi");
    }

    [Fact]
    public void IdentifyInJob_detects_node_wrapped_pi_bundle_cli()
    {
        var job = Job(
            123,
            Proc(
                123,
                "node.exe",
                @"C:\Users\herdr\AppData\Local\pi-node\current\node.exe",
                @"C:\Users\herdr\AppData\Local\pi-node\current/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js"));

        AssertKind(job, "pi");
    }

    [Fact]
    public void IdentifyInJob_detects_node_wrapped_mastracode_package_cli()
    {
        var job = Job(
            123,
            Proc(
                123,
                "node.exe",
                "node.exe",
                @"C:\Users\herdr\AppData\Roaming\npm\node_modules\mastracode\dist\cli.js"));

        AssertKind(job, "mastracode");
    }

    [Fact]
    public void IdentifyInJob_ignores_non_cli_pi_package_scripts()
    {
        string[] scripts =
        [
            @"C:\Users\herdr\AppData\Roaming\npm\node_modules\@earendil-works\pi-coding-agent\scripts\build.js",
            @"C:\Users\herdr\AppData\Local\pi-node\current\node_modules\@earendil-works\pi-coding-agent\dist\bundle\update.js",
            @"C:\workspace\dist\bundle\cli.js",
            @"C:\workspace\node_modules\other-package\dist\bundle\cli.js",
            @"C:\workspace\node_modules\@earendil-works\pi-coding-agent\dist\cli.exe",
            @"C:\workspace\node_modules\@earendil-works\pi-coding-agent\dist\cli.js\other.js",
            @"C:\workspace\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.exe",
            @"C:\workspace\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js\other.js",
        ];

        foreach (var script in scripts)
            AssertKind(Job(123, Proc(123, "node.exe", "node.exe", script)), null, script);
    }

    [Fact]
    public void IdentifyInJob_ignores_invalid_cursor_install_paths()
    {
        string[] scripts =
        [
            @"C:\Users\user\AppData\Local\cursor-agent\versions\2026.08.11-e8db854\scripts\postinstall.js",
            @"C:\Users\user\AppData\Local\cursor-agent\versions\2026.08.11-e8db854\index",
            @"C:\Users\user\AppData\Local\cursor-agent\versions\2026.08.11-e8db854\index.exe",
        ];
        var node = @"C:\Users\user\AppData\Local\cursor-agent\versions\2026.08.11-e8db854\node.exe";

        foreach (var script in scripts)
            AssertKind(Job(123, Proc(123, "node.exe", node, script)), null, script);

        var lookalike = Job(
            123,
            Proc(
                123,
                "node.exe",
                @"C:\Program Files\nodejs\node.exe",
                @"C:\workspace\cursor-agent\versions\test\index.js"));
        AssertKind(lookalike, null);
    }

    [Fact]
    public void IdentifyInJob_ignores_powershell_shell_integration_argv()
    {
        var job = Job(
            123,
            Proc(
                1,
                "powershell.exe",
                "powershell.exe",
                "-NoExit",
                "-Command",
                PowerShellShellIntegration));

        AssertKind(job, null);
    }

    [Fact]
    public void IdentifyInJob_detects_opencode2()
    {
        var job = Job(123, Proc(123, "opencode2", "opencode2", "--standalone"));
        AssertKind(job, "opencode");
    }

    [Fact]
    public void IdentifyInJob_detects_opencode_exe_process_name()
    {
        var job = Job(
            123,
            Proc(
                123,
                "opencode.exe",
                "/home/user/.local/share/pnpm/global/node_modules/opencode-ai/bin/opencode.exe"));

        AssertKind(job, "opencode");
    }

    [Fact]
    public void IdentifyInJob_detects_opencode_exe_from_argv_path()
    {
        var job = Job(
            123,
            Proc(
                123,
                "MainThread",
                "/home/user/.local/share/pnpm/global/node_modules/opencode-ai/bin/opencode.exe"));

        AssertKind(job, "opencode");
    }

    [Fact]
    public void IdentifyInJob_ignores_plain_shell_flag()
    {
        var job = Job(1, Proc(1, "bash", "bash", "-lc"));
        AssertKind(job, null);
    }

    [Fact]
    public void IdentifyInJob_ignores_python_c_argument_named_codex()
    {
        var job = Job(
            123,
            Proc(1, "python3", "python3", "-c", "import time; time.sleep(60)", "/tmp/codex"));

        AssertKind(job, null);
    }

    [Fact]
    public void IdentifyInJob_ignores_node_eval_argument_named_codex()
    {
        var job = Job(
            123,
            Proc(1, "node", "node", "-e", "setTimeout(() => {}, 60000)", "/tmp/codex"));

        AssertKind(job, null);
    }

    [Fact]
    public void IdentifyInJob_ignores_shell_c_argument_named_codex()
    {
        var job = Job(123, Proc(1, "bash", "bash", "-c", "sleep 60", "/tmp/codex"));
        AssertKind(job, null);
    }

    [Fact]
    public void IdentifyInJob_detects_python_script_named_codex()
    {
        var job = Job(123, Proc(1, "python3", "python3", "/tmp/codex", "--model", "gpt-5"));
        AssertKind(job, "codex");
    }

    [Fact]
    public void IdentifyInJob_canonicalizes_ghcs_from_cmdline()
    {
        var job = Job(
            123,
            new ForegroundProcessSnapshot
            {
                Pid = 1,
                Name = ".ghcs-wrapped",
                Cmdline = "/nix/store/example/bin/ghcs",
            });

        AssertKind(job, "copilot");
    }

    [Fact]
    public void IdentifyInJob_requires_exact_agent_basename()
    {
        var job = Job(
            123,
            new ForegroundProcessSnapshot
            {
                Pid = 1,
                Name = "helper",
                Cmdline = "/tmp/my-codex-helper",
            });

        AssertKind(job, null);
    }

    [Fact]
    public void IdentifyInJob_resolves_cursor_agent_symlink_argv0()
    {
        var dir = TempDir("cursor-agent-symlink");
        try
        {
            var target = Path.Combine(dir, "cursor-agent");
            var link = Path.Combine(dir, "agent");
            File.WriteAllText(target, "#!/bin/sh\n");
            File.CreateSymbolicLink(link, target);

            var job = Job(42, Proc(42, "MainThread", link, "--use-system-ca", "/tmp/index.js"));
            AssertKind(job, "cursor", paths: OsAgentPathCanonicalizer.Instance);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IdentifyInJob_resolves_generic_name_under_symlinked_parent()
    {
        var dir = TempDir("parent-symlink");
        try
        {
            var real = Path.Combine(dir, "real", "node_modules", "mastracode", "dist");
            Directory.CreateDirectory(real);
            var script = Path.Combine(real, "cli.js");
            File.WriteAllText(script, "// fake cli\n");
            var parent = Path.Combine(dir, "pkg");
            Directory.CreateSymbolicLink(parent, real);

            var token = Path.Combine(parent, "cli.js");
            Assert.True(File.Exists(token));
            Assert.Null(File.ResolveLinkTarget(token, returnFinalTarget: false));

            var job = Job(7, Proc(7, "node", "node", token));
            AssertKind(job, "mastracode", paths: OsAgentPathCanonicalizer.Instance);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Linux_stat_identity_reads_process_group_and_comm()
    {
        const string stat = "42 (my agent) S 1 99 99 0 77 0";
        Assert.True(UnixPaneProcessInfoProbe.TryParseLinuxStatIdentity(stat, out var pgrp, out var comm));
        Assert.Equal(99, pgrp);
        Assert.Equal("my agent", comm);
    }

    [Fact]
    public async Task Pane_reports_mastracode_for_node_package_cli()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix pane process detection.");
        var dir = TempDir("mastracode-pane");
        PaneRuntime? runtime = null;
        try
        {
            var script = Path.Combine(dir, "node_modules", "mastracode", "dist", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            await File.WriteAllTextAsync(script, "setInterval(() => {}, 1000);\n");

            runtime = await StartPaneAsync("node", script);
            var info = await WaitForAgentAsync(runtime, "mastracode");
            Assert.Equal("mastracode", info.Command);
            Assert.True(AgentKindCatalog.TryResolve(info.Command, out var kind));
            Assert.Equal("mastracode", kind);
        }
        finally
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Pane_reports_cursor_for_symlinked_cursor_agent()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix pane process detection.");
        var dir = TempDir("cursor-pane");
        PaneRuntime? runtime = null;
        try
        {
            var target = Path.Combine(dir, "cursor-agent");
            var link = Path.Combine(dir, "agent");
            await File.WriteAllTextAsync(target, "#!/bin/sh\nsleep 30\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    target,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.CreateSymbolicLink(link, target);

            runtime = await StartPaneAsync(link);
            var info = await WaitForAgentAsync(runtime, "cursor");
            Assert.Equal("cursor", info.Command);
            Assert.True(AgentKindCatalog.TryResolve(info.Command, out var kind));
            Assert.Equal("cursor", kind);
        }
        finally
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Pane_reports_agent_from_foreground_group_sibling()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix pane process detection.");
        PaneRuntime? runtime = null;
        try
        {
            runtime = await StartPaneAsync(
                "/bin/bash",
                "-c",
                "bash -c 'exec -a codex sleep 30' & wait");
            var info = await WaitForAgentAsync(runtime, "codex");
            var job = UnixPaneProcessInfoProbe.TryCollectForegroundJob(runtime.Pid!.Value, info.GroupId);
            Assert.NotNull(job);
            Assert.True(job.Processes.Count >= 2, "foreground group has " + job.Processes.Count + " process");
            Assert.Equal("codex", info.Command);
        }
        finally
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
        }
    }

    [Fact]
    public void Probe_reports_pi_for_node_package_cli_and_basename()
    {
        var dir = TempDir("pi-cli");
        try
        {
            var script = Path.Combine(
                dir,
                "node_modules",
                "@earendil-works",
                "pi-coding-agent",
                "dist",
                "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "// fake pi cli\n");

            var packageCli = UnixPaneProcessInfoProbe.CommandNameFromArgv(["node", script]);
            var basename = UnixPaneProcessInfoProbe.CommandNameFromArgv(
                ["node", Path.Combine(dir, "pi")]);

            Assert.Equal("pi", packageCli);
            Assert.Equal("pi", basename);
            Assert.True(AgentKindCatalog.TryResolve(packageCli, out var kind));
            Assert.Equal("pi", kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ForegroundJobSnapshot Job(int groupId, params ForegroundProcessSnapshot[] processes) =>
        new() { ProcessGroupId = groupId, Processes = processes };

    private static ForegroundProcessSnapshot Proc(int pid, string name, params string[] argv) =>
        new()
        {
            Pid = pid,
            Name = name,
            Argv = argv,
            Cmdline = string.Join(' ', argv),
        };

    private static void AssertKind(
        ForegroundJobSnapshot job,
        string? expected,
        string? because = null,
        IAgentPathCanonicalizer? paths = null)
    {
        var found = AgentProcessResolver.TryIdentifyInJob(job, out var kind, paths);
        if (expected is null)
        {
            Assert.False(found, because);
            return;
        }

        Assert.True(found, because);
        Assert.Equal(expected, kind);
    }

    private static string TempDir(string name)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "hypa-detect-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string PtyForkScript =
        """
        import os, pty, sys
        pid, fd = pty.fork()
        if pid == 0:
            try:
                os.execvp(sys.argv[1], sys.argv[1:])
            except OSError:
                os._exit(127)
        else:
            os.write(1, ("%d\n" % pid).encode())
            try:
                os.waitpid(pid, 0)
            except OSError:
                pass
        """;

    private static async Task<PaneRuntime> StartPaneAsync(string command, params string[] args)
    {
        var runtime = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = command,
                Args = args,
                Cols = 80,
                Rows = 24,
            },
            SpawnOnPty,
            new StubVtEngine());
        runtime.ProcessInfoProbe = new UnixPaneProcessInfoProbe();
        await runtime.StartAsync(CancellationToken.None);
        return runtime;
    }

    private static IPtyProcess SpawnOnPty(string file, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "python3",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(PtyForkScript);
        start.ArgumentList.Add(file);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("python3 did not start.");
        var line = process.StandardOutput.ReadLine();
        if (!int.TryParse(line, out var childPid) || childPid <= 0)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException("pty child pid was '" + line + "'. " + error);
        }

        return new PtyChildProcess(process, childPid);
    }

    private static async Task<PaneForegroundInfo> WaitForAgentAsync(PaneRuntime runtime, string agent)
    {
        var probe = runtime.ProcessInfoProbe ?? new UnixPaneProcessInfoProbe();
        PaneForegroundInfo? info = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var pid = runtime.Pid;
            if (pid is int live)
                info = probe.TryGetForegroundInfo(live);
            if (info?.Command == agent)
                return info;
            await Task.Delay(50);
        }

        Assert.Fail(
            "pane pid " + runtime.Pid
            + " reported '" + (info?.Command ?? "")
            + "' for agent " + agent);
        return info!;
    }

    private sealed class PtyChildProcess : IPtyProcess
    {
        private readonly Process _parent;

        public PtyChildProcess(Process parent, int childPid)
        {
            _parent = parent;
            Pid = childPid;
        }

        public int Pid { get; }

        public bool IsRunning => !_parent.HasExited;

        public int? ExitCode => _parent.HasExited ? _parent.ExitCode : null;

        public Stream StandardInput => Stream.Null;

        public Stream StandardOutput => Stream.Null;

        public void Resize(int cols, int rows)
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => _parent.WaitForExitAsync(ct);

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_parent.HasExited)
                    _parent.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            _parent.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
