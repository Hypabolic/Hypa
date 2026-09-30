using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Settings;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class StartupSplashSettingsTests
{
    [Fact]
    public void Product_registry_appends_startup_after_core_six()
    {
        var product = SettingsPageRegistry.Product();
        Assert.Equal(8, product.Pages.Count);
        Assert.Equal(SettingsPageRegistry.ReleaseNotesId, product.Pages[6].Id);
        Assert.Equal(SettingsPageRegistry.StartupId, product.Pages[7].Id);
        Assert.Equal(SettingsPageKind.Startup, product.Pages[7].Kind);
    }

    [Fact]
    public void Overlay_model_default_is_core_six()
    {
        var model = new SettingsOverlayModel();
        Assert.Equal(6, model.Pages.Count);
        Assert.DoesNotContain(model.Pages, p => p.Id == SettingsPageRegistry.StartupId);
    }

    [Fact]
    public void Startup_apply_writes_splash_false()
    {
        var model = new SettingsOverlayModel(SettingsPageRegistry.Product());
        model.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        model.OpenAt(SettingsPageRegistry.StartupId);
        Assert.Equal(SettingsPageKind.Startup, model.ActivePage.Kind);
        Assert.True(model.ShowsApply);
        Assert.Equal("on", model.Items[model.ItemIndex].Id);
        Assert.True(model.SelectItem(1));
        Assert.Equal("off", model.Items[model.ItemIndex].Id);
        Assert.True(model.Apply());
        Assert.NotNull(model.PendingPatch);
        Assert.Single(model.PendingPatch!);
        Assert.Equal("ui.startup_splash", model.PendingPatch[0].Path);
        Assert.Equal("false", model.PendingPatch[0].TomlLiteral);
    }

    [Fact]
    public void Startup_apply_writes_splash_true()
    {
        var model = new SettingsOverlayModel(SettingsPageRegistry.Product());
        model.Bind(ThemeRuntime.Default, new AttachUiConfig { StartupSplash = false });
        model.OpenAt(SettingsPageRegistry.StartupId);
        Assert.Equal("off", model.Items[model.ItemIndex].Id);
        Assert.True(model.SelectItem(0));
        Assert.True(model.Apply());
        Assert.Equal("true", model.PendingPatch![0].TomlLiteral);
    }

    [Fact]
    public void Key_engine_settings_include_the_startup_page()
    {
        var engine = new KeyEngine(KeyBindingTable.CompileOrThrow(KeysConfig.Default()));
        Assert.Equal(SettingsPageRegistry.StartupId, engine.Settings.Pages[^1].Id);
        engine.Settings.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        engine.Settings.OpenAt(SettingsPageRegistry.StartupId);
        Assert.Equal(SettingsPageKind.Startup, engine.Settings.ActivePage.Kind);
    }
}
