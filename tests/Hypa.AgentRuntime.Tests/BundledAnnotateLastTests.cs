using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.Annotate.Application;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class BundledAnnotateLastTests : IDisposable
{
    private readonly string _root;
    private readonly PluginHostService _host;

    public BundledAnnotateLastTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-bundled-last-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BundledAnnotateFixtures.WriteHypaPair(_root);
        _host = new PluginHostService(
            new SystemPluginFiles(),
            new SystemPluginClock(),
            new SystemPluginPathRoots(_root),
            new PluginManifestParser(),
            new FilePluginRegistry(new SystemPluginFiles(), new SystemPluginPathRoots(_root)),
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
    public void Last_review_open_arguments_use_positionals_and_pending_env()
    {
        var arguments = HypaBinPluginPaneOpener.BuildOpenArguments(
            "annotate",
            "last-review",
            "/plugin/root",
            "pane-42",
            HypaBinPluginPaneOpener.EditorWidth,
            HypaBinPluginPaneOpener.EditorHeight,
            AnnotateEnv.LastReviewPath,
            "/state/last-pending-1.json");

        Assert.Equal(
            [
                "plugin", "pane", "open", "annotate", "last-review",
                "--placement", "popup",
                "--width", "88",
                "--height", "24",
                "--cwd", "/plugin/root",
                "--focus",
                "--target-pane", "pane-42",
                "--env", AnnotateEnv.LastReviewPath + "=/state/last-pending-1.json",
            ],
            arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("HERDR_", StringComparison.Ordinal));
    }

    [Fact]
    public void Send_text_arguments_do_not_append_a_newline()
    {
        var arguments = HypaBinPluginPaneSender.BuildSendTextArguments("pane-42", "review note");
        Assert.Equal(["plugin", "pane", "send-text", "pane-42", "--text", "review note"], arguments);
        Assert.DoesNotContain('\n', arguments[^1]);
    }

    [Fact]
    public void Stage_manifest_requests_send_text_grant_and_last_action()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-bundled-last-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = BundledAnnotateFixtures.WriteHypaPair(root);
            var bundled = new BundledPluginService(new SystemPluginFiles(), new SystemPluginPathRoots(root));
            var staged = bundled.StageAnnotate(binary);
            Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);

            var text = File.ReadAllText(staged.Value.ManifestPath);
            Assert.Contains("pane.send_text:self", text, StringComparison.Ordinal);
            Assert.Contains("notification.request", text, StringComparison.Ordinal);

            var parsed = new PluginManifestParser().Parse(text);
            Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.Message);
            Assert.Contains(parsed.Value.RequestedGrants, g => g == PluginGrantCatalog.PaneSendTextSelf);
            Assert.Contains(parsed.Value.RequestedGrants, g => g == PluginGrantCatalog.NotificationRequest);
            var last = Assert.Single(parsed.Value.Actions, action => action.Id == "last");
            Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, last.Command[0]);
            Assert.Equal(["last"], last.Command.Skip(1).ToArray());
            Assert.NotNull(last.Description);
            Assert.Contains("Hypa extension", last.Description, StringComparison.Ordinal);
            Assert.Contains("not Plannotator", last.Description, StringComparison.Ordinal);

            var review = Assert.Single(parsed.Value.Panes, pane => pane.Id == "last-review");
            Assert.Equal("popup", review.Placement);
            Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, review.Command[0]);
            Assert.Equal(["last-review"], review.Command.Skip(1).ToArray());
            Assert.DoesNotContain(
                review.Command,
                part => string.Equals(part, "bundled", StringComparison.Ordinal));
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

    [Fact]
    public async Task Linked_annotate_send_text_reaches_bound_pane_with_one_newline()
    {
        var binary = Path.Combine(_root, "hypa");
        var bundled = new BundledPluginService(new SystemPluginFiles(), new SystemPluginPathRoots(_root));
        var staged = bundled.StageAnnotate(binary);
        Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
        Assert.True(_host.Link(staged.Value.PluginRoot, true).IsOk);
        var token = _host.PeekGrantToken("annotate");
        Assert.False(string.IsNullOrWhiteSpace(token));

        var factory = TestPaneFactories.Capturing();
        var app = new AppState(SessionId.New("annotate-last-send"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            plugins: _host);
        try
        {
            var targetId = await CreateWorkspacePaneAsync(cp);
            var otherId = await CreateWorkspacePaneAsync(cp);
            _host.NoteDeliveryTarget("annotate", targetId);
            var before = factory.Writes.Count;
            var result = await cp.DispatchAsync(
                ProtocolMethods.PluginPaneSendText,
                SendTextParams(targetId, "review note"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(before + 1, factory.Writes.Count);
            Assert.Equal("review note\n", factory.Writes[^1]);

            var refused = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginPaneSendText,
                    SendTextParams(otherId, "nope"),
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrors.CapabilityMissing, refused.Message);
            Assert.Equal(before + 1, factory.Writes.Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static async Task<string> CreateWorkspacePaneAsync(ControlPlaneService cp)
    {
        var created = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse("""{"label":"w"}""").RootElement,
            connection: null,
            CancellationToken.None);
        var workspaceId = created.GetProperty("workspace_id").GetString()!;
        var tab = await cp.DispatchAsync(
            ProtocolMethods.TabCreate,
            JsonDocument.Parse($$"""{"workspace_id":{{JsonSerializer.Serialize(workspaceId)}}}""").RootElement,
            connection: null,
            CancellationToken.None);
        return tab.GetProperty("pane").GetProperty("pane_id").GetString()!;
    }

    private static JsonElement SendTextParams(string paneId, string text) =>
        JsonDocument.Parse(
            $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"text":{{JsonSerializer.Serialize(text)}}}""")
            .RootElement;

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
}
