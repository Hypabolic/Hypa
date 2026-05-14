using Hypa.Infrastructure.Hooks;
using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Infrastructure.Skills;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class HarnessRegistryTests
{
    private static readonly SkillRenderer Renderer = new();

    private readonly HarnessRegistry _registry = new([
        new ClaudeCodeAdapter(Renderer),
        new CopilotVscodeAdapter(),
        new CopilotCliAdapter(),
        new CodexAdapter(Renderer),
    ]);

    [Fact]
    public void All_ContainsAllRegisteredAdapters()
    {
        Assert.Equal(4, _registry.All.Count);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot-vscode")]
    [InlineData("copilot-cli")]
    [InlineData("codex")]
    public void Find_KnownKey_ReturnsAdapter(string key)
    {
        var adapter = _registry.Find(key);
        Assert.NotNull(adapter);
        Assert.Equal(key, adapter.Key);
    }

    [Fact]
    public void Find_UnknownKey_ReturnsNull()
    {
        Assert.Null(_registry.Find("nonexistent"));
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("CLAUDE")]
    public void Find_IsCaseInsensitive(string key)
    {
        Assert.NotNull(_registry.Find(key));
    }
}
