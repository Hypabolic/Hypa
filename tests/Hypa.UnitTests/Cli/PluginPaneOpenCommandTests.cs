using System.Text.Json.Nodes;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// </summary>
public sealed class PluginPaneOpenCommandTests
{
    [Fact]
    public void Parse_keeps_positionals_and_pane_open_flags()
    {
        var parsed = ControlPlaneCliCommands.Parse(
        [
            "plugin", "pane", "open", "annotate", "editor",
            "--cwd", "/plugin/root",
            "--width", "88",
            "--height", "24",
            "--target-pane", "pane-42",
            "--placement", "popup",
            "--env", "HYPA_ANNOTATE_PENDING=/state/pending.json",
            "--focus",
        ]);

        Assert.Equal(["plugin", "pane", "open", "annotate", "editor"], parsed.Tokens);
        Assert.Equal("/plugin/root", parsed.Flags["--cwd"]);
        Assert.Equal("88", parsed.Flags["--width"]);
        Assert.Equal("24", parsed.Flags["--height"]);
        Assert.Equal("pane-42", parsed.Flags["--target-pane"]);
        Assert.Equal("popup", parsed.Flags["--placement"]);
        Assert.Equal("true", parsed.Flags["--focus"]);
        Assert.Equal(
            ["HYPA_ANNOTATE_PENDING=/state/pending.json"],
            parsed.EnvAssignments);
    }

