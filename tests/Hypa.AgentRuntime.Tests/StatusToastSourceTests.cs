using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Notification;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class StatusToastSourceTests : IDisposable
{
    private readonly string _root;
    private readonly MemoryPluginFiles _files;
    private readonly PluginHostService _host;

    public StatusToastSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-toast-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new MemoryPluginFiles();
        var paths = new SystemPluginPathRoots(_root);
        _host = new PluginHostService(
            _files,
            new SystemPluginClock(),
            paths,
            new PluginManifestParser(),
            new FilePluginRegistry(_files, paths),
            new ProcessPluginLauncher(),
            processRegistry: new PluginProcessRegistry(),
            refreshRunner: new ProcessPluginRefreshRunner());
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
    public void Notification_request_is_advertised_and_allowlisted()
    {
        Assert.Contains(PluginGrantCatalog.NotificationRequest, PluginGrantCatalog.Advertised);
        Assert.True(PluginGrantCatalog.IsDispatchAllowed(ProtocolMethods.NotificationShow));
    }

    [Fact]
    public async Task Plugin_source_without_grant_is_denied()
    {
        LinkNotifyPlugin(includeGrant: false);
        var token = _host.PeekGrantToken("example.notify");
        var (cp, _) = NewPlane();
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.NotificationShow,
                    Params("plugin:example.notify", "wait"),
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.CapabilityInvalid, ex.Code);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Plugin_source_with_grant_shows_toast()
    {
        LinkNotifyPlugin(includeGrant: true);
        var token = _host.PeekGrantToken("example.notify");
        var attachments = new InMemoryAttachmentRegistry();
        attachments.Create("p1", "c1", "sub1", AttachmentModes.Control, "lease1");
        var (cp, _) = NewPlane(attachments);
        try
        {
            var result = await cp.DispatchAsync(
                ProtocolMethods.NotificationShow,
                Params("plugin:example.notify", "wait", paneId: "p_target"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.Equal(NotificationShowReasons.Shown, result.GetProperty("reason").GetString());
            Assert.Equal("plugin:example.notify", result.GetProperty("source").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Per_source_mute_returns_disabled()
    {
        LinkNotifyPlugin(includeGrant: true);
        var token = _host.PeekGrantToken("example.notify");
        var attachments = new InMemoryAttachmentRegistry();
        attachments.Create("p1", "c1", "sub1", AttachmentModes.Control, "lease1");
        var config = AttachClientConfig.Default with
        {
            Ui = AttachClientConfig.Default.Ui with
            {
                Toast = AttachClientConfig.Default.Ui.Toast with
                {
                    Sources = new Dictionary<string, ToastSourceMode>(StringComparer.Ordinal)
                    {
                        ["plugin:example.notify"] = ToastSourceMode.Off,
                    },
                },
            },
        };
        var (cp, _) = NewPlane(attachments, config);
        try
        {
            var result = await cp.DispatchAsync(
                ProtocolMethods.NotificationShow,
                Params("plugin:example.notify", "wait"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.Equal(NotificationShowReasons.Disabled, result.GetProperty("reason").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Per_source_rate_limit_returns_rate_limited()
    {
        LinkNotifyPlugin(includeGrant: true);
        var token = _host.PeekGrantToken("example.notify");
        var attachments = new InMemoryAttachmentRegistry();
        attachments.Create("p1", "c1", "sub1", AttachmentModes.Control, "lease1");
        var config = AttachClientConfig.Default with
        {
            Ui = AttachClientConfig.Default.Ui with
            {
                Toast = AttachClientConfig.Default.Ui.Toast with { DelaySeconds = 0 },
            },
        };
        var (cp, _) = NewPlane(attachments, config);
        try
        {
            for (var i = 0; i < ControlPlaneService.NotificationCoreBurst; i++)
            {
                var ok = await cp.DispatchAsync(
                    ProtocolMethods.NotificationShow,
                    Params("plugin:example.notify", $"n{i}"),
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None);
                Assert.Equal(NotificationShowReasons.Shown, ok.GetProperty("reason").GetString());
            }

            var limited = await cp.DispatchAsync(
                ProtocolMethods.NotificationShow,
                Params("plugin:example.notify", "overflow"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.Equal(NotificationShowReasons.RateLimited, limited.GetProperty("reason").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Human_plugin_source_without_grant_is_rejected()
    {
        var attachments = new InMemoryAttachmentRegistry();
        attachments.Create("p1", "c1", "sub1", AttachmentModes.Control, "lease1");
        var (cp, _) = NewPlane(attachments);
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.NotificationShow,
                    Params("plugin:example.notify", "wait"),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Contains("notification.request", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Plugin_cannot_set_position_or_colour()
    {
        LinkNotifyPlugin(includeGrant: true);
        var token = _host.PeekGrantToken("example.notify");
        var attachments = new InMemoryAttachmentRegistry();
        attachments.Create("p1", "c1", "sub1", AttachmentModes.Control, "lease1");
        var (cp, _) = NewPlane(attachments);
        try
        {
            var body = new JsonObject
            {
                ["title"] = "wait",
                ["source"] = "plugin:example.notify",
                ["position"] = "top-left",
            };
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.NotificationShow,
                    JsonDocument.Parse(body.ToJsonString()).RootElement,
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Contains("host-owned", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(ToastDelivery.Off)]
    [InlineData(ToastDelivery.Hypa)]
    [InlineData(ToastDelivery.Terminal)]
    [InlineData(ToastDelivery.System)]
    public void Existing_delivery_modes_still_work(ToastDelivery delivery)
    {
        var director = new NotificationDirector(new AttachUiConfig
        {
            Toast = new AttachToastConfig
            {
                Delivery = delivery,
                DelaySeconds = 0,
                HypaPosition = ToastPosition.BottomRight,
            },
        });

        var toasts = director.OnNotificationShown("wait", "body", NotificationSources.Core, NotificationSounds.None, "p1");
        if (delivery == ToastDelivery.Hypa)
            Assert.Single(toasts);
        else
            Assert.Empty(toasts);
    }

    [Fact]
    public void Toast_click_still_focuses_target()
    {
        var director = new NotificationDirector(new AttachUiConfig
        {
            Toast = new AttachToastConfig
            {
                Delivery = ToastDelivery.Hypa,
                DelaySeconds = 0,
            },
        });

        director.OnNotificationShown("wait", null, "plugin:example.notify", NotificationSounds.None, "p_target");
        Assert.Equal("p_target", director.LastTargetPaneId);

        var toasts = director.SnapshotToasts();
        var hit = Assert.Single(toasts);
        Assert.Equal("p_target", hit.PaneId);
        Assert.True(hit.Rect.Cols > 0);
    }

    private void LinkNotifyPlugin(bool includeGrant)
    {
        var manifest = DefaultManifest("example.notify");
        if (includeGrant)
        {
            manifest += """

            [grants]
            request = ["notification.request"]
            """;
        }

        var dir = WritePlugin("example.notify", manifest);
        Assert.True(_host.Link(dir, true).IsOk);
    }

    private (ControlPlaneService Cp, AppState App) NewPlane(
        IAttachmentRegistry? attachments = null,
        AttachClientConfig? attachConfig = null)
    {
        var app = new AppState(SessionId.New("toast-src"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachments: attachments,
            attachConfigRuntime: attachConfig is null ? null : new StaticAttachConfigRuntime(attachConfig),
            plugins: _host);
        return (cp, app);
    }

    private static JsonElement Params(string source, string title, string? paneId = null)
    {
        var body = new JsonObject
        {
            ["title"] = title,
            ["source"] = source,
        };
        if (!string.IsNullOrWhiteSpace(paneId))
            body["pane_id"] = paneId;
        return JsonDocument.Parse(body.ToJsonString()).RootElement;
    }

    private string WritePlugin(string id, string manifest)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        _files.WriteAllText(Path.Combine(dir, PluginHostService.ManifestFileName), manifest);
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
