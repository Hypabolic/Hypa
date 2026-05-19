using System.CommandLine;
using System.Text.Json;
using Hypa.Cli.Commands;
using Hypa.Infrastructure.Hooks;
using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Infrastructure.Rewrite;
using Hypa.Infrastructure.Skills;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Hypa.Runtime.Domain.Hooks;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class HookCommandTests
{
    private readonly IHookIo _io;
    private readonly HookCommand _command;

    public HookCommandTests()
    {
        _io = Substitute.For<IHookIo>();

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
        var rewriteRegistry = new CommandRewriteRegistry(lexer, strategies, wrapper);
        var configLoader = Substitute.For<IConfigLoader>();
        configLoader.LoadAsync(default).ReturnsForAnyArgs(
            Task.FromResult(Result<HypaConfig, Error>.Ok(HypaConfig.Default)));
        var rewriteService = new CommandRewriteService(configLoader, rewriteRegistry);
        var readRedirector = Substitute.For<IReadRedirector>();
        readRedirector.RedirectAsync(default!, default).ReturnsForAnyArgs(Task.FromResult<string?>(null));
        var hookService = new HookService(rewriteService, readRedirector);

        var skillRenderer = Substitute.For<ISkillRenderer>();
        var codexAdapter = new CodexAdapter(skillRenderer);
        var harnessRegistry = Substitute.For<IHarnessRegistry>();
        harnessRegistry.Find("codex").Returns(codexAdapter);
        harnessRegistry.All.Returns([codexAdapter]);

        _command = new HookCommand(_io, harnessRegistry, hookService);
    }

    [Fact]
    public async Task ValidBashCommand_ExitsZero_AndCallsWriteOutput()
    {
        var payload = JsonDocument.Parse("""{"tool_name":"Bash","tool_input":{"command":"git status"}}""")
            .RootElement.Clone();
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        var exitCode = await _command.Build().InvokeAsync(["--agent", "codex"]);

        Assert.Equal(0, exitCode);
        _io.Received(1).WriteOutput(Arg.Is<AgentHookOutput>(o =>
            o.JsonBody != null && o.JsonBody.Contains("deny", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task HypaCommand_ExitsZero_AndWritesPassthroughOutput()
    {
        var payload = JsonDocument.Parse("""{"tool_name":"Bash","tool_input":{"command":"hypa git status"}}""")
            .RootElement.Clone();
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        var exitCode = await _command.Build().InvokeAsync(["--agent", "codex"]);

        Assert.Equal(0, exitCode);
        _io.Received(1).WriteOutput(Arg.Is<AgentHookOutput>(o => o.JsonBody == null));
    }
}
