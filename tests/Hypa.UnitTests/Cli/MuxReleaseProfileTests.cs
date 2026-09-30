using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxReleaseProfileTests
{
    [Fact]
    public void MuxRelease_DefaultConfigOmitsCubesAndBinds()
    {
        var text = AttachConfigDefaults.Toml;
        Assert.DoesNotContain("[ui.sidebar.cubes]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ui.sidebar.cubes", text, StringComparison.Ordinal);
        var parsed = TomlAttachConfigBinder.Bind(text);
        Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.ToString());
        Assert.Equal("catppuccin", parsed.Value.Theme.Name);
        Assert.Equal("default", parsed.Value.Session.Name);
        Assert.Equal(["ctrl+b"], parsed.Value.Keys.Prefix.Specs);
        Assert.Equal(["prefix+h"], parsed.Value.Keys.FocusPaneLeft.Specs);
        Assert.Equal(["prefix+z"], parsed.Value.Keys.Zoom.Specs);
        Assert.DoesNotContain(
            parsed.Value.Keys.FocusPaneLeft.Specs,
            spec => spec.Contains("ctrl+alt", StringComparison.Ordinal));
        Assert.Equal(ToastPosition.BottomRight, parsed.Value.Ui.Toast.HypaPosition);
        Assert.Equal(ChromeGlyphSet.Unicode, parsed.Value.Ui.Glyphs);
        Assert.Contains("chrome.glyphs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MuxRelease_SidebarRegistryIncludesCubesForConnect()
    {
        var frame = SidebarSectionComposer.Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                RequestedWidth = 26,
            },
            ChromeSectionRegistry.MuxRelease());
        Assert.Equal(["spaces", "agents", "cubes"], frame.Panes.Select(p => p.Id).ToArray());
        Assert.Contains(frame.Panes, p => p.Id == SidebarTokenGrammar.CubesId);
        Assert.Contains(frame.Sections, s => s.Id == SidebarTokenGrammar.CubesId);
    }

    [Fact]
    public void MuxRelease_PreservesPaneInnerGeometry()
    {
        var input = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            RequestedWidth = 26,
        };
        var releaseFrame = SidebarSectionComposer.Compose(input, ChromeSectionRegistry.MuxRelease());
        var fullFrame = SidebarSectionComposer.Compose(input, ChromeSectionRegistry.Core());
        var root = new LayoutNodeDto { Type = "pane", PaneId = "p1" };
        var releaseGeo = LayoutChromeGeometry.Compute(
            80,
            24,
            root,
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 26,
            sidebarFrame: releaseFrame);
        var fullGeo = LayoutChromeGeometry.Compute(
            80,
            24,
            root,
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 26,
            sidebarFrame: fullFrame);
        Assert.Equal(fullGeo.Panes[0].Content, releaseGeo.Panes[0].Content);
        Assert.Equal(fullGeo.SidebarWidth, releaseGeo.SidebarWidth);
        Assert.Equal(fullGeo.Sidebar?.Cols, releaseGeo.Sidebar?.Cols);
    }

    [Fact]
    public void MuxRelease_MobileFallbackIncludesCubesSection()
    {
        var stubs = SidebarLiveModel.ToStubRows(
            SidebarSectionComposer.Compose(
                new SidebarComposeInput
                {
                    Ui = AttachUiConfig.Default,
                    Expanded = true,
                    RequestedWidth = 26,
                },
                ChromeSectionRegistry.MuxRelease()));
        var model = MobileSwitcherModel.Build(
            64,
            24,
            stubs,
            [("t1", "one", true)]);
        Assert.Contains(model.Rows, r => r.Label == "Cubes");
        Assert.Contains(model.Rows, r => r.Label == SidebarTokenGrammar.CubesEmptyText);
    }

    [Fact]
    public void MuxRelease_OldCubesConfigDoesNotBlockMux()
    {
        var text = AttachConfigDefaults.Toml + """

            [ui.sidebar.cubes]
            row_gap = 1
            rows = [["name", "kind"], ["reachability", "work_title"]]
            """;
        var parsed = TomlAttachConfigBinder.Bind(text);
        Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.ToString());
        Assert.Equal(1, parsed.Value.Ui.Sidebar.Cubes.RowGap);

        var previous = Environment.GetEnvironmentVariable(AttachSessionResolver.SessionEnv);
        try
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, null);
            var resolved = ControlPlaneCliCommands.TryResolveSession(
                ControlPlaneCliCommands.Parse(["ping"]),
                new StaticTomlLoader(text));
            Assert.True(resolved.IsOk, resolved.IsOk ? "" : resolved.Error.ToString());
            Assert.False(string.IsNullOrWhiteSpace(resolved.Value));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, previous);
        }
    }

    [Fact]
    public void MuxRelease_OldCubesConfigEnablesNoAdapter()
    {
        var text = AttachConfigDefaults.Toml + """

            [ui.sidebar.cubes]
            row_gap = 1
            rows = [["name", "kind"]]
            """;
        var parsed = TomlAttachConfigBinder.Bind(text);
        Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.ToString());
        var frame = SidebarSectionComposer.Compose(
            new SidebarComposeInput
            {
                Ui = parsed.Value.Ui,
                Expanded = true,
                RequestedWidth = 26,
            },
            ChromeSectionRegistry.MuxRelease());
        Assert.Contains(frame.Panes, p => p.Id == SidebarTokenGrammar.CubesId);
        Assert.Contains(frame.Sections, s => s.Id == SidebarTokenGrammar.CubesId);
        var live = MouseTestGeom.ApplyLive(
            MouseTestGeom.ApplyPort(),
            MouseTestGeom.Split());
        Assert.False(live.Release.ContinuityEnabled);
        Assert.Null(live.CubeCatalog);
    }

    private sealed class StaticTomlLoader(string text) : IAttachConfigLoader
    {
        public string ResolvePath() => "/tmp/hypa-mux-rel.toml";

        public AttachConfigResult<AttachClientConfig> Load() => TomlAttachConfigBinder.Bind(text);

        public AttachConfigResult<AttachClientConfig> Parse(string value) =>
            TomlAttachConfigBinder.Bind(value);

        public string DefaultToml() => text;

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Ok(new("ok", null, null, false));
    }
}
