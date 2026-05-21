using System.Text.Json;
using Hypa.Infrastructure.Hooks;
using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Infrastructure.Skills;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Domain.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class CodexAdapterTests
{
    private readonly CodexAdapter _adapter = new(new SkillRenderer());

    [Fact]
    public void Parse_BashToolWithCommand_ReturnsInput()
    {
        var json = ParseJson("""{"tool_name":"Bash","tool_input":{"command":"git status"}}""");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("Bash", result.ToolName);
        Assert.Equal("git status", result.Command);
    }

    [Fact]
    public void Parse_ShellPayloadVariants_ReturnsInputWithCommand()
    {
        var variants = new (string Payload, string Command)[]
        {
            ("""{"tool_name":"Bash","tool_input":{"command":"from tool_input.command"}}""", "from tool_input.command"),
            ("""{"tool_name":"Bash","tool_input":{"cmd":"from tool_input.cmd"}}""", "from tool_input.cmd"),
            ("""{"tool_name":"Bash","input":{"command":"from input.command"}}""", "from input.command"),
            ("""{"tool_name":"Bash","input":{"cmd":"from input.cmd"}}""", "from input.cmd"),
            ("""{"tool_name":"functions.exec_command","arguments":{"command":"from arguments.command"}}""", "from arguments.command"),
            ("""{"tool_name":"functions.exec_command","arguments":{"cmd":"git status"}}""", "git status"),
            ("""{"tool_name":"Bash","command":"from root command"}""", "from root command"),
            ("""{"tool_name":"Bash","cmd":"from root cmd"}""", "from root cmd"),
        };

        foreach (var variant in variants)
        {
            var result = _adapter.Parse(ParseJson(variant.Payload));
            Assert.NotNull(result);
            Assert.Equal("Bash", result.ToolName);
            Assert.Equal(variant.Command, result.Command);
        }
    }

    [Fact]
    public void Parse_ShellPayloadVariants_UsesFirstStringCommandByPriority()
    {
        var json = ParseJson(
            """
            {
              "tool_name": "Bash",
              "tool_input": { "cmd": "from tool_input.cmd" },
              "input": { "command": "from input.command" },
              "arguments": { "command": "from arguments.command", "cmd": "from arguments.cmd" },
              "command": "from root command",
              "cmd": "from root cmd"
            }
            """);

        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("from tool_input.cmd", result.Command);
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("bash")]
    [InlineData("Shell")]
    [InlineData("shell")]
    [InlineData("command")]
    [InlineData("exec_command")]
    [InlineData("functions.exec_command")]
    public void Parse_ShellToolName_NormalizesToolNameToBash(string toolName)
    {
        var json = ParseJson($$$"""{"tool_name":"{{{toolName}}}","tool_input":{"command":"git status"}}""");
        var result = _adapter.Parse(json);
        Assert.NotNull(result);
        Assert.Equal("Bash", result.ToolName);
        Assert.Equal("git status", result.Command);
    }

    [Fact]
    public void Parse_NonShellTool_ReturnsNull()
    {
        var json = ParseJson("""{"tool_name":"Read","tool_input":{"path":"/tmp"}}""");
        Assert.Null(_adapter.Parse(json));
    }

    [Theory]
    [InlineData("""{"tool_name":"Bash","tool_input":{"path":"/tmp"}}""")]
    [InlineData("""{"tool_name":"functions.exec_command","arguments":{"cmd":42}}""")]
    [InlineData("""{"tool_name":"Shell","tool_input":{"command":false},"command":123}""")]
    [InlineData("""{"tool_name":42,"tool_input":{"command":"git status"}}""")]
    public void Parse_MissingCommand_ReturnsNull(string payload)
    {
        Assert.Null(_adapter.Parse(ParseJson(payload)));
    }

    [Fact]
    public void Format_Rewrite_EmitsCodexDenyWithSuggestion()
    {
        var input = MakeInput("git status");
        var decision = new HookDecision.Rewrite("hypa git status");
        var output = _adapter.Format(decision, input);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("hookSpecificOutput", output.JsonBody);
        Assert.Contains("PreToolUse", output.JsonBody);
        Assert.Contains("deny", output.JsonBody);
        Assert.Contains("Use: hypa git status", output.JsonBody);
    }

    [Fact]
    public void Format_Deny_EmitsCodexDeny()
    {
        var input = MakeInput("rm -rf /");
        var decision = new HookDecision.Deny("Command blocked");
        var output = _adapter.Format(decision, input);
        Assert.NotNull(output.JsonBody);
        Assert.Contains("hookSpecificOutput", output.JsonBody);
        Assert.Contains("deny", output.JsonBody);
    }

    [Fact]
    public void Format_Passthrough_EmitsNullBody()
    {
        var input = MakeInput("ssh user@host");
        var output = _adapter.Format(new HookDecision.Passthrough(), input);
        Assert.Null(output.JsonBody);
    }

    [Fact]
    public void Format_Rewrite_JsonBodyMatchesExactSchema()
    {
        var input = MakeInput("git status");
        var output = _adapter.Format(new HookDecision.Rewrite("hypa git status"), input);
        Assert.NotNull(output.JsonBody);
        using var doc = JsonDocument.Parse(output.JsonBody!);
        var hook = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", hook.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", hook.GetProperty("permissionDecision").GetString());
        Assert.Equal("Use: hypa git status", hook.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void Format_GenericWrappedCommand_DenyReasonIsExact()
    {
        var input = MakeInput("rg --files");
        var output = _adapter.Format(new HookDecision.Rewrite("hypa -c \"rg --files\""), input);
        Assert.NotNull(output.JsonBody);
        using var doc = JsonDocument.Parse(output.JsonBody!);
        var reason = doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("permissionDecisionReason")
            .GetString();
        Assert.Equal("Use: hypa -c \"rg --files\"", reason);
    }

    [Fact]
    public void Key_IsExpected()
    {
        Assert.Equal("codex", _adapter.Key);
    }

    [Fact]
    public void Capability_HasBothFlags()
    {
        Assert.True(_adapter.Capability.HasFlag(HarnessCapability.PreToolUse));
        Assert.True(_adapter.Capability.HasFlag(HarnessCapability.RulesFileSupport));
    }

    [Fact]
    public void CodexAdapter_Capability_IncludesMcpServer()
    {
        Assert.True(_adapter.Capability.HasFlag(HarnessCapability.McpServer));
    }

    [Fact]
    public void GetInstallPlan_Global_ContainsMcpServerPatchOperation()
    {
        var plan = _adapter.GetInstallPlan(global: true);
        var op = Assert.Single(plan.Operations.OfType<InstallOperation.PatchTomlSection>());
        Assert.Equal("mcp_servers.hypa", op.SectionPath);
        Assert.Contains("config.toml", op.FilePath);
        Assert.Contains("serve", op.Content);
    }

    [Fact]
    public void GetInstallPlan_Local_ContainsMcpServerPatchOperation()
    {
        var plan = _adapter.GetInstallPlan(global: false, projectRoot: "/repo");
        var op = Assert.Single(plan.Operations.OfType<InstallOperation.PatchTomlSection>());
        Assert.Equal("mcp_servers.hypa", op.SectionPath);
        Assert.Contains("config.toml", op.FilePath);
        Assert.Contains("serve", op.Content);
    }

    [Fact]
    public void GetInstallPlan_Local_ContainsHookTomlRulesAndInjectOps()
    {
        var plan = _adapter.GetInstallPlan(global: false, projectRoot: "/repo");
        Assert.Contains(plan.Operations, op => op is InstallOperation.PatchJsonHook hook && hook.FilePath.Contains(".codex"));
        Assert.Contains(plan.Operations, op => op is InstallOperation.EnsureCodexHooksFeature);
        Assert.Contains(plan.Operations, op => op is InstallOperation.WriteFile wf && wf.FilePath.Contains("HYPA.md"));
        Assert.Contains(plan.Operations, op => op is InstallOperation.InjectLine inject && inject.Line == "@HYPA.md");
    }

    [Fact]
    public void GetInstallPlan_Local_PathsAreUnderProjectRoot()
    {
        var plan = _adapter.GetInstallPlan(global: false, projectRoot: "/my/repo");
        var hookOp = Assert.IsType<InstallOperation.PatchJsonHook>(plan.Operations[0]);
        Assert.StartsWith("/my/repo", hookOp.FilePath);
    }

    [Fact]
    public void GetInstallPlan_Global_TargetsCodexHome()
    {
        var plan = _adapter.GetInstallPlan(global: true);
        var codexHome = CodexConfigPaths.ResolveHome();
        Assert.DoesNotContain(plan.Operations, op => op is InstallOperation.NotSupported);
        Assert.Contains(plan.Operations, op => op is InstallOperation.PatchJsonHook hook &&
            hook.FilePath == Path.Combine(codexHome, "hooks.json"));
        Assert.Contains(plan.Operations, op => op is InstallOperation.InjectLine inject && inject.Line.StartsWith("@", StringComparison.Ordinal) && !inject.Line.Equals("@HYPA.md", StringComparison.Ordinal));
    }

    [Fact]
    public void GetInstallPlan_Global_AddsHypaStorageToCodexWritableRoots()
    {
        var dataOptions = new HypaDataOptions { DataDirectory = "/home/me/.hypa" };
        var adapter = new CodexAdapter(new SkillRenderer(), dataOptions);

        var plan = adapter.GetInstallPlan(global: true);

        var op = Assert.Single(plan.Operations.OfType<InstallOperation.EnsureCodexWritableRoot>());
        Assert.Equal("/home/me/.hypa", op.WritableRoot);
        Assert.Equal("config.toml", Path.GetFileName(op.FilePath));
    }

    [Fact]
    public void GetInstallPlan_ContainsBroadCodexShellMatcher()
    {
        var plan = _adapter.GetInstallPlan(global: false, projectRoot: "/repo");
        var hookOp = plan.Operations.OfType<InstallOperation.PatchJsonHook>().Single();
        var hookDoc = JsonDocument.Parse(hookOp.HookJson);
        var matcher = hookDoc.RootElement.GetProperty("matcher").GetString();
        Assert.Equal(
            @"^(Bash|bash|Shell|shell|command|exec_command|functions\.exec_command)$",
            matcher);
    }

    [Fact]
    public void GetUninstallPlan_RemovesBroadMatcherAndLegacyBashMatcher()
    {
        var plan = _adapter.GetUninstallPlan(global: false, projectRoot: "/repo");
        var removeOps = plan.Operations.OfType<UninstallOperation.RemoveJsonHook>().ToList();

        Assert.True(removeOps.Any(op =>
        {
            var doc = JsonDocument.Parse(op.HookJson);
            var matcher = doc.RootElement.GetProperty("matcher").GetString();
            return matcher is not null && matcher.Contains("exec_command");
        }), "Expected at least one removal targeting the broad matcher");

        Assert.True(removeOps.Any(op =>
        {
            var doc = JsonDocument.Parse(op.HookJson);
            var matcher = doc.RootElement.GetProperty("matcher").GetString();
            return matcher == "Bash";
        }), "Expected at least one removal targeting the legacy 'Bash' matcher");
    }

    [Fact]
    public void GetInstallPlan_Local_WithoutProjectRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => _adapter.GetInstallPlan(global: false));
    }

    [Fact]
    public void IsDetected_Global_ReturnsBool()
    {
        var result = _adapter.IsDetected(global: true);
        Assert.IsType<bool>(result);
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

    [Fact]
    public void GetInstallPlan_WritesUpdatedHypaRules()
    {
        var plan = _adapter.GetInstallPlan(global: false, projectRoot: "/repo");
        var writeOp = plan.Operations.OfType<InstallOperation.WriteFile>()
            .Single(op => op.FilePath.Contains("HYPA.md"));
        Assert.Contains("hypa_shell", writeOp.Content);
    }

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static AgentHookInput MakeInput(string command) =>
        new("Bash", command, ParseJson($$$"""{"tool_name":"Bash","tool_input":{"command":"{{{command}}}"}}"""));
}
