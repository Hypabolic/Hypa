using System.CommandLine;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli;
using Hypa.Cli.DI;
using Hypa.Cli.Mux;
using Hypa.Infrastructure.DI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxReleaseCommandRootTests
{
    private static readonly SemaphoreSlim EnvGate = new(1, 1);

    [Fact]
    public async Task MuxRelease_ShipsWorkAndDevice_ButNotRendezvous_WithoutStoreAccess()
    {
        await EnvGate.WaitAsync();
        var state = Path.Combine(Path.GetTempPath(), "hypa-mux-rel-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var previous = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", state);
        try
        {
            var root = BuildProductRoot();

            var rendezvous = root.Parse(["rendezvous", "join"]);
            Assert.NotEmpty(rendezvous.Errors);
            Assert.NotEqual("rendezvous", rendezvous.CommandResult.Command.Name);
            Assert.NotEqual("rendezvous", rendezvous.CommandResult.Command.Parents.OfType<Command>().FirstOrDefault()?.Name);

            foreach (var args in new[] { new[] { "work", "placements", "list", "--identity", "me" }, new[] { "device", "list" } })
            {
                var parsed = root.Parse(args);
                Assert.Empty(parsed.Errors);
                Assert.Equal("list", parsed.CommandResult.Command.Name);
            }

            var captured = new StringWriter();
            var oldErr = Console.Error;
            Console.SetError(captured);
            try
            {
                Assert.NotEqual(0, await rendezvous.InvokeAsync());
                Assert.NotEqual(0, await root.Parse(["work", "status", "x"]).InvokeAsync());
            }
            finally
            {
                Console.SetError(oldErr);
            }

            Assert.DoesNotContain("not in v0", captured.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(state, "hypa", "continuity")));
            Assert.False(Directory.Exists(Path.Combine(state, "hypa", "placements")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", previous);
            EnvGate.Release();
            TryDelete(state);
        }
    }

    [Fact]
    public async Task MuxRelease_GlobalPrintFlagsFollowContract()
    {
        await EnvGate.WaitAsync();
        var state = Path.Combine(Path.GetTempPath(), "hypa-mux-rel-print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var previous = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", state);
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        try
        {
            var tomlOut = new StringWriter();
            Console.SetOut(tomlOut);
            Console.SetError(new StringWriter());
            var tomlExit = await BuildProductRoot().Parse(["--default-config"]).InvokeAsync();
            Assert.Equal(0, tomlExit);
            var toml = tomlOut.ToString();
            Assert.Contains("[ui]", toml, StringComparison.Ordinal);
            Assert.DoesNotContain("[ui.sidebar.cubes]", toml, StringComparison.Ordinal);
            Assert.Equal(new FileAttachConfigLoader().DefaultToml(), AttachConfigDefaults.Toml);

            var skill = RuntimeSkillPrinter.Text();
            Assert.Contains("name: hypa-runtime", skill, StringComparison.Ordinal);

            var workExit = await BuildProductRoot().Parse(["work", "status", "x"]).InvokeAsync();
            Assert.NotEqual(0, workExit);

            Assert.False(Directory.Exists(Path.Combine(state, "hypa", "continuity")));
            Assert.False(Directory.Exists(Path.Combine(state, "hypa", "placements")));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", previous);
            EnvGate.Release();
            TryDelete(state);
        }
    }

    [Fact]
    public void MuxRelease_ProductCapabilityDisablesContinuity()
    {
        Assert.False(MuxReleaseCapability.Product.ContinuityEnabled);
        Assert.True(MuxReleaseCapability.FullProfile.ContinuityEnabled);

        var names = new[]
        {
            "HYPA_CONFIG_PATH",
            "HYPA_SESSION",
            "HYPA_ENV",
            "HYPA_ATTACH_ROUTE_TRACE",
            "HYPA_RUNTIME_STATE_DIR",
            "HYPA_RUNTIME_SOCKET",
            "XDG_STATE_HOME",
            "XDG_CONFIG_HOME",
        };
        var previous = names.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var name in names)
                Environment.SetEnvironmentVariable(name, "/tmp/hypa-mux-rel-cap-" + name);
            Assert.False(MuxReleaseCapability.Product.ContinuityEnabled);
            Assert.Same(MuxReleaseCapability.Product, MuxReleaseCapability.Product);
        }
        finally
        {
            foreach (var pair in previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static RootCommand BuildProductRoot()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure();
        services.AddCli();
        return services.BuildServiceProvider().GetRequiredService<RootCommand>();
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
