using Hypa.Infrastructure.Skills;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Skills;

public sealed class SkillRendererTests
{
    private readonly SkillRenderer _renderer = new();

    [Fact]
    public void GetRulesContent_MentionsHypaShell()
    {
        var content = _renderer.GetRulesContent();
        Assert.Contains("hypa_shell", content);
    }
}
