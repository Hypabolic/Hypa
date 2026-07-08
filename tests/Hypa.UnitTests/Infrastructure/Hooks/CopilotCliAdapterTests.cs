using System.Text.Json;
using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Runtime.Domain.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class CopilotCliAdapterTests
{
    private readonly CopilotCliAdapter _adapter = new();

    [Fact]
    public void Parse_BashTool_ReturnsInput()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":"{\"command\":\"git status\"}"}""");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("git status", result.Command);
        Assert.Equal("Bash", result.ToolName);
    }

    [Fact]
    public void Parse_NonBashTool_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"read","toolArgs":"{\"path\":\"/tmp\"}"}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_InvalidToolArgsJson_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":"not-json"}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Format_Rewrite_EmitsModifiedArgs()
    {
        var input = MakeInput("git status");
        var decision = new HookDecision.Rewrite("hypa -c \"git status\"");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("modifiedArgs", output.JsonBody);
        Assert.Contains("command", output.JsonBody);
        Assert.Contains(@"hypa -c \u0022git status\u0022", output.JsonBody);
        Assert.DoesNotContain("permissionDecision", output.JsonBody);
        Assert.DoesNotContain("deny", output.JsonBody);
    }

    [Fact]
    public void Format_Deny_EmitsDenyWithReason()
    {
        var input = MakeInput("rm -rf /");
        var decision = new HookDecision.Deny("Command blocked");
        var output = _adapter.Format(decision, input);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("deny", output.JsonBody);
        Assert.Contains("Command blocked", output.JsonBody);
    }

    [Fact]
    public void Format_Ask_EmitsAskWithReason()
    {
        var input = MakeInput("git push");
        var decision = new HookDecision.Ask("Confirm?");
        var output = _adapter.Format(decision, input);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("ask", output.JsonBody);
        Assert.Contains("Confirm?", output.JsonBody);
        Assert.DoesNotContain("deny", output.JsonBody);
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
        Assert.Equal("copilot-cli", _adapter.Key);
    }

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static AgentHookInput MakeInput(string command) =>
        new("bash", command, ParseJson("{}"));
}