    [Fact]
    public void BuildPluginPaneOpenParams_sets_cwd_env_cells_target_and_focus()
    {
        var payload = OpenParams(
            "plugin", "pane", "open", "annotate", "editor",
            "--cwd", "/plugin/root",
            "--width", "88",
            "--height", "24",
            "--target-pane", "pane-42",
            "--placement", "popup",
            "--env", "HYPA_ANNOTATE_PENDING=/state/pending.json",
            "--focus");

        Assert.Equal("annotate", payload["plugin_id"]!.GetValue<string>());
        Assert.Equal("editor", payload["entrypoint"]!.GetValue<string>());
        Assert.Equal("popup", payload["placement"]!.GetValue<string>());
        Assert.Equal("/plugin/root", payload["cwd"]!.GetValue<string>());
        Assert.Equal("pane-42", payload["target_pane_id"]!.GetValue<string>());
        Assert.True(payload["focus"]!.GetValue<bool>());
        Assert.Equal(88, payload["width"]!.GetValue<int>());
        Assert.Equal(24, payload["height"]!.GetValue<int>());
        Assert.Equal(
            "/state/pending.json",
            payload["env"]!["HYPA_ANNOTATE_PENDING"]!.GetValue<string>());
        Assert.DoesNotContain("HERDR_", payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPluginPaneOpenParams_width_percent_stays_string()
    {
        var payload = OpenParams(
            "plugin", "pane", "open", "annotate", "editor",
            "--width", "80%",
            "--height", "40%");

        Assert.Equal("80%", payload["width"]!.GetValue<string>());
        Assert.Equal("40%", payload["height"]!.GetValue<string>());
    }

    [Fact]
    public void BuildPluginPaneOpenParams_focus_defaults_true()
    {
        var payload = OpenParams("plugin", "pane", "open", "annotate", "editor");
        Assert.True(payload["focus"]!.GetValue<bool>());
        Assert.Null(payload["cwd"]);
        Assert.Null(payload["env"]);
        Assert.Null(payload["width"]);
        Assert.Null(payload["height"]);
        Assert.Null(payload["target_pane_id"]);
    }

    [Fact]
    public void BuildPluginPaneOpenParams_no_focus_sets_false()
    {
        var parsed = ControlPlaneCliCommands.Parse(
            ["plugin", "pane", "open", "annotate", "editor", "--no-focus"]);
        Assert.Equal("false", parsed.Flags["--focus"]);
        var payload = ControlPlaneCliCommands.BuildPluginPaneOpenParams(
            parsed.Tokens.Skip(3).ToArray(), parsed);
        Assert.False(payload["focus"]!.GetValue<bool>());
    }

    [Fact]
    public void Parse_repeatable_env_joins_and_last_key_wins()
    {
        var payload = OpenParams(
            "plugin", "pane", "open", "annotate", "editor",
            "--env", "FOO=1",
            "--env", "BAR=2",
            "--env", "FOO=3");

        var env = payload["env"]!;
        Assert.Equal("3", env["FOO"]!.GetValue<string>());
        Assert.Equal("2", env["BAR"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_env_accepts_empty_value()
    {
        var payload = OpenParams(
            "plugin", "pane", "open", "annotate", "editor",
            "--env", "FOO=");
        Assert.Equal("", payload["env"]!["FOO"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_env_requires_key_value_separator()
    {
        var ex = Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(
                ["plugin", "pane", "open", "annotate", "editor", "--env", "FOO"]));
        Assert.Equal("env must use KEY=VALUE", ex.Message);
    }

    [Fact]
    public void Parse_env_rejects_empty_key()
    {
        var ex = Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(
                ["plugin", "pane", "open", "annotate", "editor", "--env", "=VALUE"]));
        Assert.Equal("env key must not be empty", ex.Message);
    }

    [Fact]
    public void Parse_env_keeps_unit_separator_in_value()
    {
        var payload = OpenParams(
            "plugin", "pane", "open", "annotate", "editor",
            "--env", "FOO=a\u001fb");
        Assert.Equal("a\u001fb", payload["env"]!["FOO"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_env_rejects_nul_bytes()
    {
        var keyNul = Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(
                ["plugin", "pane", "open", "annotate", "editor", "--env", "FOO\0=1"]));
        Assert.Equal("env must not contain NUL bytes", keyNul.Message);

        var valueNul = Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(
                ["plugin", "pane", "open", "annotate", "editor", "--env", "FOO=1\0"]));
        Assert.Equal("env must not contain NUL bytes", valueNul.Message);
    }

    [Fact]
    public void Parse_env_requires_a_value()
    {
        var ex = Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(
                ["plugin", "pane", "open", "annotate", "editor", "--env"]));
        Assert.Equal("--env requires a value", ex.Message);
    }

    [Fact]
    public void BuildPluginPaneOpenParams_extra_positional_is_usage_error()
    {
        var parsed = ControlPlaneCliCommands.Parse(
            ["plugin", "pane", "open", "annotate", "editor", "extra"]);
        var ex = Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.BuildPluginPaneOpenParams(
                parsed.Tokens.Skip(3).ToArray(), parsed));
        Assert.Contains(
            "usage: hypa plugin pane open <plugin_id> <entrypoint>",
            ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("--cwd", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--env KEY=VALUE", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--width", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--height", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--target-pane", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--focus|--no-focus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPluginPaneOpenParams_missing_entrypoint_is_usage_error()
    {
        var parsed = ControlPlaneCliCommands.Parse(["plugin", "pane", "open", "annotate"]);
        var ex = Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.BuildPluginPaneOpenParams(
                parsed.Tokens.Skip(3).ToArray(), parsed));
        Assert.StartsWith("usage: hypa plugin pane open", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_lists_plugin_pane_open_flags()
    {
        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains(
            "plugin pane open <plugin_id> <entrypoint>",
            help,
            StringComparison.Ordinal);
        Assert.Contains("--cwd PATH", help, StringComparison.Ordinal);
        Assert.Contains("--env KEY=VALUE", help, StringComparison.Ordinal);
        Assert.Contains("--width SIZE", help, StringComparison.Ordinal);
        Assert.Contains("--height SIZE", help, StringComparison.Ordinal);
        Assert.Contains("--target-pane PANE", help, StringComparison.Ordinal);
        Assert.Contains("--focus|--no-focus", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--plugin", help.Split("plugin pane open")[1].Split('\n')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("--entrypoint", help, StringComparison.Ordinal);
    }

    private static JsonObject OpenParams(params string[] args)
    {
        var parsed = ControlPlaneCliCommands.Parse(args);
        return ControlPlaneCliCommands.BuildPluginPaneOpenParams(
            parsed.Tokens.Skip(3).ToArray(), parsed);
    }
}
