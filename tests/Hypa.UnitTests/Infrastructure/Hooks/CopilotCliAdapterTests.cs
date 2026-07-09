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

    [Theory]
    [InlineData("bash")]
    [InlineData("Bash")]
    [InlineData("BASH")]
    [InlineData("powershell")]
    [InlineData("PowerShell")]
    public void Parse_ShellToolNameCaseVariants_ReturnInput(string toolName)
    {
        var json = ParseJson($$"""{"toolName":"{{toolName}}","toolArgs":"{\"command\":\"git status\"}"}""");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("git status", result.Command);
        Assert.Equal("Bash", result.ToolName);
    }

    [Fact]
    public void Parse_PowerShell_ScenarioB_ReturnsInput()
    {
        // Issue #52 Scenario B: native camelCase preToolUse on Windows.
        var json = ParseJson("""
            {
              "sessionId": "1e194835-92e7-44dd-ab13-9adf7db6e568",
              "timestamp": 1783508050028,
              "cwd": "C:\\SoftwareDev\\real-time-locating-system",
              "toolName": "powershell",
              "toolArgs": "{\"command\":\"git -C \\\"C:\\\\SoftwareDev\\\\real-time-locating-system\\\" --no-pager status --short --branch\",\"description\":\"Show git status for repository\"}"
            }
            """);
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("Bash", result.ToolName);
        Assert.Contains("git -C", result.Command);
        Assert.Contains("status --short --branch", result.Command);
    }

    [Fact]
    public void Parse_ToolArgsAsObject_ReturnsInput()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":{"command":"git status"}}""");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("git status", result.Command);
        Assert.Equal("Bash", result.ToolName);
    }

    [Fact]
    public void Parse_NonShellTool_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"view","toolArgs":"{\"path\":\"/tmp\"}"}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("cmd")]
    [InlineData("Shell")]
    public void Parse_NonAcceptedShellAliases_ReturnNull(string toolName)
    {
        var json = ParseJson($$"""{"toolName":"{{toolName}}","toolArgs":"{\"command\":\"git status\"}"}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_InvalidToolArgsJson_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":"not-json"}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_MissingToolArgs_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"bash"}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_EmptyStringToolArgs_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":""}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_ObjectToolArgsWithoutCommand_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":{"description":"no command"}}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_ObjectToolArgsEmptyCommand_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":{"command":""}}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_NonObjectNonStringToolArgs_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":42}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_StringToolArgsEmptyCommand_ReturnsNull()
    {
        var json = ParseJson("""{"toolName":"bash","toolArgs":"{\"command\":\"\"}"}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_PascalCaseScenarioA_ReturnsInput()
    {
        // Issue #52 Scenario A: PascalCase PreToolUse without Claude markers.
        var json = ParseJson("""
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
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("Bash", result.ToolName);
        Assert.Equal("git status", result.Command);
    }

    [Fact]
    public void Parse_PascalCaseWithClaudeMarker_ReturnsNull()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "transcript_path": "/tmp/t.jsonl",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    [Theory]
    [InlineData("""{"hook_event_name":"PreToolUse","permission_mode":"","tool_name":"Bash","tool_input":{"command":"git status"}}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","permission_mode":null,"tool_name":"Bash","tool_input":{"command":"git status"}}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","transcript_path":"","tool_name":"Bash","tool_input":{"command":"git status"}}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_use_id":null,"tool_name":"Bash","tool_input":{"command":"git status"}}""")]
    public void Parse_PascalCase_EmptyOrNullMarkerFields_StillMatches(string jsonText)
    {
        // Empty/null marker fields do not count as Claude markers (non-empty string rule).
        var json = ParseJson(jsonText);
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("git status", result.Command);
    }

    [Fact]
    public void Parse_PascalCaseNonShell_ReturnsNull()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Read",
              "tool_input": {"path":"/tmp/foo"}
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_PascalCasePostToolUse_ReturnsNull()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PostToolUse",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_PascalCaseWrongEventNameCasing_ReturnsNull()
    {
        // Ordinal gate: camelCase preToolUse is not PreToolUse on the PascalCase path.
        var json = ParseJson("""
            {
              "hook_event_name": "preToolUse",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_PascalCaseMissingToolInput_ReturnsNull()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Bash"
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_PascalCaseNonObjectToolInput_ReturnsNull()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Bash",
              "tool_input": "git status"
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_PascalCaseEmptyCommand_ReturnsNull()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Bash",
              "tool_input": {"command":""}
            }
            """);
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
        Assert.DoesNotContain("updatedInput", output.JsonBody);
        Assert.DoesNotContain("hookSpecificOutput", output.JsonBody);
    }

    [Fact]
    public void Format_Deny_EmitsDenyWithReason()
    {
        var input = MakeInput("rm -rf /");
        var decision = new HookDecision.Deny("Command blocked");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("permissionDecision", output.JsonBody);
        Assert.Contains("deny", output.JsonBody);
        Assert.Contains("Command blocked", output.JsonBody);
        Assert.DoesNotContain("modifiedArgs", output.JsonBody);
        Assert.DoesNotContain("updatedInput", output.JsonBody);
        Assert.DoesNotContain("hookSpecificOutput", output.JsonBody);
    }

    [Fact]
    public void Format_Ask_EmitsAskWithReason()
    {
        var input = MakeInput("git push");
        var decision = new HookDecision.Ask("Confirm?");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("permissionDecision", output.JsonBody);
        Assert.Contains("ask", output.JsonBody);
        Assert.Contains("Confirm?", output.JsonBody);
        Assert.DoesNotContain("deny", output.JsonBody);
        Assert.DoesNotContain("modifiedArgs", output.JsonBody);
        Assert.DoesNotContain("updatedInput", output.JsonBody);
        Assert.DoesNotContain("hookSpecificOutput", output.JsonBody);
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
        JsonDocument.Parse(json).RootElement.Clone();

    private static AgentHookInput MakeInput(string command) =>
        new("bash", command, ParseJson("{}"));
}
