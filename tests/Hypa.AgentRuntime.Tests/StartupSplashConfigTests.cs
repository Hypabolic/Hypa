using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class StartupSplashConfigTests
{
    [Fact]
    public void Startup_splash_parses_and_defaults_true()
    {
        Assert.True(AttachClientConfig.Default.Ui.StartupSplash);
        Assert.True(Bind("").Ui.StartupSplash);
        Assert.True(Bind("ui.startup_splash = true").Ui.StartupSplash);
        Assert.False(Bind("ui.startup_splash = false").Ui.StartupSplash);
        Assert.True(Bind(AttachConfigDefaults.Toml).Ui.StartupSplash);
    }

    [Fact]
    public void Upsert_startup_splash_writes_ui_key()
    {
        var result = TomlKeyRewriter.Upsert(
            "",
            [new AttachConfigAssignment("ui.startup_splash", "false")]);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        Assert.Contains("[ui]", result.Value, StringComparison.Ordinal);
        Assert.Contains("startup_splash = false", result.Value, StringComparison.Ordinal);
        var bound = TomlAttachConfigBinder.Bind(result.Value);
        Assert.True(bound.IsOk, bound.IsOk ? "" : bound.Error.Message);
        Assert.False(bound.Value.Ui.StartupSplash);
    }

    private static AttachClientConfig Bind(string text)
    {
        var result = TomlAttachConfigBinder.Bind(text);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.ToString());
        return result.Value;
    }
}
