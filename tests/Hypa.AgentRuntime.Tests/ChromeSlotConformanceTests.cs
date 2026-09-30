using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Application.Status;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Plugins;
using Hypa.Cli.Attach.Settings;
using Hypa.Cli.Attach.Sidebar;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class ChromeSlotConformanceTests : IDisposable
{
    private const string PluginId = BundledPluginLayout.SlotsPluginId;
    private const string ResourceId = "plugin:slots/queue";
    private const string SlotsConfig = """
        [[ui.sidebar.section]]
        id = "slots"
        resource = "plugin:slots/queue"
        title = "Slots"
        rows = ["state_icon", "$title", "$age"]
        sort = "attention"

        [ui]
        tab_bar_right = [{ type = "resource", resource = "plugin:slots/queue", format = "$attention_count waiting", max_items = 1 }]
        """;

    private readonly string _root;
    private readonly SystemPluginFiles _files;
    private readonly SystemPluginPathRoots _paths;
    private readonly PluginHostService _host;
    private readonly BundledPluginService _bundled;

    public ChromeSlotConformanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-slots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new SystemPluginFiles();
        _paths = new SystemPluginPathRoots(_root);
        _host = new PluginHostService(
            _files,
            new SystemPluginClock(),
            _paths,
            new PluginManifestParser(),
            new FilePluginRegistry(_files, _paths),
            new ProcessPluginLauncher(),
            processRegistry: new PluginProcessRegistry(),
            refreshRunner: new ProcessPluginRefreshRunner());
        _bundled = new BundledPluginService(_files, _paths);
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
    public void Fixture_plugin_is_hypa_owned_and_not_atomic()
    {
        var manifest = OfficialPluginAssets.SlotsManifest;
        Assert.Contains("id = \"slots\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("atomic", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plugin:atomic", manifest, StringComparison.Ordinal);
        var staged = _bundled.StageSlots();
        Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
        Assert.Equal(PluginId, staged.Value.PluginId);
        Assert.Equal("/bin/echo", staged.Value.Command0);
        var linked = _host.Link(staged.Value.PluginRoot, true);
        Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
        var plugin = Assert.Single(_host.List(null).Value, p => p.PluginId == PluginId);
        Assert.Single(plugin.Resources);
        Assert.Contains(plugin.Actions, a => a is { Id: "open", Palette: true });
        Assert.Contains(plugin.MenuItems, m => m.Id == "plan");
        Assert.Contains(plugin.MenuItems, m => m.Id == "reload");
        Assert.Single(plugin.SettingsFields);
        Assert.Single(plugin.Panes, p => p.Id == "board");
        Assert.Contains(PluginGrantCatalog.ResourcePublish, plugin.RequestedGrants);
        Assert.Contains(PluginGrantCatalog.NotificationRequest, plugin.RequestedGrants);
        Assert.Contains(PluginGrantCatalog.PaneOpenSelf, plugin.RequestedGrants);
    }

    [Fact]
    public void Missing_plugin_does_not_fail_attach_config()
    {
        var parsed = TomlAttachConfigBinder.Bind(SlotsConfig);
        Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.ToString());
        var frame = Compose(parsed.Value.Ui, linked: false);
        Assert.DoesNotContain(frame.Panes, p => p.Id == PluginId);
        var chips = StatusResourceChipComposer.Compose(parsed.Value.Ui.TabBarRight, resources: []);
        Assert.Empty(chips);
    }

    [Fact]
    public async Task Fixture_fills_every_slot_and_disable_drops_all()
    {
        LinkSlots();
        var attachments = new InMemoryAttachmentRegistry();
        attachments.Create("p1", "c1", "sub1", AttachmentModes.Control, "lease1");
        var factory = TestPaneFactories.Capturing();
        var (cp, _) = NewPlane(factory, attachments);
        try
        {
            var published = await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, QueueEnvelope(1));
            Assert.False(published.GetProperty("ignored").GetBoolean());
            AssertResultKeys(FixtureCatalog.MethodResponsePath(ProtocolMethods.PluginResourcePublish), published);

            var listed = await cp.DispatchAsync(ProtocolMethods.PluginResourceList, null, CancellationToken.None);
            AssertResultKeys(FixtureCatalog.MethodResponsePath(ProtocolMethods.PluginResourceList), listed);
            Assert.Equal(1, listed.GetProperty("resources").GetArrayLength());

            var got = await DispatchAsync(
                cp,
                ProtocolMethods.PluginResourceGet,
                new JsonObject { ["resource_id"] = ResourceId });
            AssertResultKeys(FixtureCatalog.MethodResponsePath(ProtocolMethods.PluginResourceGet), got);

            var ui = TomlAttachConfigBinder.Bind(SlotsConfig).Value.Ui;
            var resources = MapLiveResources(got.GetProperty("resource"));
            var frame = Compose(ui, resources: resources);
            var pane = Assert.Single(frame.Panes, p => p.Id == PluginId);
            Assert.Contains("Plan retry", pane.Rows[0].Label, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b", pane.Rows[0].Label, StringComparison.Ordinal);

            var hit = new ChromeHit(
                ChromeHitKind.SidebarCollectionItem,
                PaneId: "slots:work-42",
                CollectionActivation: new SidebarCollectionActivation
                {
                    SectionId = PluginId,
                    PluginId = PluginId,
                    ItemId = "work-42",
                    ResourceId = ResourceId,
                    Revision = 1,
                    ActionId = "open",
                    Target = new SidebarCollectionTarget { PaneId = "w1:p3" },
                });
            var primary = ChromeHitApply.Apply(hit, overflowOffset: 0, maxOverflowOffset: 0);
            Assert.Equal("w1:p3", primary.FocusPaneId);

            var chips = StatusResourceChipComposer.Compose(ui.TabBarRight, ToDto(got.GetProperty("resource")));
            var chip = Assert.Single(chips);
            Assert.Equal(StatusChipKind.Resource, chip.Kind);
            Assert.Equal("2 waiting", chip.Text);

            var plugins = _host.List(null).Value;
            Assert.Contains(PluginChromeCatalog.PaletteTargets(plugins), row => row.Label == "Open slot item");
            Assert.Contains(
                PluginChromeCatalog.MenuItems(plugins, PluginMenuContexts.CollectionItem),
                item => item.Label == "Plan slot item");
            Assert.Contains(GlobalMenuModel.Items(plugins), item => item.Label == "Reload slots");

            var settings = SettingsPageRegistry.ProductWithPlugins(
            [
                new PluginSettingsPageView(
                    PluginId,
                    "Slots",
                    [new PluginSettingsFieldView("theme", "string", "Slots theme", [], "dark")]),
            ]);
            Assert.Contains(settings.Pages, p => p.Id == "plugin:slots");

            var token = _host.PeekGrantToken(PluginId);
            var toast = await cp.DispatchAsync(
                ProtocolMethods.NotificationShow,
                JsonDocument.Parse("""{"title":"wait","source":"plugin:slots"}""").RootElement,
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.Equal(NotificationShowReasons.Shown, toast.GetProperty("reason").GetString());

            var targetId = await CreateWorkspacePaneAsync(cp);
            var opened = await cp.DispatchAsync(
                ProtocolMethods.PluginPaneOpen,
                JsonDocument.Parse(
                    $$"""{"plugin_id":"slots","entrypoint":"board","placement":"split","target_pane_id":{{JsonSerializer.Serialize(targetId)}}}""")
                    .RootElement,
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(
                opened.GetProperty("plugin_pane").GetProperty("pane_id").GetString()));

            var removed = await DispatchAsync(
                cp,
                ProtocolMethods.PluginResourceRemove,
                new JsonObject { ["resource_id"] = ResourceId });
            AssertResultKeys(FixtureCatalog.MethodResponsePath(ProtocolMethods.PluginResourceRemove), removed);
            Assert.True(removed.GetProperty("removed").GetBoolean());

            await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, QueueEnvelope(2));
            var disabled = await DispatchAsync(
                cp,
                ProtocolMethods.PluginDisable,
                new JsonObject { ["plugin_id"] = PluginId });
            Assert.False(disabled.GetProperty("plugin").GetProperty("enabled").GetBoolean());
            Assert.Empty(_host.OwnedPaneIds(PluginId));
            var after = await cp.DispatchAsync(ProtocolMethods.PluginResourceList, null, CancellationToken.None);
            Assert.Equal(0, after.GetProperty("resources").GetArrayLength());
            var disabledPlugins = _host.List(null).Value;
            Assert.Empty(PluginChromeCatalog.PaletteTargets(disabledPlugins));
            Assert.Empty(PluginChromeCatalog.MenuItems(disabledPlugins, PluginMenuContexts.CollectionItem));
            Assert.DoesNotContain(GlobalMenuModel.Items(disabledPlugins), item => item.Label == "Reload slots");
            var hidden = Compose(ui, linked: false);
            Assert.DoesNotContain(hidden.Panes, p => p.Id == PluginId);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unknown_projection_fails_closed()
    {
        LinkSlots();
        var (cp, _) = NewPlane();
        try
        {
            var envelope = QueueEnvelope(1);
            envelope["schema"] = "hypa.projection.collection.v0";
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, envelope));
            Assert.Equal(PluginError.UnknownProjection, ex.ErrorCode);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Schema_marks_plugin_resources_implemented()
    {
        var document = ProtocolSchemaCatalog.Create();
        foreach (var method in new[]
                 {
                     ProtocolMethods.PluginResourceList,
                     ProtocolMethods.PluginResourceGet,
                     ProtocolMethods.PluginResourcePublish,
                     ProtocolMethods.PluginResourceRemove,
                 })
        {
            Assert.Equal(
                ProtocolSchemaStatus.Implemented,
                Assert.Single(document.Methods, m => m.Name == method).Status);
        }

        Assert.Equal(
            ProtocolSchemaStatus.Implemented,
            Assert.Single(document.Events, e => e.Name == ProtocolEventTypes.ResourceChanged).Status);
    }

    [Fact]
    public void Mouse_and_prefix_scripts_keep_input_kinds()
    {
        AssertScript("mouse-slots", mouse: true);
        AssertScript("prefix-slots", mouse: false);
    }

    private void LinkSlots()
    {
        var staged = _bundled.StageSlots();
        Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
        var linked = _host.Link(staged.Value.PluginRoot, true);
        Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
    }

    private (ControlPlaneService Cp, AppState App) NewPlane(
        IPaneRuntimeFactory? factory = null,
        IAttachmentRegistry? attachments = null)
    {
        var app = new AppState(SessionId.New("slots"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            factory ?? TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachments: attachments,
            plugins: _host);
        return (cp, app);
    }

    private static async Task<string> CreateWorkspacePaneAsync(ControlPlaneService cp)
    {
        var created = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(
                $$"""{"cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}},"command":"/bin/true","create_pane":true}""")
                .RootElement,
            CancellationToken.None);
        return created.GetProperty("pane").GetProperty("pane_id").GetString()!;
    }

    private static async Task<JsonElement> DispatchAsync(ControlPlaneService cp, string method, JsonObject body)
    {
        using var doc = JsonDocument.Parse(body.ToJsonString());
        return await cp.DispatchAsync(method, doc.RootElement, CancellationToken.None);
    }

    private static JsonObject QueueEnvelope(long revision) =>
        new()
        {
            ["owner_id"] = "plugin:slots",
            ["resource_id"] = ResourceId,
            ["schema"] = PluginResourceLimits.CollectionSchema,
            ["revision"] = revision,
            ["value"] = new JsonObject
            {
                ["summary"] = "3 need attention",
                ["items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "work-42",
                        ["label"] = "Plan\u001b retry",
                        ["status"] = "blocked",
                        ["attention"] = 2,
                        ["tokens"] = new JsonObject
                        {
                            ["title"] = "Plan\u001b retry",
                            ["age"] = "12m",
                        },
                        ["action"] = "open",
                    },
                },
            },
        };

    private static void AssertResultKeys(string fixturePath, JsonElement live)
    {
        var fixture = JsonSerializer.Deserialize(
            FixtureCatalog.Load(fixturePath),
            ProtocolJsonContext.Default.RpcResponse);
        Assert.NotNull(fixture);
        using var fixtureDoc = JsonDocument.Parse(fixture.Result!.Value.GetRawText());
        foreach (var prop in fixtureDoc.RootElement.EnumerateObject())
            Assert.True(live.TryGetProperty(prop.Name, out _), prop.Name);
    }

    private static IReadOnlyList<SidebarPluginResourceView> MapLiveResources(JsonElement resource)
    {
        var dto = JsonSerializer.Deserialize(resource.GetRawText(), ProtocolJsonContext.Default.PluginResourceDto);
        Assert.NotNull(dto);
        return
        [
            new SidebarPluginResourceView
            {
                ResourceId = dto.ResourceId ?? ResourceId,
                Revision = dto.Revision,
                Freshness = dto.Freshness ?? PluginResourceLimits.FreshnessReady,
                Summary = dto.Value?.Summary,
                Items = (dto.Value?.Items ?? [])
                    .Select(item => new SidebarPluginCollectionItemView
                    {
                        Id = item.Id ?? "",
                        Label = item.Label ?? "",
                        Status = item.Status ?? "unknown",
                        Attention = item.Attention,
                        Tokens = item.Tokens ?? new Dictionary<string, string>(StringComparer.Ordinal),
                        ActionId = item.Action,
                        Target = item.Target is null
                            ? null
                            : new SidebarCollectionTarget { PaneId = item.Target.PaneId },
                    })
                    .ToArray(),
            },
        ];
    }

    private static IReadOnlyList<PluginResourceDto> ToDto(JsonElement resource)
    {
        var dto = JsonSerializer.Deserialize(resource.GetRawText(), ProtocolJsonContext.Default.PluginResourceDto);
        Assert.NotNull(dto);
        return [dto];
    }

    private static SidebarFrame Compose(
        AttachUiConfig ui,
        bool linked = true,
        IReadOnlyList<SidebarPluginResourceView>? resources = null)
    {
        var input = new SidebarComposeInput
        {
            Ui = ui,
            Expanded = true,
            RequestedWidth = 26,
            LinkedPluginIds = linked
                ? new HashSet<string>(StringComparer.Ordinal) { PluginId }
                : new HashSet<string>(StringComparer.Ordinal),
            PluginResources = resources ?? [],
        };
        return SidebarSectionComposer.Compose(
            input,
            SidebarPluginSectionRegistry.Build(ui, continuityEnabled: true, input.LinkedPluginIds));
    }

    private static void AssertScript(string suite, bool mouse)
    {
        var dir = Path.Combine(FindRepoRoot(), "tests/Hypa.AgentRuntime.Tests/Fixtures/m6", suite);
        var meta = File.ReadAllText(Path.Combine(dir, "meta.json"));
        var expected = File.ReadAllText(Path.Combine(dir, "expected.json"));
        Assert.Contains("reconstructed_tty_script", meta, StringComparison.Ordinal);
        Assert.Contains(mouse ? "sgr_1006" : "prefix_keys", meta, StringComparison.Ordinal);
        foreach (var name in new[] { "attach", "sidebar", "chip", "command", "menu", "settings", "pane", "toast", "detach" })
            Assert.Contains("\"" + name + "\"", expected, StringComparison.Ordinal);
        if (mouse)
            Assert.Contains("\"kind\":\"click\"", File.ReadAllText(Path.Combine(dir, "session.script.ndjson")), StringComparison.Ordinal);

        foreach (var raw in File.ReadAllLines(Path.Combine(dir, "session.script.ndjson")))
        {
            if (!raw.Contains("\"stdin\"", StringComparison.Ordinal))
                continue;
            var start = raw.IndexOf("\"data\":\"", StringComparison.Ordinal);
            var end = raw.LastIndexOf('"');
            var data = raw[(start + 8)..end];
            var bytes = Convert.FromBase64String(data);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            if (mouse)
                Assert.DoesNotContain((char)0x02, text);
            else
            {
                Assert.DoesNotContain("\u001b[<", text, StringComparison.Ordinal);
                Assert.DoesNotContain("\u001b[M", text, StringComparison.Ordinal);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
