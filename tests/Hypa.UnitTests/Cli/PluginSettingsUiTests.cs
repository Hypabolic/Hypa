using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Settings;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class PluginSettingsUiTests
{
    [Fact]
    public void Core_tabs_stay_first_and_keep_names_with_plugin_pages()
    {
        var pluginPages =
            new[]
            {
                new PluginSettingsPageView(
                    "atomic.workbench",
                    "Atomic",
                    [new PluginSettingsFieldView("gateway_url", "string", "Atomic gateway", [], "")]),
            };
        var registry = SettingsPageRegistry.ProductWithPlugins(pluginPages);
        Assert.Equal(9, registry.Pages.Count);
        Assert.Equal(SettingsPageRegistry.ThemeId, registry.Pages[0].Id);
        Assert.Equal(SettingsPageRegistry.IntegrationsId, registry.Pages[5].Id);
        Assert.Equal(SettingsPageRegistry.ReleaseNotesId, registry.Pages[6].Id);
        Assert.Equal(SettingsPageRegistry.StartupId, registry.Pages[7].Id);
        Assert.Equal("plugin:atomic.workbench", registry.Pages[8].Id);
        Assert.Equal(SettingsPageKind.Hosted, registry.Pages[8].Kind);
        Assert.Equal("Atomic", registry.Pages[8].Label);
    }

    [Fact]
    public void Unlink_removes_plugin_tab_from_registry()
    {
        var withPlugin = SettingsPageRegistry.ProductWithPlugins(
        [
            new PluginSettingsPageView(
                "atomic.workbench",
                "Atomic",
                [new PluginSettingsFieldView("gateway_url", "string", "Atomic gateway", [], "")]),
        ]);
        var withoutPlugin = SettingsPageRegistry.Product();
        Assert.Equal(9, withPlugin.Pages.Count);
        Assert.Equal(8, withoutPlugin.Pages.Count);
        Assert.DoesNotContain(withoutPlugin.Pages, p => p.Kind == SettingsPageKind.Hosted);
    }

    [Fact]
    public void Hosted_page_renders_core_controls_for_supported_types()
    {
        var page = new PluginSettingsPageView(
            "example.settings",
            "Example",
            [
                new PluginSettingsFieldView("gateway_url", "string", "Atomic gateway", [], ""),
                new PluginSettingsFieldView("poll_ms", "integer", "Poll period", [], "15000"),
                new PluginSettingsFieldView("enabled", "boolean", "Enabled", [], "false"),
                new PluginSettingsFieldView("mode", "choice", "Mode", ["fast", "slow"], "fast"),
            ]);
        var items = PluginSettingsFieldControl.ItemsFor(page);
        Assert.Equal(4, items.Count);
        Assert.Contains(items, i => i.Label.Contains("Atomic gateway", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Label.Contains("15000", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Label.Contains("off", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Label.Contains("fast", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_on_hosted_page_queues_config_write()
    {
        var pluginPages =
            new[]
            {
                new PluginSettingsPageView(
                    "example.settings",
                    "Example",
                    [new PluginSettingsFieldView("enabled", "boolean", "Enabled", [], "false")]),
            };
        var registry = SettingsPageRegistry.ProductWithPlugins(pluginPages);
        var model = new SettingsOverlayModel(registry);
        model.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        model.BindPluginPages(pluginPages, registry);
        model.OpenAt("plugin:example.settings");
        Assert.True(model.Apply());
        Assert.NotNull(model.PendingPluginConfigWrite);
        Assert.Equal("example.settings", model.PendingPluginConfigWrite!.PluginId);
        Assert.Equal("enabled", model.PendingPluginConfigWrite.Key);
        Assert.Equal("true", model.PendingPluginConfigWrite.Value);
    }
}
