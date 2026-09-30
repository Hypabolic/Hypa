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

/// <summary>
/// End-to-end auto-detect tests for <c>hypa hook</c> with the full DI registration order.
/// Adapter identity is inferred from Format body shape (not private ResolveAdapter methods).
/// </summary>
public sealed class HookAutoDetectTests
{
    private static readonly SkillRenderer Renderer = new();

    private readonly IHookIo _io;
    private readonly HookCommand _command;
    private readonly HarnessRegistry _registry;

    public HookAutoDetectTests()
    {
        _io = Substitute.For<IHookIo>();
        _registry = CreateProductionOrderedRegistry();

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

        _command = new HookCommand(_io, _registry, hookService);
    }

    private static HarnessRegistry CreateProductionOrderedRegistry() => new([
        new ClaudeCodeAdapter(Renderer),
        new CopilotVscodeAdapter(),
        new CopilotCliAdapter(),
        new CodexAdapter(Renderer),
        new PiAdapter(),
    ]);

    [Fact]
    public void Registry_Order_MatchesDiRegistration()
    {
        Assert.Equal(
            new[] { "claude", "copilot-vscode", "copilot-cli", "codex", "pi" },
            _registry.All.Select(a => a.Key).ToArray());
    }

    [Fact]
    public async Task AutoDetect_ScenarioA_PascalCaseCopilot_EmitsModifiedArgs()
    {
        // Issue #52 Scenario A — must not produce updatedInput (Claude) or hookSpecificOutput (Codex).
        var payload = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "session_id": "61eb593e-2b98-4481-8293-bc3667aa96b7",
              "timestamp": "2026-07-08T10:51:48.535Z",
              "cwd": "C:\\SoftwareDev\\real-time-locating-system",
              "tool_name": "Bash",
              "tool_input": {
                "command": "git status",
                "description": "Show git status"
              }
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        AgentHookOutput? written = null;
        _io.When(x => x.WriteOutput(Arg.Any<AgentHookOutput>()))
            .Do(ci => written = ci.Arg<AgentHookOutput>());

        var exitCode = await _command.Build().InvokeAsync([]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(written);
        Assert.NotNull(written.JsonBody);
        Assert.Contains("modifiedArgs", written.JsonBody);
        Assert.Contains("hypa", written.JsonBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updatedInput", written.JsonBody);
        Assert.DoesNotContain("hookSpecificOutput", written.JsonBody);
    }

    [Fact]
    public async Task AutoDetect_ScenarioA_DoesNotMatchCodex()
    {
        var payload = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        AgentHookOutput? written = null;
        _io.When(x => x.WriteOutput(Arg.Any<AgentHookOutput>()))
            .Do(ci => written = ci.Arg<AgentHookOutput>());

        var exitCode = await _command.Build().InvokeAsync([]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(written?.JsonBody);
        // Codex rewrite-as-deny uses hookSpecificOutput / "Use: " phrasing.
        Assert.DoesNotContain("hookSpecificOutput", written.JsonBody);
        Assert.DoesNotContain("\"decision\"", written.JsonBody);
        Assert.Contains("modifiedArgs", written.JsonBody);
        Assert.Contains("hypa", written.JsonBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoDetect_ScenarioB_PowerShell_EmitsModifiedArgs()
    {
        var payload = ParseJson("""
            {
              "sessionId": "1e194835-92e7-44dd-ab13-9adf7db6e568",
              "timestamp": 1783508050028,
              "cwd": "C:\\SoftwareDev\\real-time-locating-system",
              "toolName": "powershell",
              "toolArgs": "{\"command\":\"git status\",\"description\":\"Show git status\"}"
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        AgentHookOutput? written = null;
        _io.When(x => x.WriteOutput(Arg.Any<AgentHookOutput>()))
            .Do(ci => written = ci.Arg<AgentHookOutput>());

        var exitCode = await _command.Build().InvokeAsync([]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(written);
        Assert.NotNull(written.JsonBody);
        Assert.Contains("modifiedArgs", written.JsonBody);
        Assert.Contains("hypa", written.JsonBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updatedInput", written.JsonBody);
        Assert.DoesNotContain("hookSpecificOutput", written.JsonBody);
    }

    [Fact]
    public async Task AutoDetect_ClaudeWithMarkers_EmitsUpdatedInput()
    {
        var payload = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "transcript_path": "/tmp/hypa-test-transcript.jsonl",
              "permission_mode": "default",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        AgentHookOutput? written = null;
        _io.When(x => x.WriteOutput(Arg.Any<AgentHookOutput>()))
            .Do(ci => written = ci.Arg<AgentHookOutput>());

        var exitCode = await _command.Build().InvokeAsync([]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(written);
        Assert.NotNull(written.JsonBody);
        Assert.Contains("updatedInput", written.JsonBody);
        Assert.Contains("hypa", written.JsonBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("modifiedArgs", written.JsonBody);
        Assert.DoesNotContain("hookSpecificOutput", written.JsonBody);
    }

    [Fact]
    public async Task AutoDetect_CopilotVscode_RunInTerminal_EmitsUpdatedInput()
    {
        var payload = ParseJson("""
            {
              "tool_name": "run_in_terminal",
              "tool_input": {"command":"git status"}
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        AgentHookOutput? written = null;
        _io.When(x => x.WriteOutput(Arg.Any<AgentHookOutput>()))
            .Do(ci => written = ci.Arg<AgentHookOutput>());

        var exitCode = await _command.Build().InvokeAsync([]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(written);
        Assert.NotNull(written.JsonBody);
        // VS Code Copilot Format uses updatedInput (same key as Claude Format).
        Assert.Contains("updatedInput", written.JsonBody);
        Assert.Contains("hypa", written.JsonBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("modifiedArgs", written.JsonBody);
    }

    [Fact]
    public async Task AutoDetect_CodexShell_EmitsHookSpecificOutput()
    {
        // Codex-shaped payload without hook_event_name or toolName — only tool_name shell.
        var payload = ParseJson("""
            {
              "tool_name": "Shell",
              "tool_input": {"command":"git status"}
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        AgentHookOutput? written = null;
        _io.When(x => x.WriteOutput(Arg.Any<AgentHookOutput>()))
            .Do(ci => written = ci.Arg<AgentHookOutput>());

        var exitCode = await _command.Build().InvokeAsync([]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(written);
        Assert.NotNull(written.JsonBody);
        Assert.Contains("hookSpecificOutput", written.JsonBody);
        Assert.Contains("hypa", written.JsonBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("modifiedArgs", written.JsonBody);
        Assert.DoesNotContain("updatedInput", written.JsonBody);
    }

    [Fact]
    public async Task ExplicitAgent_CopilotCli_ScenarioA_EmitsModifiedArgs()
    {
        var payload = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        AgentHookOutput? written = null;
        _io.When(x => x.WriteOutput(Arg.Any<AgentHookOutput>()))
            .Do(ci => written = ci.Arg<AgentHookOutput>());

        var exitCode = await _command.Build().InvokeAsync(["--agent", "copilot-cli"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(written?.JsonBody);
        Assert.Contains("modifiedArgs", written.JsonBody);
        Assert.Contains("hypa", written.JsonBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitAgent_CopilotCli_ScenarioB_EmitsModifiedArgs()
    {
        var payload = ParseJson("""
            {
              "sessionId": "1e194835-92e7-44dd-ab13-9adf7db6e568",
              "timestamp": 1783508050028,
              "cwd": "C:\\SoftwareDev\\real-time-locating-system",
              "toolName": "powershell",
              "toolArgs": "{\"command\":\"git status\",\"description\":\"Show git status\"}"
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        AgentHookOutput? written = null;
        _io.When(x => x.WriteOutput(Arg.Any<AgentHookOutput>()))
            .Do(ci => written = ci.Arg<AgentHookOutput>());

        var exitCode = await _command.Build().InvokeAsync(["--agent", "copilot-cli"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(written?.JsonBody);
        Assert.Contains("modifiedArgs", written.JsonBody);
        Assert.Contains("hypa", written.JsonBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitAgent_Claude_ScenarioA_NoRewrite()
    {
        // Scenario A is not a Claude payload; --agent claude Parse returns null → exit 0, no write.
        var payload = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        _io.ReadStdinAsync(default).ReturnsForAnyArgs(Task.FromResult<JsonElement?>(payload));

        var exitCode = await _command.Build().InvokeAsync(["--agent", "claude"]);

        Assert.Equal(0, exitCode);
        _io.DidNotReceive().WriteOutput(Arg.Any<AgentHookOutput>());
    }

    [Fact]
    public void ParseOrder_ScenarioA_MatchesCopilotCli()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        var (key, input) = FirstMatch(json);
        Assert.Equal("copilot-cli", key);
        Assert.NotNull(input);
        Assert.Equal("git status", input.Command);
    }

    [Fact]
    public void ParseOrder_ScenarioB_MatchesCopilotCli()
    {
        var json = ParseJson("""
            {
              "toolName": "powershell",
              "toolArgs": "{\"command\":\"git status\"}"
            }
            """);
        var (key, input) = FirstMatch(json);
        Assert.Equal("copilot-cli", key);
        Assert.NotNull(input);
        Assert.Equal("git status", input.Command);
    }

    [Fact]
    public void ParseOrder_ClaudeMarkers_MatchesClaude()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "transcript_path": "/tmp/t.jsonl",
              "permission_mode": "default",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        var (key, input) = FirstMatch(json);
        Assert.Equal("claude", key);
        Assert.NotNull(input);
    }

    private (string? Key, AgentHookInput? Input) FirstMatch(JsonElement json)
    {
        foreach (var candidate in _registry.All)
        {
            var parsed = candidate.Parse(json);
            if (parsed is not null)
                return (candidate.Key, parsed);
        }

        return (null, null);
    }

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
