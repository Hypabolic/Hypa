using System.CommandLine;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli.Attach;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachConfigFailClosedTests
{
    [Fact]
    public async Task Mux_stop_invalid_config_exits_1_without_default_session()
    {
        var (exit, stdout, stderr) = await InvokeAsync(
            new MuxCommand(new FailLoader()).Build(),
            ["stop"]);
        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr, StringComparison.Ordinal);
        Assert.Contains("keys.prefix", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("default", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopped mux", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("No mux server status", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_invalid_config_exits_1_without_default_session()
    {
        var catalog = new MuxSessionCatalog(Path.Combine(Path.GetTempPath(), "h35-status-fail"));
        var (exit, stdout, stderr) = await InvokeAsync(
            new StatusCommand(catalog, new FailLoader()).Build(),
            []);
        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("client.session=", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("server.session=", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_snapshot_invalid_config_exits_1_without_connect()
    {
        var (exit, stdout, stderr) = await InvokeAsync(
            new ApiCommand(new FailLoader()).Build(),
            ["snapshot"]);
        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to connect", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("default", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mux_stop_explicit_session_invalid_config_exits_1_without_stop()
    {
        var (exit, stdout, stderr) = await InvokeAsync(
            new MuxCommand(new FailLoader()).Build(),
            ["stop", "--session", "work"]);
        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopped mux", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("No mux server status", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("work", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_snapshot_explicit_session_invalid_config_exits_1_without_connect()
    {
        var (exit, stdout, stderr) = await InvokeAsync(
            new ApiCommand(new FailLoader()).Build(),
            ["snapshot", "--session", "work"]);
        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to connect", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("work", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_stop_invalid_config_exits_1_without_default_session()
    {
        var catalog = new MuxSessionCatalog(Path.Combine(Path.GetTempPath(), "h35-session-fail"));
        var repo = Substitute.For<ISessionRepository>();
        var sessions = new SessionService(repo, Substitute.For<ISessionResolver>());
        var supervisor = Substitute.For<IMuxSupervisor>();
        var attach = new MuxAttachService(supervisor, new StubDriver(), new FailLoader());
        var cmd = new SessionCommand(sessions, attach, catalog, new FailLoader()).Build();
        var (exit, stdout, stderr) = await InvokeAsync(cmd, ["stop"]);
        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopped mux", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("No mux server status", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("default", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_stop_named_invalid_config_exits_1_without_stop()
    {
        var catalog = new MuxSessionCatalog(Path.Combine(Path.GetTempPath(), "h35-session-fail-named"));
        var repo = Substitute.For<ISessionRepository>();
        var sessions = new SessionService(repo, Substitute.For<ISessionResolver>());
        var supervisor = Substitute.For<IMuxSupervisor>();
        var attach = new MuxAttachService(supervisor, new StubDriver(), new FailLoader());
        var cmd = new SessionCommand(sessions, attach, catalog, new FailLoader()).Build();
        var (exit, stdout, stderr) = await InvokeAsync(cmd, ["stop", "work"]);
        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopped mux", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("No mux server status", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("work", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_uses_session_name_from_config()
    {
        var catalog = new MuxSessionCatalog(Path.Combine(Path.GetTempPath(), "h35-status-name"));
        var previous = Environment.GetEnvironmentVariable(AttachSessionResolver.SessionEnv);
        try
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, null);
            var (exit, stdout, stderr) = await InvokeAsync(
                new StatusCommand(catalog, new StaticLoader("work")).Build(),
                ["client"]);
            Assert.Equal(0, exit);
            Assert.Contains("client.session=work", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("config: issues found", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, previous);
        }
    }

    [Fact]
    public async Task Attach_invalid_config_exits_1_without_ensure_ready()
    {
        var supervisor = Substitute.For<IMuxSupervisor>();
        var service = new MuxAttachService(supervisor, new StubDriver(), new FailLoader());
        var stderr = new StringWriter();
        var oldErr = Console.Error;
        int exit;
        Console.SetError(stderr);
        try
        {
            exit = await service.AttachAsync(
                sessionOption: null,
                cwd: Environment.CurrentDirectory,
                once: true,
                socketOverride: null,
                sessionOptionWasSet: false,
                CancellationToken.None);
        }
        finally
        {
            Console.SetError(oldErr);
        }

        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr.ToString(), StringComparison.Ordinal);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attach_explicit_session_invalid_config_exits_1_without_ensure_ready()
    {
        var supervisor = Substitute.For<IMuxSupervisor>();
        var service = new MuxAttachService(supervisor, new StubDriver(), new FailLoader());
        var stderr = new StringWriter();
        var oldErr = Console.Error;
        int exit;
        Console.SetError(stderr);
        try
        {
            exit = await service.AttachAsync(
                sessionOption: "work",
                cwd: null,
                once: true,
                socketOverride: null,
                sessionOptionWasSet: true,
                CancellationToken.None);
        }
        finally
        {
            Console.SetError(oldErr);
        }

        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr.ToString(), StringComparison.Ordinal);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attach_once_unencodable_prefix_exits_1_without_ensure_ready()
    {
        var supervisor = Substitute.For<IMuxSupervisor>();
        var service = new MuxAttachService(
            supervisor,
            new StubDriver(),
            new TomlLoader("""
                [keys]
                prefix = "f20"
                """));
        var stderr = new StringWriter();
        var oldErr = Console.Error;
        int exit;
        Console.SetError(stderr);
        try
        {
            exit = await service.AttachAsync(
                sessionOption: null,
                cwd: Environment.CurrentDirectory,
                once: true,
                socketOverride: null,
                sessionOptionWasSet: false,
                CancellationToken.None);
        }
        finally
        {
            Console.SetError(oldErr);
        }

        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("config [keys]:", stderr.ToString(), StringComparison.Ordinal);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attach_once_duplicate_chords_exits_1_without_ensure_ready()
    {
        var supervisor = Substitute.For<IMuxSupervisor>();
        var service = new MuxAttachService(
            supervisor,
            new StubDriver(),
            new TomlLoader("""
                [keys]
                detach = "prefix+x"
                """));
        var stderr = new StringWriter();
        var oldErr = Console.Error;
        int exit;
        Console.SetError(stderr);
        try
        {
            exit = await service.AttachAsync(
                sessionOption: "work",
                cwd: null,
                once: true,
                socketOverride: null,
                sessionOptionWasSet: true,
                CancellationToken.None);
        }
        finally
        {
            Console.SetError(oldErr);
        }

        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("config [keys]:", stderr.ToString(), StringComparison.Ordinal);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attach_invalid_ui_accent_exits_1_without_ensure_ready()
    {
        var supervisor = Substitute.For<IMuxSupervisor>();
        var service = new MuxAttachService(
            supervisor,
            new StubDriver(),
            new TomlLoader("""
                [ui]
                accent = "not-a-color"
                """));
        var stderr = new StringWriter();
        var oldErr = Console.Error;
        int exit;
        Console.SetError(stderr);
        try
        {
            exit = await service.AttachAsync(
                sessionOption: null,
                cwd: Environment.CurrentDirectory,
                once: true,
                socketOverride: null,
                sessionOptionWasSet: false,
                CancellationToken.None);
        }
        finally
        {
            Console.SetError(oldErr);
        }

        Assert.Equal(1, exit);
        Assert.Contains("config: issues found", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("ui.accent", stderr.ToString(), StringComparison.Ordinal);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Attach_theme_from_config_rejects_invalid_name()
    {
        var result = AttachSession.CreateThemeRuntime(AttachClientConfig.Default with
        {
            Theme = new AttachThemeConfig { Name = "tokyonight" },
        });
        Assert.False(result.IsOk);
        Assert.Equal("theme.name", result.Error.Key);
        Assert.DoesNotContain("catppuccin", result.Error.Message, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> InvokeAsync(
        Command command,
        string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        int exit;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            exit = await command.Parse(args).InvokeAsync();
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }

        return (exit, stdout.ToString(), stderr.ToString());
    }

    private sealed class StaticLoader(string sessionName) : IAttachConfigLoader
    {
        public string ResolvePath() => "/tmp/attach-static.toml";

        public AttachConfigResult<AttachClientConfig> Load() =>
            AttachConfigResult<AttachClientConfig>.Ok(AttachClientConfig.Default with
            {
                Session = new AttachSessionConfig { Name = sessionName },
            });

        public AttachConfigResult<AttachClientConfig> Parse(string text) => Load();

        public string DefaultToml() => AttachConfigDefaults.Toml;

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Ok(
                new AttachConfigResetResult("ok", ResolvePath(), null, false));
    }

    private sealed class TomlLoader(string text) : IAttachConfigLoader
    {
        public string ResolvePath() => "/tmp/attach-compile.toml";

        public AttachConfigResult<AttachClientConfig> Load() =>
            TomlAttachConfigBinder.Bind(text);

        public AttachConfigResult<AttachClientConfig> Parse(string parseText) =>
            TomlAttachConfigBinder.Bind(parseText);

        public string DefaultToml() => AttachConfigDefaults.Toml;

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Ok(
                new AttachConfigResetResult("ok", ResolvePath(), null, false));
    }

    private sealed class FailLoader : IAttachConfigLoader
    {
        public string ResolvePath() => "/tmp/attach-fail.toml";

        public AttachConfigResult<AttachClientConfig> Load() =>
            AttachConfigResult<AttachClientConfig>.Fail(
                AttachConfigError.Value("keys.prefix", "keys.prefix is required.", 2));

        public AttachConfigResult<AttachClientConfig> Parse(string text) => Load();

        public string DefaultToml() => AttachConfigDefaults.Toml;

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Fail(
                AttachConfigError.Value("keys.prefix", "keys.prefix is required.", 2));
    }

    private sealed class StubDriver : IMuxAttachDriver
    {
        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct) =>
            Task.FromResult(0);
    }
}
