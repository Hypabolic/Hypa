using System.Text.Json;
using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Runtime.Domain.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class CopilotVscodeAdapterTests
{
    private readonly CopilotVscodeAdapter _adapter = new();

    [Fact]
    public void Parse_RunTerminalCommand_ReturnsInput()
    {
        var json = ParseJson("""{"tool_name":"runTerminalCommand","tool_input":{"command":"git status"}}""");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("git status", result.Command);
    }

    [Fact]
    public void Parse_NonTerminalTool_ReturnsNull()
    {
        var json = ParseJson("""{"tool_name":"Bash","tool_input":{"command":"git status"}}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Format_Rewrite_EmitsUpdatedInput()
    {
        var input = MakeInput("git status");
        var decision = new HookDecision.Rewrite("hypa git status");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains("updatedInput", output.JsonBody!);
        Assert.Contains("hypa git status", output.JsonBody!);
    }

    [Fact]
    public void Format_Passthrough_EmitsNullBody()
    {
        var input = MakeInput("ssh user@host");
        var output = _adapter.Format(new HookDecision.Passthrough(), input);
        Assert.Null(output.JsonBody);
    }

    [Fact]
    public void Key_IsExpected()
    {
        Assert.Equal("copilot-vscode", _adapter.Key);
    }

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static AgentHookInput MakeInput(string command) =>
        new("runTerminalCommand", command, ParseJson($$$"""{"tool_name":"runTerminalCommand","tool_input":{"command":"{{{command}}}"}}"""));
}
