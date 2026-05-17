using System.Text.Json;
using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Infrastructure.Skills;
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
    public void Parse_NonBashTool_ReturnsNull()
    {
        var json = ParseJson("""{"tool_name":"Read","tool_input":{"path":"/tmp"}}""");
        Assert.Null(_adapter.Parse(json));
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
        Assert.DoesNotContain(plan.Operations, op => op is InstallOperation.NotSupported);
        Assert.Contains(plan.Operations, op => op is InstallOperation.PatchJsonHook hook && hook.FilePath.EndsWith(".codex/hooks.json"));
        Assert.Contains(plan.Operations, op => op is InstallOperation.InjectLine inject && inject.Line.Contains("@/"));
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

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static AgentHookInput MakeInput(string command) =>
        new("Bash", command, ParseJson($$$"""{"tool_name":"Bash","tool_input":{"command":"{{{command}}}"}}"""));
}
