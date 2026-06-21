using System.CommandLine;
using Hypa.Cli.Commands;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Hooks;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

[Collection("SequentialEnvTests")]
public sealed class InitCommandStorageTests
{
    [Fact]
    public async Task InitCommand_StorageProvisionFailureWithoutAgent_PrintsGenericStorageHint()
    {
        var dataOptions = new HypaDataOptions { DataDirectory = "/tmp/hypa-data" };
        var (exitCode, stderr) = await InvokeStorageFailureAsync(["--global"], dataOptions);

        Assert.Equal(1, exitCode);
        Assert.Contains("Permission denied", stderr);
        Assert.Contains("Ensure this path exists and is writable", stderr);
        Assert.Contains("/tmp/hypa-data", stderr);
        Assert.DoesNotContain("~/.codex/config.toml", stderr);
    }

    [Fact]
    public async Task InitCommand_StorageProvisionFailureForCodex_PrintsCodexSandboxHint()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var dataOptions = new HypaDataOptions { DataDirectory = @"C:\Users\matthew\Hypa ""Data""" };
        var codexHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "codex-home");
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            var (exitCode, stderr) = await InvokeStorageFailureAsync(["--global", "--agent", "codex"], dataOptions);

            Assert.Equal(1, exitCode);
            Assert.Contains("Permission denied", stderr);
            Assert.Contains("If running inside a Codex sandbox", stderr);
            Assert.Contains(Path.Combine(codexHome, "config.toml"), stderr);
            Assert.Contains(@"C:\Users\matthew\Hypa ""Data""", stderr);
            Assert.Contains("      \"C:\\\\Users\\\\matthew\\\\Hypa \\\"Data\\\"\"", stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    private static async Task<(int ExitCode, string Stderr)> InvokeStorageFailureAsync(
        string[] args,
        HypaDataOptions dataOptions)
    {
        var service = BuildServiceWithStorageFailure();

        var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        try
        {
            var exitCode = await new InitCommand(service, dataOptions).Build().InvokeAsync(args);
            return (exitCode, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static InitService BuildServiceWithStorageFailure()
    {
        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns("codex");
        adapter.IsAvailable().Returns(true);
        adapter.GetInstallPlan(Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>()).Returns(new InstallPlan([]));

        var registry = Substitute.For<IHarnessRegistry>();
        registry.All.Returns([adapter]);
        registry.Find("codex").Returns(adapter);

        var installer = Substitute.For<IHookInstaller>();
        var rootDetector = Substitute.For<IProjectRootDetector>();
        rootDetector.Detect(Arg.Any<string>()).Returns("/repo/root");

        var projectRegistry = Substitute.For<IProjectRegistry>();

        var provisioner = Substitute.For<IStorageProvisioner>();
        provisioner.ProvisionAsync(Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Fail(new Error("storage.access_denied", "Permission denied to ~/.hypa")));

        return new InitService(registry, installer, rootDetector, projectRegistry, provisioner);
    }
}
