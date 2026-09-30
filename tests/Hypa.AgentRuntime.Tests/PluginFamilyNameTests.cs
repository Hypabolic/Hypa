using Hypa.AgentRuntime.Application.Plugins;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PluginFamilyNameTests
{
    [Fact]
    public void Manifest_file_is_hypa_plugin_and_cli_has_no_marketplace()
    {
        Assert.Equal("hypa-plugin.toml", PluginHostService.ManifestFileName);
        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.DoesNotContain("marketplace", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plugin", help, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewed catalog", help, StringComparison.OrdinalIgnoreCase);
    }
}
