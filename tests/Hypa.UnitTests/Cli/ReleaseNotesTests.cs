using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.ReleaseNotes;
using Hypa.Cli.Attach.Settings;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class ReleaseNotesTests
{
    [Fact]
    public void Loader_reads_embedded_pack_notes_when_beside_binary_missing()
    {
        var loader = new PackNotesLoader(
            readBesideBinary: () => null,
            readEmbedded: () => "Continuity is unavailable in this release.");
        var notes = loader.TryLoad("1.2.3");
        Assert.NotNull(notes);
        Assert.Equal("1.2.3", notes.Version);
        Assert.Contains("Continuity is unavailable", notes.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_prefers_beside_binary_over_embedded()
    {
        var loader = new PackNotesLoader(
            readBesideBinary: () => "Pack body from archive.",
            readEmbedded: () => "embedded");
        var notes = loader.TryLoad("2.0.0");
        Assert.NotNull(notes);
        Assert.Equal("Pack body from archive.", notes.Body);
    }

    [Fact]
    public void Seen_store_persists_dismissed_version()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Dir, "hypa", PackNotesSeenStore.FileName);
        var store = CreateTestSeenStore(dir.Dir);
        Assert.True(store.TryMarkSeen("0.9.0"));
        Assert.Equal("0.9.0", store.TryLoadSeenVersion());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Service_should_show_on_startup_until_dismissed()
    {
        using var dir = new TempDir();
        var store = CreateTestSeenStore(dir.Dir);
        var loader = new PackNotesLoader(
            readBesideBinary: () => "Continuity is unavailable.",
            readEmbedded: () => null);
        var service = new PackNotesService(loader, store);
        var notes = service.TryLoad();
        Assert.NotNull(notes);

        Assert.True(service.ShouldShowOnStartup());
        Assert.True(service.MarkSeen());
        Assert.False(service.ShouldShowOnStartup());
    }

    [Fact]
    public void KeyEngine_opens_and_dismisses_release_notes_without_human()
    {
        var engine = new KeyEngine(KeyBindingTable.CompileOrThrow(KeysConfig.Default()));
        var notes = new PackNotesDocument("0.1.0", "Continuity is unavailable in this release.");
        var entered = engine.EnterWhatsNew(notes);
        Assert.Contains(entered, ev => ev.Kind is KeyEngineEventKind.EnterMode && ev.Mode is AttachClientMode.WhatsNew);
        Assert.True(engine.ReleaseNotes.IsOpen);

        var dismissed = engine.Feed(new KeyChord(false, false, false, false, "esc"));
        Assert.Contains(dismissed, ev => ev.Kind is KeyEngineEventKind.LeaveMode);
        Assert.Contains(
            dismissed,
            ev => ev.Kind is KeyEngineEventKind.HelpFilter
                && ev.Filter == ReleaseNotesOverlayModel.FilterDismiss);
        Assert.False(engine.ReleaseNotes.IsOpen);
        Assert.Equal(AttachClientMode.Terminal, engine.Mode);
    }

    [Fact]
    public void KeyEngine_scrolls_release_notes_body()
    {
        var engine = new KeyEngine(KeyBindingTable.CompileOrThrow(KeysConfig.Default()));
        var body = string.Join('\n', Enumerable.Range(0, 40).Select(i => "line " + i));
        Assert.True(engine.EnterWhatsNew(new PackNotesDocument("0.1.0", body)).Count > 0);
        engine.ReleaseNotes.Layout = ReleaseNotesPainter.Measure(engine.ReleaseNotes, 80, 24);
        var before = engine.ReleaseNotes.Scroll;
        var scrolled = engine.Feed(new KeyChord(false, false, false, false, "j"));
        Assert.Contains(
            scrolled,
            ev => ev.Kind is KeyEngineEventKind.HelpFilter
                && ev.Filter == ReleaseNotesOverlayModel.FilterScroll);
        Assert.True(engine.ReleaseNotes.Scroll > before);
    }

    [Fact]
    public void Settings_release_notes_page_requests_modal_open()
    {
        var engine = new KeyEngine(
            KeyBindingTable.CompileOrThrow(KeysConfig.Default()),
            settingsPages: SettingsPageRegistry.Product());
        engine.Settings.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        engine.EnterSettings(SettingsPageRegistry.ReleaseNotesId);
        Assert.True(engine.Settings.Apply());
        Assert.True(engine.Settings.PendingOpenReleaseNotes);
    }

    [Fact]
    public void AttachSession_opens_from_settings_and_persists_dismiss()
    {
        using var dir = new TempDir();
        var store = CreateTestSeenStore(dir.Dir);
        var loader = new PackNotesLoader(
            readBesideBinary: () => "Continuity is unavailable.",
            readEmbedded: () => null);
        var service = new PackNotesService(loader, store);
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, settingsPages: SettingsPageRegistry.Product()),
            Table = table,
            Dispatcher = null!,
            PackNotes = service,
        };
        live.Engine.Settings.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        live.Engine.EnterSettings(SettingsPageRegistry.ReleaseNotesId);
        Assert.True(live.Engine.Settings.Apply());

        Assert.True(AttachSession.TryOpenReleaseNotesFromSettings(live));
        Assert.Equal(AttachClientMode.WhatsNew, live.Engine.Mode);
        Assert.True(live.Engine.ReleaseNotes.IsOpen);

        foreach (var ev in live.Engine.Feed(new KeyChord(false, false, false, false, "esc")))
        {
            if (ev.Kind is KeyEngineEventKind.HelpFilter
                && ev.Filter == ReleaseNotesOverlayModel.FilterDismiss)
            {
                AttachSession.TryDismissReleaseNotes(live);
            }
        }

        Assert.False(service.ShouldShowOnStartup());
    }

    [Fact]
    public void AttachSession_auto_opens_on_first_attach_after_upgrade()
    {
        using var dir = new TempDir();
        var store = CreateTestSeenStore(dir.Dir);
        store.TryMarkSeen("0.0.1");
        var loader = new PackNotesLoader(
            readBesideBinary: () => "Continuity is unavailable.",
            readEmbedded: () => null);
        var service = new PackNotesService(loader, store);
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            PackNotes = service,
        };
        var config = new AttachClientConfig { Onboarding = false };
        AttachSession.ApplyPackNotesAtStart(live, config);
        Assert.Equal(AttachClientMode.WhatsNew, live.Engine.Mode);
    }

    [Fact]
    public void Stamp_dims_host_and_draws_accent_box()
    {
        var host = new HostFrame();
        host.Resize(80, 24);
        var marker = new AssembledCell("T", 1, false, AssembledStyle.Default);
        host.Stamp(0, 0, marker);
        var model = new ReleaseNotesOverlayModel();
        Assert.True(model.Open(new PackNotesDocument("0.1.0", "Continuity is unavailable in this release.")));

        ReleaseNotesPainter.Stamp(new HostFrameCellSink(host), model, 80, 24);

        Assert.Equal("T", host.CellAt(0, 0).Text);
        Assert.True(host.CellAt(0, 0).Style.Dim);
        var layout = model.Layout!;
        Assert.Equal("┌", host.CellAt(layout.Panel.Col, layout.Panel.Row).Text);
        Assert.Equal("┐", host.CellAt(layout.Panel.EndCol - 1, layout.Panel.Row).Text);
        Assert.Equal("└", host.CellAt(layout.Panel.Col, layout.Panel.EndRow - 1).Text);
        Assert.Equal("┘", host.CellAt(layout.Panel.EndCol - 1, layout.Panel.EndRow - 1).Text);
        Assert.False(host.CellAt(layout.Panel.Col, layout.Panel.Row).Style.Dim);
        Assert.Contains("v0.1.0", RowText(host, layout.Title.Row, layout.Title.Col, layout.Title.Cols), StringComparison.Ordinal);
        Assert.Contains("esc close", RowText(host, layout.Close.Row, layout.Close.Col, layout.Close.Cols), StringComparison.Ordinal);
        Assert.Contains("what's new in this release", RowText(host, layout.Subtitle.Row, layout.Subtitle.Col, layout.Subtitle.Cols), StringComparison.Ordinal);
        Assert.Contains("scroll", RowText(host, layout.Footer.Row, layout.Footer.Col, layout.Footer.Cols), StringComparison.Ordinal);
        Assert.Contains("esc / enter", RowText(host, layout.Footer.Row, layout.Footer.Col, layout.Footer.Cols), StringComparison.Ordinal);
    }

    [Fact]
    public void Stamp_keeps_close_inside_the_box()
    {
        var host = new HostFrame();
        host.Resize(80, 24);
        var model = new ReleaseNotesOverlayModel();
        Assert.True(model.Open(new PackNotesDocument("0.1.0", "body")));
        ReleaseNotesPainter.Stamp(new HostFrameCellSink(host), model, 80, 24);
        var layout = model.Layout!;
        Assert.True(layout.Panel.Contains(layout.Close.Col, layout.Close.Row));
        Assert.True(layout.Close.EndCol <= layout.Panel.EndCol - 1);
        Assert.True(layout.Close.Col >= layout.Panel.Col + 1);
    }

    private static string RowText(HostFrame host, int row, int col, int cols)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < cols; i++)
            sb.Append(host.CellAt(col + i, row).Text);
        return sb.ToString();
    }

    [Fact]
    public void Markdown_lines_strip_fences_and_render_inline_code()
    {
        var lines = ReleaseNotesMarkdownLines.Build("```\ncode\n```\nUse `hypa attach`.");
        Assert.Equal(2, lines.Count);
        Assert.Equal("code", lines[0]);
        Assert.Equal("Use hypa attach.", lines[1]);
    }

    private static PackNotesSeenStore CreateTestSeenStore(string home) =>
        new(
            env: new TestAttachConfigEnvironment(home),
            fileExists: File.Exists,
            readAllText: File.ReadAllText,
            writeAtomic: WriteSeenAtomic);

    private static void WriteSeenAtomic(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, json);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "hypa-pack-notes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        public string Dir { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class TestAttachConfigEnvironment(string home) : IAttachConfigEnvironment
    {
        public string UserHome => home;

        public string? AppData => null;

        public bool IsWindows => false;

        public bool IsMacOs => false;

        public string? GetVariable(string name) =>
            name == FileAttachConfigLoader.XdgConfigHomeVariable ? home : null;
    }
}
