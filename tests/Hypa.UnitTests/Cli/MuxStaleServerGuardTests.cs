using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxStaleServerGuardTests
{
    private const string Current = """{"ok":true,"protocol":1,"version":"1.0.6","install_present":true}""";
    private const string Removed = """{"ok":true,"protocol":1,"version":"1.0.3","install_present":false}""";
    private const string Older = """{"ok":true,"protocol":1,"version":"1.0.5","install_present":true}""";
    private const string Legacy = """{"ok":true,"protocol":1}""";

    [Fact]
    public void Matching_version_is_not_stale()
    {
        var check = MuxServerVersionCheck.FromPing(Current, "1.0.6")!;

        Assert.False(check.IsStale);
    }

    [Fact]
    public void Removed_install_is_stale_and_explains_failing_panes()
    {
        var check = MuxServerVersionCheck.FromPing(Removed, "1.0.6")!;

        Assert.True(check.IsStale);
        Assert.True(check.InstallRemoved);
        var text = check.Describe("default");
        Assert.Contains("Hypa 1.0.3", text, StringComparison.Ordinal);
        Assert.Contains("removed by an upgrade", text, StringComparison.Ordinal);
        Assert.Contains("New tabs and panes will fail", text, StringComparison.Ordinal);
        Assert.Contains("This client is Hypa 1.0.6", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_version_is_stale()
    {
        var check = MuxServerVersionCheck.FromPing(Older, "1.0.6")!;

        Assert.True(check.IsStale);
        Assert.False(check.InstallRemoved);
        Assert.DoesNotContain("will fail", check.Describe("default"), StringComparison.Ordinal);
    }

    [Fact]
    public void Server_without_a_version_is_stale()
    {
        var check = MuxServerVersionCheck.FromPing(Legacy, "1.0.6")!;

        Assert.True(check.IsStale);
        Assert.True(check.VersionUnknown);
        Assert.Contains("an older Hypa", check.Describe("default"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json", "1.0.6")]
    [InlineData(Current, null)]
    [InlineData(null, "1.0.6")]
    public void Unreadable_input_gives_no_check(string? ping, string? client) =>
        Assert.Null(MuxServerVersionCheck.FromPing(ping, client));

    [Fact]
    public async Task Current_mux_is_left_alone()
    {
        var (guard, error, stops) = Guard(interactive: true, answer: "y");

        Assert.False(await guard.TryRestartAsync(Ready(Current), once: false));
        Assert.Empty(stops);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Non_interactive_attach_warns_without_stopping()
    {
        var (guard, error, stops) = Guard(interactive: false, answer: "y");

        Assert.False(await guard.TryRestartAsync(Ready(Removed), once: false));
        Assert.Empty(stops);
        Assert.Contains(MuxStaleServerGuard.RestartHint("default", "/run/hypa.sock"), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_hint_targets_the_checked_session()
    {
        var (guard, error, _) = Guard(interactive: false, answer: "y");
        var socket = UnixSocketServer.ResolveSocketPath("work", honorEnvironment: false);

        await guard.TryRestartAsync(new MuxReadyInfo("work", socket, Removed), once: false);

        var text = error.ToString();
        Assert.Contains("`hypa mux stop --session work`", text, StringComparison.Ordinal);
        Assert.Contains("`hypa attach --session work`", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/run/hypa.sock", "hypa mux stop --socket /run/hypa.sock")]
    [InlineData("/run/my mux/it's.sock", "hypa mux stop --socket '/run/my mux/it'\\''s.sock'")]
    public void Restart_stop_names_a_custom_socket(string socket, string expected) =>
        Assert.Equal(expected, MuxRestartCommands.Stop("work", socket));

    [Fact]
    public async Task Once_attach_warns_without_prompting()
    {
        var (guard, error, stops) = Guard(interactive: true, answer: "y");

        Assert.False(await guard.TryRestartAsync(Ready(Removed), once: true));
        Assert.Empty(stops);
        Assert.DoesNotContain("[y/N]", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declined_prompt_keeps_the_running_mux()
    {
        var (guard, error, stops) = Guard(interactive: true, answer: "n");

        Assert.False(await guard.TryRestartAsync(Ready(Removed), once: false));
        Assert.Empty(stops);
        var text = error.ToString();
        Assert.Contains(MuxStaleServerGuard.RestartImpact, text, StringComparison.Ordinal);
        Assert.Contains("[y/N]", text, StringComparison.Ordinal);
        Assert.Contains(MuxStaleServerGuard.KeepCopy("default", "/run/hypa.sock"), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepted_prompt_stops_the_mux_for_a_fresh_start()
    {
        var (guard, _, stops) = Guard(interactive: true, answer: "y");

        Assert.True(await guard.TryRestartAsync(Ready(Removed), once: false));
        Assert.Equal([("default", "/run/hypa.sock")], stops);
    }

    [Fact]
    public async Task Failed_stop_attaches_to_the_running_mux()
    {
        var error = new StringWriter();
        var guard = new MuxStaleServerGuard(
            new StringReader("y\n"),
            error,
            interactive: true,
            clientVersion: "1.0.6",
            stop: (_, _) => Task.FromResult(false));

        Assert.False(await guard.TryRestartAsync(Ready(Removed), once: false));
        Assert.Contains(MuxStaleServerGuard.StopFailedCopy, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_starts_a_fresh_mux_after_a_restart()
    {
        var supervisor = new SequenceSupervisor(Ready(Removed), Ready(Current));
        var driver = new RecordingDriver();
        var (guard, _, _) = Guard(interactive: true, answer: "y");
        var attach = new MuxAttachService(supervisor, driver, staleGuard: guard);

        var exit = await attach.AttachAsync(null, null, false, null, false, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(2, supervisor.Calls);
        Assert.Equal(Current, driver.Ready!.PingJson);
    }

    private static (MuxStaleServerGuard Guard, StringWriter Error, List<(string, string)> Stops) Guard(
        bool interactive,
        string answer)
    {
        var error = new StringWriter();
        var stops = new List<(string, string)>();
        var guard = new MuxStaleServerGuard(
            new StringReader(answer + "\n"),
            error,
            interactive,
            "1.0.6",
            (session, socket) =>
            {
                stops.Add((session, socket));
                return Task.FromResult(true);
            });
        return (guard, error, stops);
    }

    private static MuxReadyInfo Ready(string ping) => new("default", "/run/hypa.sock", ping);

    private sealed class SequenceSupervisor(params MuxReadyInfo[] results) : IMuxSupervisor
    {
        public int Calls { get; private set; }

        public Task<MuxReadyInfo> EnsureReadyAsync(
            string session,
            string? cwd,
            string? socketOverride,
            CancellationToken ct) =>
            Task.FromResult(results[Math.Min(Calls++, results.Length - 1)]);
    }

    private sealed class RecordingDriver : Hypa.Cli.Attach.IMuxAttachDriver
    {
        public MuxReadyInfo? Ready { get; private set; }

        public Task<int> RunAsync(MuxReadyInfo ready, Hypa.Cli.Attach.MuxAttachRequest request, CancellationToken ct)
        {
            Ready = ready;
            return Task.FromResult(0);
        }
    }
}
