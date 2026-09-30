using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Config;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class AttachExperimentalImeConfigTests
{
    [Fact]
    public void Experimental_ime_keys_default_off()
    {
        var loaded = TomlAttachConfigBinder.Bind("");
        Assert.True(loaded.IsOk);
        var experimental = loaded.Value.Experimental;
        Assert.False(experimental.RevealHiddenCursorForCjkIme);
        Assert.Empty(experimental.CjkImeAgents);
        Assert.Equal("steady_block", experimental.CjkImeCursorShape);
        Assert.False(experimental.SwitchAsciiInputSourceInPrefix);
    }

    [Fact]
    public void Experimental_ime_keys_parse()
    {
        var loaded = TomlAttachConfigBinder.Bind("""
            [experimental]
            reveal_hidden_cursor_for_cjk_ime = true
            cjk_ime_agents = ["claude", "codex"]
            cjk_ime_cursor_shape = "bar"
            switch_ascii_input_source_in_prefix = true
            """);
        Assert.True(loaded.IsOk);
        var experimental = loaded.Value.Experimental;
        Assert.True(experimental.RevealHiddenCursorForCjkIme);
        Assert.Equal(new[] { "claude", "codex" }, experimental.CjkImeAgents);
        Assert.Equal("bar", experimental.CjkImeCursorShape);
        Assert.True(experimental.SwitchAsciiInputSourceInPrefix);
    }

    [Fact]
    public void Default_toml_marks_ime_keys_experimental()
    {
        var toml = AttachConfigDefaults.Toml;
        Assert.Contains("Experimental. Default is off.", toml, StringComparison.Ordinal);
        Assert.Contains("reveal_hidden_cursor_for_cjk_ime = false", toml, StringComparison.Ordinal);
        Assert.Contains("switch_ascii_input_source_in_prefix = false", toml, StringComparison.Ordinal);
        Assert.Contains("Windows switches a Korean IME only.", toml, StringComparison.Ordinal);
    }
}
