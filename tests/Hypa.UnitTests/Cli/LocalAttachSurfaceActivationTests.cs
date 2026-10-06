using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class LocalAttachSurfaceActivationTests
{
    [Fact]
    public void Local_attach_geometry_uses_host_size()
    {
        var live = Live();
        live.Host.Resize(120, 40);
        var geometry = AttachSession.BuildLocalAttachGeometry(live, tty: null);
        Assert.Equal(120, geometry.Columns);
        Assert.Equal(40, geometry.Rows);
        Assert.Equal(8u, geometry.CellWidthPx);
        Assert.Equal(16u, geometry.CellHeightPx);
        Assert.Equal(1UL, geometry.GeometryRevision);
    }

    [Fact]
    public void Local_attach_geometry_defaults_when_host_is_empty()
    {
        var live = Live();
        var geometry = AttachSession.BuildLocalAttachGeometry(live, tty: null);
        Assert.Equal(80, geometry.Columns);
        Assert.Equal(24, geometry.Rows);
    }

    [Fact]
    public void Local_attach_hello_marks_surface_active()
    {
        var geometry = AttachSession.BuildLocalAttachGeometry(Live(), tty: null);
        var hello = AttachEndpointPreflight.BuildHello("cli", geometry, surfaceActive: true);
        Assert.True(hello.SurfaceActive);
        Assert.Equal("cli", hello.ClientId);
    }

    [Fact]
    public void Endpoint_client_id_is_per_attach_not_the_mux_connection_id()
    {
        // Each mux numbers its connections, so two machines' attaches are
        // both conn_2. A cube peer must not hello with the host attach's id.
        var host = Live();
        var peer = Live();
        host.AttachClientId = "conn_2";
        peer.AttachClientId = "conn_2";

        Assert.StartsWith("cli_", host.EndpointClientId, StringComparison.Ordinal);
        Assert.NotEqual(host.EndpointClientId, peer.EndpointClientId);
        Assert.NotEqual(host.AttachClientId, host.EndpointClientId);

        var before = host.EndpointClientId;
        host.AttachClientId = "conn_7"; // a reconnect gets a new connection id
        Assert.Equal(before, host.EndpointClientId);
    }

    [Fact]
    public async Task Local_attach_activate_requires_client_id()
    {
        var live = Live();
        await using var client = new ControlPlaneClient("/tmp/hypa-local-surface-unused.sock");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AttachSession.ActivateLocalAttachSurfaceAsync(client, live, tty: null, CancellationToken.None));
        Assert.Contains("attach client id", ex.Message, StringComparison.Ordinal);
    }

    private static AttachLiveState Live()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new SilentPort(), "w1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            InputLease = "lease-in",
            ResizeLease = "lease-r",
            ChromeEnabled = true,
        };
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    }
}
