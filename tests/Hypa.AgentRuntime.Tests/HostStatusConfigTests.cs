using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class HostStatusConfigTests
{
    [Fact]
    public void The_default_is_auto() =>
        Assert.Equal(HostStatusMode.Auto, AttachUiConfig.Default.HostStatus);

    [Theory]
    [InlineData("auto", HostStatusMode.Auto)]
    [InlineData("on", HostStatusMode.On)]
    [InlineData("off", HostStatusMode.Off)]
    public void Host_status_parses(string text, HostStatusMode expected)
    {
        var result = TomlAttachConfigBinder.Bind($"""
            [ui]
            host_status = "{text}"
            """);

        Assert.True(result.IsOk);
        Assert.Equal(expected, result.Value!.Ui.HostStatus);
    }

    [Fact]
    public void An_unknown_value_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            host_status = "always"
            """);

        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.host_status"
            && e.Code == AttachConfigError.InvalidValue
            && e.Message.Contains("auto", StringComparison.Ordinal)
            && e.Message.Contains("off", StringComparison.Ordinal));
    }

    [Fact]
    public void The_shipped_default_config_selects_auto()
    {
        var result = TomlAttachConfigBinder.Bind(DefaultConfigText());

        Assert.True(result.IsOk);
        Assert.Equal(HostStatusMode.Auto, result.Value!.Ui.HostStatus);
    }

    private static string DefaultConfigText()
    {
        var assembly = typeof(TomlAttachConfigBinder).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("default-config.toml", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
}
