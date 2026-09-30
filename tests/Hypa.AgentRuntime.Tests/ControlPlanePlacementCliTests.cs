using Hypa.AgentRuntime.Application;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ControlPlanePlacementCliTests
{
    private static readonly object EnvGate = new();

    [Fact]
    public void Create_copies_env_token_unless_lease_or_parent_capability_is_explicit()
    {
        lock (EnvGate)
        {
            var prevToken = Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneToken);
            var prevId = Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneId);
            try
            {
                Environment.SetEnvironmentVariable(PaneIdEnvironment.HypaPaneToken, "occ_env");
                Environment.SetEnvironmentVariable(PaneIdEnvironment.HypaPaneId, "p_env");

                var copied = ControlPlaneCliCommands.BuildPaneCreateParams(
                    ["create", "--command", "/bin/echo", "--placement", "hidden"]);
                Assert.Equal("occ_env", copied["occupant_token"]?.GetValue<string>());
                Assert.Equal("p_env", copied["parent_pane_id"]?.GetValue<string>());

                var lease = ControlPlaneCliCommands.BuildPaneCreateParams(
                    ["create", "--command", "/bin/echo", "--lease-id", "lease_1"]);
                Assert.False(lease.ContainsKey("occupant_token"));
                Assert.False(lease.ContainsKey("parent_pane_id"));
                Assert.Equal("lease_1", lease["lease_id"]?.GetValue<string>());

                var cap = ControlPlaneCliCommands.Parse(
                    ["pane", "show", "--pane-id", "p_child", "--parent-capability", "par_1", "--seq", "1"]);
                var show = ControlPlaneCliCommands.BuildPaneShowParams(cap, ["show", "--pane-id", "p_child"]);
                Assert.Equal("par_1", show["parent_capability"]?.GetValue<string>());
                Assert.False(show.ContainsKey("occupant_token"));
                Assert.Equal(1, show["seq"]?.GetValue<long>());
            }
            finally
            {
                Environment.SetEnvironmentVariable(PaneIdEnvironment.HypaPaneToken, prevToken);
                Environment.SetEnvironmentVariable(PaneIdEnvironment.HypaPaneId, prevId);
            }
        }
    }

    [Fact]
    public void Show_uses_env_pane_id_and_help_lists_show_hide()
    {
        lock (EnvGate)
        {
            var prevId = Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneId);
            try
            {
                Environment.SetEnvironmentVariable(PaneIdEnvironment.HypaPaneId, "p_self");
                var parsed = ControlPlaneCliCommands.Parse(["pane", "show", "--mode", "tiled", "--seq", "3"]);
                var show = ControlPlaneCliCommands.BuildPaneShowParams(parsed, ["show"]);
                Assert.Equal("p_self", show["pane_id"]?.GetValue<string>());
                Assert.Equal("tiled", show["mode"]?.GetValue<string>());
                Assert.Equal(3, show["seq"]?.GetValue<long>());
            }
            finally
            {
                Environment.SetEnvironmentVariable(PaneIdEnvironment.HypaPaneId, prevId);
            }
        }

        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("pane show", help, StringComparison.Ordinal);
        Assert.Contains("pane hide", help, StringComparison.Ordinal);
        Assert.Contains("--parent-capability", help, StringComparison.Ordinal);
        Assert.DoesNotContain("HYPA_ATTACH_CLIENT_ID", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Hide_forwards_lease_and_seq()
    {
        var parsed = ControlPlaneCliCommands.Parse(
            ["pane", "hide", "p9", "--lease-id", "lease_9", "--seq", "4"]);
        var hide = ControlPlaneCliCommands.BuildPaneHideParams(parsed, ["hide", "p9"]);
        Assert.Equal("p9", hide["pane_id"]?.GetValue<string>());
        Assert.Equal("lease_9", hide["lease_id"]?.GetValue<string>());
        Assert.Equal(4, hide["seq"]?.GetValue<long>());
        Assert.False(hide.ContainsKey("occupant_token"));
    }
}
