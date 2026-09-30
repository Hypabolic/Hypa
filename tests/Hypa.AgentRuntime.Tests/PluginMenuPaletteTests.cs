using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using SystemPluginPathRoots = Hypa.AgentRuntime.Infrastructure.Plugins.SystemPluginPathRoots;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Plugins;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PluginMenuPaletteTests : IDisposable
{
    private readonly string _root;
    private readonly MemoryPluginFiles _files;
    private readonly PluginHostService _host;

    public PluginMenuPaletteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-plugin-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new MemoryPluginFiles();
        var paths = new SystemPluginPathRoots(_root);
        _host = new PluginHostService(
            _files,
            new FakePluginClock(),
            paths,
            new PluginManifestParser(),
            new FilePluginRegistry(_files, paths),
            new RecordingLauncher());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Menu_items_bind_for_allowed_contexts()
    {
        var dir = WritePlugin("example.menu", DefaultManifest("example.menu") + MenuPaletteManifest());
        var linked = _host.Link(dir, true);
        Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
        var plugin = Assert.Single(_host.List(null).Value, p => p.PluginId == "example.menu");
        var menuItem = Assert.Single(plugin.MenuItems);
        Assert.Equal("plan", menuItem.Id);
        Assert.Contains(plugin.Actions, action => action is { Id: "open", Palette: true });
    }

    [Fact]
    public void Agent_row_context_fails_closed()
    {
        var dir = WritePlugin("example.agent-row", DefaultManifest("example.agent-row") + """

            [[actions]]
            id = "open"
            title = "Open"
            contexts = ["agent_row"]
            command = ["/bin/echo", "ok"]
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginMenuContextError.AgentRowForbidden, result.Error.Code);
    }

    [Fact]
    public void Unknown_menu_context_fails_closed()
    {
        var dir = WritePlugin("example.bad-ctx", DefaultManifest("example.bad-ctx") + """

            [[actions]]
            id = "open"
            title = "Open"
            command = ["/bin/echo", "ok"]

            [[menu_items]]
            id = "plan"
            title = "Plan"
            contexts = ["sidebar"]
            action = "open"
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginMenuContextError.UnknownContext, result.Error.Code);
    }

    [Fact]
    public void Suggested_key_core_collision_fails_closed()
    {
        var dir = WritePlugin("example.collision", DefaultManifest("example.collision") + """

            [[actions]]
            id = "open"
            title = "Open"
            key = "prefix+q"
            command = ["/bin/echo", "ok"]
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.SuggestedKeyCoreCollision, result.Error.Code);
    }

    [Fact]
    public void Palette_actions_list_in_goto_and_command_picker_catalogs()
    {
        var dir = WritePlugin("example.palette", DefaultManifest("example.palette") + MenuPaletteManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var plugins = _host.List(null).Value;
        var gotoRows = PluginChromeCatalog.PaletteTargets(plugins);
        var pickerRows = PluginChromeCatalog.CommandPickerTargets(plugins);
        Assert.Equal(gotoRows.Count, pickerRows.Count);
        Assert.Contains(gotoRows, row => row.Label == "Open item");
    }

    [Fact]
    public void Collection_item_menu_invokes_action()
    {
        var dir = WritePlugin("example.invoke", DefaultManifest("example.invoke") + MenuPaletteManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var plugins = _host.List(null).Value;
        var items = ContextMenuModel.CollectionItemMenuItems(plugins);
        var item = Assert.Single(items);
        Assert.Equal("example.invoke.open", item.PluginQualifiedActionId);
        var invoked = _host.InvokeAction(item.PluginQualifiedActionId!, null, null);
        Assert.True(invoked.IsOk);
        Assert.Equal("example.invoke", invoked.Value.Action.PluginId);
        Assert.Equal("open", invoked.Value.Action.ActionId);
    }

    [Fact]
    public void Disable_and_unlink_remove_menu_and_palette_rows()
    {
        var dir = WritePlugin("example.drop", DefaultManifest("example.drop") + MenuPaletteManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        Assert.NotEmpty(PluginChromeDiscovery.PaletteActions(_host.List(null).Value));
        Assert.True(_host.SetEnabled("example.drop", false).IsOk);
        Assert.Empty(PluginChromeDiscovery.PaletteActions(_host.List(null).Value));
        Assert.True(_host.SetEnabled("example.drop", true).IsOk);
        Assert.NotEmpty(PluginChromeDiscovery.PaletteActions(_host.List(null).Value));
        Assert.True(_host.Unlink("example.drop").IsOk);
        Assert.Empty(PluginChromeDiscovery.MenuItemsForContext(_host.List(null).Value, PluginMenuContexts.GlobalMenu));
    }

    [Fact]
    public void Suggested_key_does_not_compile_into_key_table()
    {
        var dir = WritePlugin("example.suggest", DefaultManifest("example.suggest") + """

            [[actions]]
            id = "open"
            title = "Open"
            key = "prefix+shift+9"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var action = Assert.Single(_host.List(null).Value).Actions[0];
        Assert.Equal("prefix+shift+9", action.SuggestedKey);
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        Assert.False(table.TryLookup(AttachClientMode.Prefix, KeyChord.Parse("shift+9"), out _));
    }

    [Fact]
    public void User_plugin_action_key_compiles_and_core_collision_fails_closed()
    {
        var ok = KeyBindingTable.Compile(KeysConfig.Default() with
        {
            Commands =
            [
                new KeyCommandBinding("prefix+shift+9", "example.menu.open", "plugin_action", "Open item"),
            ],
        });
        Assert.True(ok.IsOk);

        var collision = KeyBindingTable.Compile(KeysConfig.Default() with
        {
            Commands = [new KeyCommandBinding("prefix+q", "example.menu.open", "plugin_action")],
        });
        Assert.False(collision.IsOk);
        Assert.Equal(KeyCompileError.DuplicateBinding, collision.Error.Code);
    }

    [Fact]
    public void Help_shows_user_key_not_manifest_suggested_key()
    {
        var dir = WritePlugin("example.help", DefaultManifest("example.help") + """

            [[actions]]
            id = "open"
            title = "Open"
            key = "prefix+shift+9"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default() with
        {
            Commands =
            [
                new KeyCommandBinding("prefix+shift+c", "example.help.open", "plugin_action", "User open"),
            ],
        });
        var help = KeybindHelpModel.FromTable(table);
        Assert.Contains(help.AllRows, row => row.Action == "User open");
        Assert.DoesNotContain(help.AllRows, row => row.Chord.Contains("shift+9", StringComparison.Ordinal));
    }

    [Fact]
    public void Global_menu_registers_plugin_items_for_context()
    {
        var dir = WritePlugin("example.global", DefaultManifest("example.global") + """

            [[actions]]
            id = "reload"
            title = "Reload plugin"
            command = ["/bin/echo", "reload"]

            [[menu_items]]
            id = "reload"
            title = "Reload plugin"
            contexts = ["global_menu"]
            action = "reload"
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var items = GlobalMenuModel.Items(_host.List(null).Value);
        Assert.Contains(items, item => item.Label == "Reload plugin");
    }

    private string WritePlugin(string id, string manifest)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PluginHostService.ManifestFileName);
        _files.WriteAllText(path, manifest);
        return dir;
    }

    private static string DefaultManifest(string id) =>
        $"""
        id = "{id}"
        name = "Test {id}"
        version = "0.1.0"
        min_hypa_version = "0.1.0"
        platforms = ["linux", "macos"]
        """;

    private static string MenuPaletteManifest() =>
        """

        [[actions]]
        id = "open"
        title = "Open item"
        contexts = ["collection_item"]
        command = ["/bin/echo", "ok"]
        palette = true

        [[menu_items]]
        id = "plan"
        title = "Plan this item"
        contexts = ["collection_item"]
        action = "open"
        """;

    private sealed class FakePluginClock : IPluginClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch.AddDays(1);
    }

    private sealed class RecordingLauncher : IPluginProcessLauncher
    {
        public bool TryStart(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            int outputCapBytes,
            Action<PluginProcessExit> onExit,
            out string? error)
        {
            error = null;
            onExit(new PluginProcessExit(0, "", "", null));
            return true;
        }
    }

    private sealed class MemoryPluginFiles : IPluginFiles
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

        public bool FileExists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public bool DirectoryExists(string path) => _dirs.Contains(Path.GetFullPath(path));

        public string ReadAllText(string path) => _files[Path.GetFullPath(path)];

        public void WriteAllText(string path, string contents)
        {
            var full = Path.GetFullPath(path);
            _files[full] = contents;
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
                _dirs.Add(parent);
        }

        public void CreateDirectory(string path) => _dirs.Add(Path.GetFullPath(path));

        public bool DeleteFile(string path) => _files.Remove(Path.GetFullPath(path));

        public bool DeleteDirectory(string path)
        {
            var full = Path.GetFullPath(path);
            var prefix = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var removed = _dirs.Remove(full);
            var nestedDirs = _dirs.Where(d => d.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            foreach (var dir in nestedDirs)
            {
                _dirs.Remove(dir);
                removed = true;
            }

            var nestedFiles = _files.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) || key == full)
                .ToArray();
            foreach (var file in nestedFiles)
            {
                _files.Remove(file);
                removed = true;
            }

            return removed;
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);
    }
}
