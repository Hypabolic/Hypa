using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class ProtocolSchemaTests
{
    [Fact]
    public void Schema_covers_post_m6_methods_and_marks_plugin_resources_implemented()
    {
        var document = ProtocolSchemaCatalog.Create();
        Assert.Equal(ProtocolVersion.Name, document.ProtocolName);
        Assert.Equal(ProtocolVersion.Major, document.ProtocolMajor);
        Assert.Equal(ProtocolVersion.Minor, document.ProtocolMinor);
        Assert.Equal(ProtocolAttachEndpoint.EndpointGeneration, document.EndpointGeneration);
        Assert.DoesNotContain(document.Methods, m => m.Name == "agent.attach");
        Assert.Contains(
            ProtocolPaneReadSources.RecentUnwrapped,
            document.PaneReadSources,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            ProtocolPaneReadSources.RecentUnwrappedLegacy,
            document.PaneReadSources,
            StringComparer.Ordinal);

        foreach (var method in ProtocolSchemaCatalog.PostM6Methods)
        {
            var entry = Assert.Single(document.Methods, m => m.Name == method);
            var expected = ProtocolSchemaCatalog.ReservedMethods.Contains(method, StringComparer.Ordinal)
                ? ProtocolSchemaStatus.Reserved
                : ProtocolSchemaStatus.Implemented;
            Assert.Equal(expected, entry.Status);
        }

        Assert.Empty(ProtocolSchemaCatalog.ReservedMethods);
        Assert.Empty(ProtocolSchemaCatalog.ReservedEvents);
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
        Assert.Equal(
            ProtocolSchemaStatus.Implemented,
            Assert.Single(document.Events, e => e.Name == ProtocolEventTypes.PaneScrollChanged).Status);
        Assert.Equal(
            ProtocolSchemaStatus.Implemented,
            Assert.Single(document.Events, e => e.Name == ProtocolEventTypes.LayoutUpdated).Status);
        Assert.Contains(
            ProtocolAttachEndpoint.PresentationReadyRecord,
            document.Attach.Records,
            StringComparer.Ordinal);
        Assert.Equal(ProtocolEventTypes.TerminalRender, document.Render.Event);
        Assert.Contains(document.Render.Payloads, p => p.Kind == TerminalRenderCellsPayload.KindCells);
        Assert.Contains(document.Render.Payloads, p => p.Kind == TerminalRenderSnapshotPayload.KindSnapshot);
        Assert.Contains(document.Render.Payloads, p => p.Kind == TerminalRenderBlitPayload.KindBlit);
        var cursor = Assert.Single(document.Render.Payloads, p => p.Name == "cursor");
        Assert.Equal("cells", cursor.Parent);
        Assert.Contains(cursor.Fields, f => f.Name == "shape" && f.OmitWhen == "0");
    }

    [Fact]
    public void Schema_json_round_trips_with_source_gen()
    {
        var document = ProtocolSchemaCatalog.Create();
        var json = JsonSerializer.Serialize(document, ProtocolJsonContext.Default.ProtocolSchemaDocument);
        var copy = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ProtocolSchemaDocument);
        Assert.NotNull(copy);
        Assert.Equal(document.Methods.Count, copy.Methods.Count);
        Assert.Equal(document.Attach.Methods, copy.Attach.Methods);
        Assert.Equal(document.Render.Payloads.Count, copy.Render.Payloads.Count);
        Assert.Contains("\"kind\":\"cells\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("agent.attach", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Human_schema_text_is_hypa_native()
    {
        var text = ProtocolSchemaText.Render(ProtocolSchemaCatalog.Create());
        Assert.Contains("Hypa API schema", text, StringComparison.Ordinal);
        Assert.Contains("endpoint_generation: 1", text, StringComparison.Ordinal);
        Assert.Contains("hypa api schema --json", text, StringComparison.Ordinal);
        Assert.True(text.Length < 400);
    }

    [Fact]
    public void Pane_read_fixtures_use_recent_unwrapped()
    {
        var req = FixtureCatalog.Load(FixtureCatalog.PaneReadRecentUnwrappedRequest);
        var res = FixtureCatalog.Load(FixtureCatalog.PaneReadRecentUnwrappedResponse);
        Assert.Contains("recent_unwrapped", req, StringComparison.Ordinal);
        Assert.DoesNotContain("recent-unwrapped", req, StringComparison.Ordinal);
        Assert.Contains("recent_unwrapped", res, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(req);
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneRead,
            doc.RootElement.GetProperty("params"),
            out var error),
            error);
        Assert.False(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneRead,
            JsonDocument.Parse("{}").RootElement,
            out _));
    }

    [Fact]
    public async Task Live_pane_read_matches_recent_unwrapped_fixture_keys()
    {
        var cp = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var live = await DispatchAsync(cp, ProtocolMethods.PaneRead, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = ProtocolPaneReadSources.RecentUnwrapped,
                ["lines"] = 100,
            });
            var fixture = JsonSerializer.Deserialize(
                FixtureCatalog.Load(FixtureCatalog.PaneReadRecentUnwrappedResponse),
                ProtocolJsonContext.Default.RpcResponse);
            Assert.NotNull(fixture);
            using var fixtureDoc = JsonDocument.Parse(fixture.Result!.Value.GetRawText());
            foreach (var prop in fixtureDoc.RootElement.EnumerateObject())
                Assert.True(live.TryGetProperty(prop.Name, out _), prop.Name);
            Assert.Equal(ProtocolPaneReadSources.RecentUnwrapped, live.GetProperty("source").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Live_events_wait_fail_closed_matches_unsupported_fixture()
    {
        var cp = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.EventsWait, new JsonObject
                {
                    ["match_event"] = new JsonObject
                    {
                        ["event"] = "pane.agent_status_changed",
                        ["pane_id"] = paneId,
                        ["agent_status"] = "blocked",
                    },
                    ["timeout_ms"] = 100,
                }));
            var fixture = FixtureCatalog.Load(FixtureCatalog.EventsWaitUnsupportedError);
            Assert.Contains(EventsWaitErrors.UnsupportedMatch, fixture, StringComparison.Ordinal);
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Equal(EventsWaitErrors.UnsupportedMatch, ex.ErrorCode);
            Assert.Equal(EventsWaitErrors.UnsupportedMatchMessage, ex.Message);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Live_reload_config_matches_fixture_status_key()
    {
        var cp = NewPlane();
        try
        {
            var live = await DispatchAsync(cp, ProtocolMethods.ServerReloadConfig, new JsonObject());
            var fixture = JsonSerializer.Deserialize(
                FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(ProtocolMethods.ServerReloadConfig)),
                ProtocolJsonContext.Default.RpcResponse);
            Assert.NotNull(fixture);
            using var fixtureDoc = JsonDocument.Parse(fixture.Result!.Value.GetRawText());
            foreach (var prop in fixtureDoc.RootElement.EnumerateObject())
                Assert.True(live.TryGetProperty(prop.Name, out _), prop.Name);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static ControlPlaneService NewPlane()
    {
        var state = new AppState(SessionId.New("ps-" + Guid.NewGuid().ToString("N")[..8]));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        return new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
    }

    private static async Task<string> CreatePaneAsync(ControlPlaneService cp)
    {
        var created = await DispatchAsync(cp, ProtocolMethods.WorkspaceCreate, new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["command"] = "/bin/true",
            ["create_pane"] = true,
        });
        return created.GetProperty("pane").GetProperty("pane_id").GetString()!;
    }

    private static async Task<JsonElement> DispatchAsync(
        ControlPlaneService cp, string method, JsonObject body)
    {
        using var doc = JsonDocument.Parse(body.ToJsonString());
        return await cp.DispatchAsync(method, doc.RootElement, CancellationToken.None);
    }
}
