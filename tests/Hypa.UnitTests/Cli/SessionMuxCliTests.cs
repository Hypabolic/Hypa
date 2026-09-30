using System.CommandLine;
using Hypa.Cli.Attach;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SessionMuxCliTests
{
    [Fact]
    public async Task Session_list_prints_discovered_sessions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "h33-list-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "alpha"));
        File.WriteAllText(
            Path.Combine(dir, "alpha", "runtime.status.json"),
            """{"session":"alpha","socket":"/tmp/alpha.sock","cwd":"/tmp","pid":42}""");
        try
        {
            var (root, _, _) = BuildRoot(dir);
            var exit = await root.Parse(["session", "list"]).InvokeAsync();
            Assert.Equal(0, exit);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }

    [Fact]
    public async Task Session_attach_guid_hits_compression_session_service()
    {
        var (root, repo, _) = BuildRoot();
        var id = Guid.NewGuid();
        repo.LoadAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Ok(new ContextSession
            {
                Id = id,
                ProjectRoot = "/tmp",
            }));

        var exit = await root.Parse(["session", "attach", id.ToString()]).InvokeAsync();
        Assert.Equal(0, exit);
        await repo.Received(1).LoadAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Session_attach_name_calls_mux_attach()
    {
        var driver = Substitute.For<IMuxAttachDriver>();
        driver.RunAsync(Arg.Any<MuxReadyInfo>(), Arg.Any<MuxAttachRequest>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var (root, repo, supervisor) = BuildRootWithDriver(driver);

        var exit = await root.Parse(["session", "attach", "work"]).InvokeAsync();
        Assert.Equal(0, exit);
        await supervisor.Received(1).EnsureReadyAsync(
            "work", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().LoadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Session_delete_removes_stopped_session_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "h33-del-" + Guid.NewGuid().ToString("N")[..8]);
        var sessionDir = Path.Combine(dir, "gone");
        Directory.CreateDirectory(sessionDir);
        try
        {
            var catalog = new MuxSessionCatalog(dir);
            var (root, _, _) = BuildRoot(catalog);
            var exit = await root.Parse(["session", "delete", "gone"]).InvokeAsync();
            Assert.Equal(0, exit);
            Assert.False(Directory.Exists(sessionDir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }

    [Fact]
    public async Task Session_stop_named_other_with_env_socket_fails_closed()
    {
        var prev = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", "/tmp/hypa-env-override.sock");
            var (root, _, _) = BuildRoot();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            int exit;
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                exit = await root.Parse(["session", "stop", "other"]).InvokeAsync();
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }

            Assert.Equal(2, exit);
            Assert.Contains("HYPA_RUNTIME_SOCKET", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("other", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", prev);
        }
    }

    [Fact]
    public void Status_command_is_registered()
    {
        var (root, _, _) = BuildRoot();
        var status = root.Subcommands.Single(c => c.Name == "status");
        Assert.Contains(status.Subcommands, c => c.Name == "client");
        Assert.Contains(status.Subcommands, c => c.Name == "server");
    }

    private static (RootCommand Root, ISessionRepository Repo, IMuxSupervisor Supervisor) BuildRoot(
        string? catalogRoot = null) =>
        BuildRoot(new MuxSessionCatalog(catalogRoot));

    private static (RootCommand Root, ISessionRepository Repo, IMuxSupervisor Supervisor) BuildRoot(
        MuxSessionCatalog catalog) =>
        BuildRootWithDriver(new StubAttachDriver(), catalog);

    private static (RootCommand Root, ISessionRepository Repo, IMuxSupervisor Supervisor) BuildRootWithDriver(
        IMuxAttachDriver driver,
        MuxSessionCatalog? catalog = null)
    {
        catalog ??= new MuxSessionCatalog(Path.Combine(Path.GetTempPath(), "h33-empty"));
        var repo = Substitute.For<ISessionRepository>();
        var sessions = new SessionService(repo, Substitute.For<ISessionResolver>());
        var supervisor = Substitute.For<IMuxSupervisor>();
        supervisor.EnsureReadyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new MuxReadyInfo(ci.ArgAt<string>(0), "/tmp/hypa.sock", """{"ok":true,"protocol":1}"""));
        var attach = new MuxAttachService(supervisor, driver);
        var root = new RootCommand("hypa");
        root.Add(new SessionCommand(sessions, attach, catalog).Build());
        root.Add(new StatusCommand(catalog).Build());
        root.Add(new ApiCommand().Build());
        return (root, repo, supervisor);
    }

    private sealed class StubAttachDriver : IMuxAttachDriver
    {
        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct) =>
            Task.FromResult(0);
    }
}
