using System.CommandLine;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli.Attach;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PackChannelStatusTests
{
    [Fact]
    public async Task Status_client_prints_pack_channel()
    {
        var catalog = new MuxSessionCatalog(Path.Combine(Path.GetTempPath(), "pack-channel-f1"));
        var previous = Environment.GetEnvironmentVariable(AttachSessionResolver.SessionEnv);
        try
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, null);
            var (exit, stdout, stderr) = await InvokeAsync(
                new StatusCommand(catalog, new NamedLoader("work")).Build(),
                ["client"]);
            Assert.Equal(0, exit);
            Assert.Contains("pack.channel=" + PackChannelStatus.Report(), stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("config: issues found", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, previous);
        }
    }

    [Fact]
    public void Status_command_does_not_add_channel_set()
    {
        var catalog = new MuxSessionCatalog(Path.Combine(Path.GetTempPath(), "pack-channel-names"));
        var cmd = new StatusCommand(catalog, new NamedLoader("work")).Build();
        Assert.Equal("status", cmd.Name);
        Assert.DoesNotContain(cmd.Subcommands, c => c.Name == "channel");
        Assert.DoesNotContain(cmd.Subcommands, c => c.Name == "set");
    }

    [Fact]
    public void Report_maps_missing_or_other_marker_to_f1()
    {
        var marker = PackChannelStatus.ReadMarker();
        var expected = string.Equals(marker, PackChannelStatus.F2, StringComparison.Ordinal)
            ? PackChannelStatus.F2
            : PackChannelStatus.F1;
        Assert.Equal(expected, PackChannelStatus.Report());
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

    private sealed class NamedLoader(string sessionName) : IAttachConfigLoader
    {
        public string ResolvePath() => "/tmp/pack-channel.toml";

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
}
