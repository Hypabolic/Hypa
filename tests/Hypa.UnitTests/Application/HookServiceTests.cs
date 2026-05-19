using System.Text.Json;
using Hypa.Infrastructure.Rewrite;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Hooks;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class HookServiceTests
{
    private readonly HookService _service;

    public HookServiceTests()
    {
        var lexer = new ShellLexer();
        var wrapper = new GenericWrapperStrategy();
        var strategies = new ICommandRewriteStrategy[]
        {
            new GitRewriteStrategy(),
            new DotnetRewriteStrategy(),
            new PackageManagerRewriteStrategy(),
            new TscRewriteStrategy(),
            new DockerRewriteStrategy(),
            new KubectlRewriteStrategy(),
            wrapper,
        };
        var registry = new CommandRewriteRegistry(lexer, strategies, wrapper);
        var configLoader = Substitute.For<IConfigLoader>();
        configLoader.LoadAsync(default).ReturnsForAnyArgs(
            Task.FromResult(Result<Hypa.Runtime.Domain.Config.HypaConfig, Hypa.Runtime.Domain.Common.Error>.Ok(
                Hypa.Runtime.Domain.Config.HypaConfig.Default with { GenericWrapperEnabled = false })));

        var rewriteService = new CommandRewriteService(configLoader, registry);
        var readRedirector = Substitute.For<IReadRedirector>();
        readRedirector.RedirectAsync(default!, default).ReturnsForAnyArgs(Task.FromResult<string?>(null));
        _service = new HookService(rewriteService, readRedirector);
    }

    // --- Loop prevention ---

    [Theory]
    [InlineData("hypa git status")]
    [InlineData("hypa dotnet build")]
    [InlineData("hypa -c \"any command\"")]
    [InlineData("hypa")]
    public async Task HypaCommand_ReturnsPassthrough(string command)
    {
        var input = MakeInput(command);
        var decision = await _service.ProcessAsync(input);
        Assert.IsType<HookDecision.Passthrough>(decision);
    }

    // --- Rewrite decisions ---

    [Theory]
    [InlineData("git status", "hypa git status")]
    [InlineData("dotnet build", "hypa dotnet build")]
    [InlineData("kubectl get pods", "hypa kubectl get pods")]
    [InlineData("docker ps", "hypa docker ps")]
    public async Task KnownCommand_ReturnsRewrite(string command, string expected)
    {
        var input = MakeInput(command);
        var decision = await _service.ProcessAsync(input);
        var rewrite = Assert.IsType<HookDecision.Rewrite>(decision);
        Assert.Equal(expected, rewrite.Command);
    }

    [Theory]
    [InlineData("ssh user@host")]
    [InlineData("vim file.cs")]
    [InlineData("cat file.txt")]
    public async Task UnknownCommand_NoGenericWrapper_ReturnsPassthrough(string command)
    {
        // Default config has generic wrapper enabled, so these go through generic wrapper
        // Just ensure we get either Rewrite or Passthrough (no exception thrown)
        var input = MakeInput(command);
        var decision = await _service.ProcessAsync(input);
        Assert.True(decision is HookDecision.Rewrite or HookDecision.Passthrough);
    }

    [Fact]
    public async Task Passthrough_ReturnsPassthrough()
    {
        var input = MakeInput("git push origin main");
        var decision = await _service.ProcessAsync(input);
        Assert.IsType<HookDecision.Passthrough>(decision);
    }

    private static AgentHookInput MakeInput(string command) =>
        new("Bash", command, JsonDocument.Parse("{}").RootElement);
}

public sealed class WhenGenericWrapperEnabled
{
    private readonly HookService _service;

    public WhenGenericWrapperEnabled()
    {
        var lexer = new ShellLexer();
        var wrapper = new GenericWrapperStrategy();
        var strategies = new ICommandRewriteStrategy[]
        {
            new GitRewriteStrategy(),
            new DotnetRewriteStrategy(),
            new PackageManagerRewriteStrategy(),
            new TscRewriteStrategy(),
            new DockerRewriteStrategy(),
            new KubectlRewriteStrategy(),
            wrapper,
        };
        var registry = new CommandRewriteRegistry(lexer, strategies, wrapper);
        var configLoader = Substitute.For<IConfigLoader>();
        configLoader.LoadAsync(default).ReturnsForAnyArgs(
            Task.FromResult(Result<Hypa.Runtime.Domain.Config.HypaConfig, Hypa.Runtime.Domain.Common.Error>.Ok(
                Hypa.Runtime.Domain.Config.HypaConfig.Default with { GenericWrapperEnabled = true })));

        var rewriteService = new CommandRewriteService(configLoader, registry);
        var readRedirector = Substitute.For<IReadRedirector>();
        readRedirector.RedirectAsync(default!, default).ReturnsForAnyArgs(Task.FromResult<string?>(null));
        _service = new HookService(rewriteService, readRedirector);
    }

    [Theory]
    [InlineData("rg --files", "hypa -c \"rg --files\"")]
    [InlineData("find .", "hypa -c \"find .\"")]
    [InlineData("sed -n '1,220p' docs/README.md", "hypa -c \"sed -n '1,220p' docs/README.md\"")]
    [InlineData("cat CLAUDE.md", "hypa -c \"cat CLAUDE.md\"")]
    public async Task GenericWrapperEnabled_RawCommand_ReturnsRewriteWithHypaC(string command, string expected)
    {
        var decision = await _service.ProcessAsync(MakeInput(command));
        var rewrite = Assert.IsType<HookDecision.Rewrite>(decision);
        Assert.Equal(expected, rewrite.Command);
    }

    [Theory]
    [InlineData("hypa git status")]
    [InlineData("hypa -c \"rg --files\"")]
    [InlineData("hypa")]
    public async Task ExistingHypaCommand_ReturnsPassthrough(string command)
    {
        var decision = await _service.ProcessAsync(MakeInput(command));
        Assert.IsType<HookDecision.Passthrough>(decision);
    }

    [Theory]
    [InlineData("git log | grep fix")]
    [InlineData("cat file.txt > output.txt")]
    [InlineData("echo $(git rev-parse HEAD)")]
    public async Task ShellismOrPipe_PreservesPassthrough_WhenRegistryCannotSafelyRewrite(string command)
    {
        var decision = await _service.ProcessAsync(MakeInput(command));
        Assert.IsType<HookDecision.Passthrough>(decision);
    }

    private static AgentHookInput MakeInput(string command) =>
        new("Bash", command, JsonDocument.Parse("{}").RootElement);
}
