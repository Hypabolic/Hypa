using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Cli.Attach;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Render credit over a real Unix socket: the attach hello turns it on, and
/// the server reads <c>render.ack</c> before its serial dispatch queue.
/// </summary>
public sealed class RenderCreditSocketTests
{
    [SkippableFact]
    public async Task A_render_ack_applies_while_a_slow_request_holds_the_dispatch_queue()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix sockets only.");
        var (dir, sock) = NewPrivateSocketPath("render-credit");
        var state = new AppState(SessionId.New("rc-" + Guid.NewGuid().ToString("N")[..8]));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready, Placement = "local" });
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        UnixSocketServer? server = null;
        ControlPlaneClient? client = null;
        try
        {
            var created = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                Params(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "/bin/true",
                    ["create_pane"] = true,
                }),
                CancellationToken.None);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString()!;

            server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None).WaitAsync(Poll.HangGuard);
            client = new ControlPlaneClient(sock, connectTimeout: Poll.HangGuard);
            await client.ConnectAsync().WaitAsync(Poll.HangGuard);

            var hello = AttachEndpointPreflight.BuildHello(
                "credit-client",
                new AttachGeometry
                {
                    Columns = 80,
                    Rows = 24,
                    CellWidthPx = 8,
                    CellHeightPx = 16,
                    GeometryRevision = 1,
                },
                renderCredit: true);
            var (welcome, _) = await new AttachEndpointRpcClient(client).HelloAsync(hello).WaitAsync(Poll.HangGuard);
            Assert.Contains(AttachEndpointProtocol.RenderCreditCapability, welcome.Capabilities, StringComparer.Ordinal);
            Assert.True(client.RenderCreditEnabled);
            var connectionId = Assert.Single(cp.RenderCreditConnectionIds);

            // events.wait holds the serial dispatch of this connection.
            var wait = client.CallAsync(
                ProtocolMethods.EventsWait,
                new JsonObject
                {
                    ["match_event"] = new JsonObject
                    {
                        ["event"] = EventsWaitMatch.PaneAgentStatusChanged,
                        ["pane_id"] = paneId,
                        ["agent_status"] = "blocked",
                    },
                    ["timeout_ms"] = 120_000,
                },
                CancellationToken.None,
                Poll.HangGuard);
            var queued = client.CallAsync(ProtocolMethods.Ping, null, CancellationToken.None, Poll.HangGuard);

            cp.ChargeRenderCreditForTests(connectionId, paneId, 3, 4096);
            Assert.Equal(4096, cp.RenderCreditStateFor(connectionId)?.UnackedBytes);
            client.NoteRenderPainted([new RenderAck(paneId, 3)]);
            await Poll.UntilAsync(
                () => cp.RenderCreditStateFor(connectionId)?.UnackedBytes == 0,
                "the ack gives the credit back");
            Assert.False(wait.IsCompleted);
            Assert.False(queued.IsCompleted);
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
            if (server is not null)
                await server.DisposeAsync();
            await cp.ShutdownAsync(CancellationToken.None);
            TryDeleteTree(dir);
        }

        Assert.Empty(cp.RenderCreditConnectionIds);
    }

    private static JsonElement Params(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private static (string Dir, string Sock) NewPrivateSocketPath(string prefix)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine("/tmp", prefix + "-" + id);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return (dir, Path.Combine(dir, "s.sock"));
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
