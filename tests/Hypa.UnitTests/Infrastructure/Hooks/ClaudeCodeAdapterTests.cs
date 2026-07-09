using System.Text.Json;
using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Infrastructure.Skills;
using Hypa.Runtime.Domain.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class ClaudeCodeAdapterTests
{
    private readonly ClaudeCodeAdapter _adapter = new(new SkillRenderer());

    // --- Parse ---

    [Fact]
    public void Parse_BashToolWithCommand_ReturnsInput()
    {
        var json = ClaudePreToolUseJson("Bash", """{"command":"git status"}""");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("Bash", result.ToolName);
        Assert.Equal("git status", result.Command);
    }

    [Fact]
    public void Parse_MissingHookEventName_ReturnsNull()
    {
        var json = ParseJson("""{"tool_name":"Bash","tool_input":{"command":"git status"}}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_ReadTool_ReturnsInputWithPath()
    {
        var json = ClaudePreToolUseJson("Read", """{"path":"/tmp/foo"}""");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("Read", result.ToolName);
        Assert.Equal("/tmp/foo", result.Path);
    }

    [Fact]
    public void Parse_OtherTool_ReturnsPassthroughInput()
    {
        var json = ClaudePreToolUseJson("Edit", "{}");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("Edit", result.ToolName);
    }

    [Fact]
    public void Parse_MissingToolName_ReturnsNull()
    {
        // Markers present so we pass the marker gate and exercise post-gate tool_name missing.
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "transcript_path": "/tmp/hypa-test-transcript.jsonl",
              "permission_mode": "default",
              "tool_input": {"command":"git status"}
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_MissingCommand_ReturnsNull()
    {
        // Markers present so we pass the marker gate and exercise Bash without command.
        var json = ClaudePreToolUseJson("Bash", "{}");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_EmptyCommand_ReturnsNull()
    {
        var json = ClaudePreToolUseJson("Bash", """{"command":""}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_CopilotPascalCase_NoMarkers_ReturnsNull()
    {
        // Issue #52 Scenario A: Copilot PascalCase PreToolUse without Claude markers.
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
        Assert.Null(_adapter.Parse(json));
    }

    [Theory]
    [InlineData("transcript_path", "/tmp/t.jsonl")]
    [InlineData("permission_mode", "default")]
    [InlineData("tool_use_id", "toolu_abc123")]
    public void Parse_SingleNonEmptyMarker_StillMatches(string markerName, string markerValue)
    {
        var json = ParseJson($$"""
            {
              "hook_event_name": "PreToolUse",
              "{{markerName}}": "{{markerValue}}",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("git status", result.Command);
    }

    [Fact]
    public void Parse_EmptyPermissionModeOnly_ReturnsNull()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "permission_mode": "",
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    [Fact]
    public void Parse_NullPermissionModeOnly_ReturnsNull()
    {
        var json = ParseJson("""
            {
              "hook_event_name": "PreToolUse",
              "permission_mode": null,
              "tool_name": "Bash",
              "tool_input": {"command":"git status"}
            }
            """);
        Assert.Null(_adapter.Parse(json));
    }

    // --- Format ---

    [Fact]
    public void Format_Rewrite_EmitsUpdatedInput()
    {
        var input = MakeInput("git status");
        var decision = new HookDecision.Rewrite("hypa git status");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("updatedInput", output.JsonBody);
        Assert.Contains("hypa git status", output.JsonBody);
        Assert.DoesNotContain("modifiedArgs", output.JsonBody);
        Assert.DoesNotContain("hookSpecificOutput", output.JsonBody);
    }

    [Fact]
    public void Format_Deny_EmitsBlockDecision()
    {
        var input = MakeInput("rm -rf /");
        var decision = new HookDecision.Deny("Command blocked by Hypa policy: rm -rf /");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("block", output.JsonBody);
        Assert.Contains("Command blocked", output.JsonBody);
    }

    [Fact]
    public void Format_Ask_EmitsBlockDecision()
    {
        var input = MakeInput("git push origin main");
        var decision = new HookDecision.Ask("Confirm running: git push origin main");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("block", output.JsonBody);
    }

    [Fact]
    public void Format_Passthrough_EmitsNullBody()
    {
        var input = MakeInput("ssh user@host");
        var decision = new HookDecision.Passthrough();
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.Null(output.JsonBody);
    }

    [Fact]
    public void Format_Redirect_EmitsUpdatedPathInput()
    {
        var input = MakeInput("ignored");
        var decision = new HookDecision.Redirect("/tmp/hypa-hook/abc.hypa");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("updatedInput", output.JsonBody);
        Assert.Contains("/tmp/hypa-hook/abc.hypa", output.JsonBody);
    }

    // --- Metadata ---

    [Fact]
    public void Key_IsExpected()
    {
        Assert.Equal("claude", _adapter.Key);
    }

    [Fact]
    public void Capability_IsPreToolUse()
    {
        Assert.True(_adapter.Capability.HasFlag(HarnessCapability.PreToolUse));
    }

    [Fact]
    public void GetInstallPlan_Global_ContainsRequiredOps()
    {
        var plan = _adapter.GetInstallPlan(global: true, includeMcp: true);
        Assert.Contains(plan.Operations, op => op is InstallOperation.PatchJsonHook);
        Assert.Contains(plan.Operations, op => op is InstallOperation.WriteFile);
        Assert.Contains(plan.Operations, op => op is InstallOperation.PatchJsonObject);
        Assert.Contains(plan.Operations, op => op is InstallOperation.InjectFencedBlock);
    }

    [Fact]
    public void GetInstallPlan_Global_WithoutMcp_ExcludesMcpServerPatch()
    {
        var plan = _adapter.GetInstallPlan(global: true, includeMcp: false);
        Assert.DoesNotContain(plan.Operations, op => op is InstallOperation.PatchJsonObject);
        Assert.Contains(plan.Operations, op => op is InstallOperation.PatchJsonHook);
        Assert.Contains(plan.Operations, op => op is InstallOperation.WriteFile);
        Assert.Contains(plan.Operations, op => op is InstallOperation.InjectFencedBlock);
    }

    [Fact]
    public void GetInstallPlan_Local_ContainsOnlySettingsOp()
    {
        var plan = _adapter.GetInstallPlan(global: false, includeMcp: false, projectRoot: "/repo");
        Assert.Single(plan.Operations);
        Assert.IsType<InstallOperation.PatchJsonHook>(plan.Operations[0]);
    }

    [Fact]
    public void GetInstallPlan_Local_PathIsSettingsLocalJson()
    {
        var plan = _adapter.GetInstallPlan(global: false, includeMcp: false, projectRoot: "/my/repo");
        var hook = Assert.IsType<InstallOperation.PatchJsonHook>(plan.Operations[0]);
        Assert.StartsWith("/my/repo", hook.FilePath);
        Assert.EndsWith("settings.local.json", hook.FilePath);
    }

    [Fact]
    public void GetInstallPlan_Local_WithoutProjectRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => _adapter.GetInstallPlan(global: false, includeMcp: false));
    }

    [Fact]
    public void GetInstallPlan_Global_SkillContentIsNonEmpty()
    {
        var plan = _adapter.GetInstallPlan(global: true, includeMcp: false);
        var writeFile = plan.Operations.OfType<InstallOperation.WriteFile>().First();
        Assert.NotEmpty(writeFile.Content);
        Assert.Contains("hypa", writeFile.Content, StringComparison.OrdinalIgnoreCase);
    }

    // --- ClaudeMdBlock + skill content ---

    [Fact]
    public void GetInstallPlan_Global_ClaudeMdBlock_DoesNotReferenceHypaReadMcpTool()
    {
        var plan = _adapter.GetInstallPlan(global: true, includeMcp: false);
        var block = plan.Operations.OfType<InstallOperation.InjectFencedBlock>().First();
        Assert.DoesNotContain("hypa_read", block.Content);
    }

    [Fact]
    public void GetInstallPlan_Global_ClaudeMdBlock_ReferencesCliEquivalents()
    {
        var plan = _adapter.GetInstallPlan(global: true, includeMcp: false);
        var block = plan.Operations.OfType<InstallOperation.InjectFencedBlock>().First();
        Assert.Contains("hypa read", block.Content);
        Assert.Contains("hypa compress", block.Content);
        Assert.Contains("hypa search", block.Content);
    }

    [Fact]
    public void GetInstallPlan_Global_WithoutMcp_SkillHasNoMcpSections()
    {
        var plan = _adapter.GetInstallPlan(global: true, includeMcp: false);
        var writeFile = plan.Operations.OfType<InstallOperation.WriteFile>().First();
        Assert.DoesNotContain("MCP Tools Reference", writeFile.Content);
        Assert.DoesNotContain("MCP Server Management", writeFile.Content);
    }

    [Fact]
    public void GetInstallPlan_Global_WithMcp_SkillHasMcpSections()
    {
        var plan = _adapter.GetInstallPlan(global: true, includeMcp: true);
        var writeFile = plan.Operations.OfType<InstallOperation.WriteFile>().First();
        Assert.Contains("MCP Tools Reference", writeFile.Content);
        Assert.Contains("MCP Server Management", writeFile.Content);
    }

    [Fact]
    public void IsDetected_Local_WithoutProjectRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => _adapter.IsDetected(global: false));
    }

    [Fact]
    public void GetUninstallPlan_Local_WithoutProjectRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => _adapter.GetUninstallPlan(global: false));
    }

    /// <summary>
    /// Always inject Claude envelope markers used by production PreToolUse.
    /// Happy-path default: transcript_path + permission_mode (not tool_use_id).
    /// </summary>
    private static JsonElement ClaudePreToolUseJson(string toolName, string toolInputObjectJson)
    {
        var json = $$"""
            {
              "hook_event_name": "PreToolUse",
              "transcript_path": "/tmp/hypa-test-transcript.jsonl",
              "permission_mode": "default",
              "tool_name": "{{toolName}}",
              "tool_input": {{toolInputObjectJson}}
            }
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static AgentHookInput MakeInput(string command) =>
        new("Bash", command, ParseJson($$$"""{"tool_name":"Bash","tool_input":{"command":"{{{command}}}"}}"""));
}
