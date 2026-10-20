using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PredictiveEchoConfigTests
{
    [Fact]
    public void Predictive_echo_defaults_to_adaptive()
    {
        var loaded = TomlAttachConfigBinder.Bind("");
        Assert.True(loaded.IsOk);
        Assert.Equal(PredictiveEchoMode.Adaptive, loaded.Value.Experimental.PredictiveEcho);
    }

    [Theory]
    [InlineData("adaptive", PredictiveEchoMode.Adaptive)]
    [InlineData("always", PredictiveEchoMode.Always)]
    [InlineData("never", PredictiveEchoMode.Never)]
    public void Predictive_echo_parses_each_value(string value, PredictiveEchoMode expected)
    {
        var loaded = TomlAttachConfigBinder.Bind($"""
            [experimental]
            predictive_echo = "{value}"
            """);
        Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
        Assert.Equal(expected, loaded.Value.Experimental.PredictiveEcho);
    }

    [Theory]
    [InlineData("\"sometimes\"")]
    [InlineData("\"Adaptive\"")]
    [InlineData("true")]
    public void An_unknown_predictive_echo_value_is_a_config_error(string value)
    {
        var loaded = TomlAttachConfigBinder.Bind($"""
            [experimental]
            predictive_echo = {value}
            """);
        Assert.False(loaded.IsOk);
        var error = Assert.Single(loaded.Errors);
        Assert.Equal("experimental.predictive_echo", error.Key);
        Assert.Contains("experimental.predictive_echo", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_predictive_echo_string_names_the_values()
    {
        var loaded = TomlAttachConfigBinder.Bind("""
            [experimental]
            predictive_echo = "sometimes"
            """);
        Assert.False(loaded.IsOk);
        Assert.Equal(AttachConfigError.InvalidValue, loaded.Error.Code);
        Assert.Equal("experimental.predictive_echo must be adaptive / always / never.", loaded.Error.Message);
    }

    [Fact]
    public void Default_toml_lists_predictive_echo_as_adaptive()
    {
        var toml = AttachConfigDefaults.Toml;
        Assert.Contains("predictive_echo = \"adaptive\"", toml, StringComparison.Ordinal);
        var loaded = TomlAttachConfigBinder.Bind(toml);
        Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
        Assert.Equal(PredictiveEchoMode.Adaptive, loaded.Value.Experimental.PredictiveEcho);
    }
}
