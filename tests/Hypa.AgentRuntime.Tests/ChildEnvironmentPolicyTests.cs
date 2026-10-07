using Hypa.AgentRuntime.Application;
using Hypa.Terminal.Pty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ChildEnvironmentPolicyTests
{
    [Fact]
    public void Hosted_denies_full_parent_dump_and_secret_keys()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/agent",
            ["TERM"] = "xterm",
            ["SECRET_TOKEN"] = "should-not-leak",
            ["API_KEY"] = "nope",
            ["PASSWORD"] = "nope",
            ["MY_SECRET"] = "nope",
            ["RANDOM_TOOL_VAR"] = "should-not-appear",
            ["OPENAI_API_KEY"] = "nope",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(explicitEnv: null, parentEnv: parent);

        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("/home/agent", env["HOME"]);
        Assert.False(env.ContainsKey("SECRET_TOKEN"));
        Assert.False(env.ContainsKey("API_KEY"));
        Assert.False(env.ContainsKey("PASSWORD"));
        Assert.False(env.ContainsKey("MY_SECRET"));
        Assert.False(env.ContainsKey("RANDOM_TOOL_VAR"));
        Assert.False(env.ContainsKey("OPENAI_API_KEY"));
        Assert.True(ChildEnvironmentPolicy.IsSecretKey("OPENAI_API_KEY"));
        Assert.False(ChildEnvironmentPolicy.IsAllowedHostedKey("RANDOM_TOOL_VAR", null));
        Assert.Equal(ChildEnvironmentPolicy.DefaultTerm, env["TERM"]);
    }

    [Fact]
    public void Hosted_allows_base_and_context_abi_from_parent()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/bin",
            ["HOME"] = "/home/u",
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["LANG"] = "en_US.UTF-8",
            ["ATOMIC_CONTEXT_PACK_PATH"] = "/tmp/pack.json",
            ["ATOMIC_CONTEXT_PACK_ID"] = "pack-1",
            ["ATOMIC_CONTEXT_PACK_SHA256"] = "abc",
            ["ATOMIC_CONTEXT_ABI"] = "1",
            ["ATOMIC_RUN_ID"] = "run_1",
            ["ATOMIC_STEP_ID"] = "step_1",
            ["ATOMIC_AGENT_SESSION_ID"] = "sess_1",
            ["LEAK_ME"] = "no",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(parentEnv: parent);

        Assert.Equal("/tmp/pack.json", env["ATOMIC_CONTEXT_PACK_PATH"]);
        Assert.Equal("pack-1", env["ATOMIC_CONTEXT_PACK_ID"]);
        Assert.Equal("abc", env["ATOMIC_CONTEXT_PACK_SHA256"]);
        Assert.Equal("1", env["ATOMIC_CONTEXT_ABI"]);
        Assert.Equal("run_1", env["ATOMIC_RUN_ID"]);
        Assert.Equal("step_1", env["ATOMIC_STEP_ID"]);
        Assert.Equal("sess_1", env["ATOMIC_AGENT_SESSION_ID"]);
        Assert.Equal("en_US.UTF-8", env["LANG"]);
        Assert.False(env.ContainsKey("LEAK_ME"));
    }

    [Fact]
    public void Hosted_passes_login_session_identity_from_parent()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/bin",
            ["HOME"] = "/Users/u",
            ["USER"] = "u",
            ["LOGNAME"] = "u",
            ["SHELL"] = "/bin/zsh",
            ["TMPDIR"] = "/var/folders/xx/T/",
            ["SSH_AUTH_SOCK"] = "/tmp/agent.sock",
            ["XDG_CONFIG_HOME"] = "/Users/u/.config",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(parentEnv: parent);

        Assert.Equal("u", env["USER"]);
        Assert.Equal("u", env["LOGNAME"]);
        Assert.Equal("/bin/zsh", env["SHELL"]);
        Assert.Equal("/var/folders/xx/T/", env["TMPDIR"]);
        Assert.Equal("/tmp/agent.sock", env["SSH_AUTH_SOCK"]);
        Assert.Equal("/Users/u/.config", env["XDG_CONFIG_HOME"]);
    }

    [Fact]
    public void Hosted_defaults_user_identity_when_parent_lacks_it()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/bin",
            ["LOGNAME"] = "from-logname",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(parentEnv: parent);
        Assert.Equal("from-logname", env["USER"]);
        Assert.Equal("from-logname", env["LOGNAME"]);

        var bare = ChildEnvironmentBuilder.BuildHosted(parentEnv: new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Equal(Environment.UserName, bare["USER"]);
        Assert.Equal(Environment.UserName, bare["LOGNAME"]);
    }

    [Fact]
    public void Hosted_preserves_explicit_pane_spawn_env()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/bin",
        };
        var explicitEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CUSTOM_TOOL"] = "yes",
            ["ATOMIC_RUN_ID"] = "from-options",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(explicitEnv, parent);
        Assert.Equal("yes", env["CUSTOM_TOOL"]);
        Assert.Equal("from-options", env["ATOMIC_RUN_ID"]);
    }

    [Fact]
    public void Builder_injects_defaults_when_missing()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = ChildEnvironmentBuilder.BuildHosted(parentEnv: parent);

        Assert.Equal(ChildEnvironmentPolicy.DefaultTerm, env["TERM"]);
        Assert.Equal(ChildEnvironmentPolicy.DefaultColorTerm, env["COLORTERM"]);
        Assert.Equal(ChildEnvironmentPolicy.DefaultLang, env["LANG"]);
        Assert.True(env.ContainsKey("HOME"));
        Assert.True(env.ContainsKey("PATH"));
        Assert.False(string.IsNullOrEmpty(env["PATH"]));
    }

    [Fact]
    public void Local_unmanaged_strips_secret_keys_from_parent()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["EDITOR"] = "vim",
            ["GITHUB_TOKEN"] = "secret",
            ["AWS_SECRET_ACCESS_KEY"] = "secret",
        };

        var env = ChildEnvironmentBuilder.BuildLocalUnmanaged(parentEnv: parent);
        Assert.Equal("vim", env["EDITOR"]);
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("AWS_SECRET_ACCESS_KEY"));
    }

    [Fact]
    public void Hosted_and_local_do_not_inherit_parent_pane_ids()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/bin",
            ["HOME"] = "/home/u",
            [PaneIdEnvironment.HypaPaneId] = "outer-hypa-pane",
            [PaneIdEnvironment.HerdrPaneId] = "outer-herdr-pane",
        };

        var hosted = ChildEnvironmentBuilder.BuildHosted(parentEnv: parent);
        Assert.False(hosted.ContainsKey(PaneIdEnvironment.HypaPaneId));
        Assert.False(hosted.ContainsKey(PaneIdEnvironment.HerdrPaneId));

        var local = ChildEnvironmentBuilder.BuildLocalUnmanaged(parentEnv: parent);
        Assert.False(local.ContainsKey(PaneIdEnvironment.HypaPaneId));
        Assert.False(local.ContainsKey(PaneIdEnvironment.HerdrPaneId));
        Assert.Equal("/bin", local["PATH"]);
    }

    [Fact]
    public void Hosted_preserves_explicit_pane_id_for_real_panes()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/bin",
            [PaneIdEnvironment.HypaPaneId] = "outer-hypa-pane",
        };
        var explicitEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PaneIdEnvironment.HypaPaneId] = "this-pane",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(explicitEnv, parent);
        Assert.Equal("this-pane", env[PaneIdEnvironment.HypaPaneId]);
        Assert.False(env.ContainsKey(PaneIdEnvironment.HerdrPaneId));
    }

    [Fact]
    public void Hosted_forces_pane_term_over_parent_term()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/agent",
            ["TERM"] = "xterm-ghostty",
            ["COLORTERM"] = "falsecolor",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(explicitEnv: null, parentEnv: parent);

        Assert.Equal(ChildEnvironmentPolicy.DefaultTerm, env["TERM"]);
        Assert.Equal(ChildEnvironmentPolicy.DefaultColorTerm, env["COLORTERM"]);
        Assert.DoesNotContain("xterm-ghostty", env.Values);
    }

    [Fact]
    public void Local_unmanaged_forces_pane_term_over_parent_term()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/agent",
            ["TERM"] = "xterm-ghostty",
            ["COLORTERM"] = "falsecolor",
            ["EDITOR"] = "vim",
        };

        var env = ChildEnvironmentBuilder.BuildLocalUnmanaged(parentEnv: parent);

        Assert.Equal(ChildEnvironmentPolicy.DefaultTerm, env["TERM"]);
        Assert.Equal(ChildEnvironmentPolicy.DefaultColorTerm, env["COLORTERM"]);
        Assert.Equal("vim", env["EDITOR"]);
        Assert.DoesNotContain("xterm-ghostty", env.Values);
    }

    [Fact]
    public void Explicit_term_overrides_pane_term()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/agent",
            ["TERM"] = "xterm-ghostty",
            ["COLORTERM"] = "falsecolor",
        };
        var explicitEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = "vt100",
            ["COLORTERM"] = "24bit",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(explicitEnv, parent);

        Assert.Equal("vt100", env["TERM"]);
        Assert.Equal("24bit", env["COLORTERM"]);
    }

    [Fact]
    public void Terminfo_never_reaches_hosted_child()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/agent",
            ["TERM"] = "xterm-ghostty",
            ["TERMINFO"] = "/Applications/Ghostty.app/Contents/Resources/terminfo",
        };

        var env = ChildEnvironmentBuilder.BuildHosted(parentEnv: parent);

        Assert.False(env.ContainsKey("TERMINFO"));
        Assert.Equal(ChildEnvironmentPolicy.DefaultTerm, env["TERM"]);
    }
}
