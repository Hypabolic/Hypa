using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Annotate.Application;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class BundledAnnotateManagerTests
{
    [Fact]
    public void Manager_open_arguments_use_positionals_and_target_pane()
    {
        var arguments = HypaBinPluginPaneOpener.BuildOpenArguments(
            "annotate",
            "manager",
            "/plugin/root",
            "pane-42",
            HypaBinPluginPaneOpener.ManagerWidth,
            HypaBinPluginPaneOpener.ManagerHeight,
            envName: null,
            envValue: null);

        Assert.Equal(
            [
                "plugin", "pane", "open", "annotate", "manager",
                "--placement", "popup",
                "--width", "100",
                "--height", "30",
                "--cwd", "/plugin/root",
                "--focus",
                "--target-pane", "pane-42",
            ],
            arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("HERDR_", StringComparison.Ordinal));
    }

    [Fact]
    public void Plugin_popup_area_fits_manager_manifest_cells()
    {
        var (cols, rows) = ControlPlaneService.PluginPopupHostArea(null, null, "100", "30");
        Assert.True(cols >= 100);
        Assert.True(rows >= 30);
        var geometry = PopupGeometry.TryResolve(cols, rows, PopupSize.Cells(100), PopupSize.Cells(30));
        Assert.NotNull(geometry);
        Assert.Equal(100, geometry.OuterCols);
        Assert.Equal(30, geometry.OuterRows);
    }

    [Fact]
    public void Stage_manifest_includes_manage_copy_archive_and_manager_popup()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-bundled-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = BundledAnnotateFixtures.WriteHypaPair(root);
            var bundled = new BundledPluginService(new SystemPluginFiles(), new SystemPluginPathRoots(root));
            var staged = bundled.StageAnnotate(binary);
            Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);

            var parsed = new PluginManifestParser().Parse(File.ReadAllText(staged.Value.ManifestPath));
            Assert.True(parsed.IsOk);
            Assert.Contains(parsed.Value.Actions, action => action.Id == "copy-archive");
            Assert.Contains(parsed.Value.Actions, action => action.Id == "manage");
            var manager = Assert.Single(parsed.Value.Panes, pane => pane.Id == "manager");
            Assert.Equal("popup", manager.Placement);
            Assert.Equal("100", manager.Width);
            Assert.Equal("30", manager.Height);
            Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, manager.Command[0]);
            Assert.Equal(["manager"], manager.Command.Skip(1).ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
