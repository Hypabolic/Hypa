using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach sends <c>render.ack</c> only when its hello offered render
/// credit and the welcome of the mux listed it. A fake mux answers the hello.
/// </summary>
public sealed class RenderCreditClientTests
{
    [SkippableFact]
    public async Task A_mux_that_lists_render_credit_gets_one_ack_with_the_newest_generation_of_each_pane()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var mux = FakeMux.Listen(listsRenderCredit: true);
        await using var client = await mux.ConnectAsync();
        await new AttachEndpointRpcClient(client).HelloAsync(Hello(offerCredit: true));
        Assert.True(client.RenderCreditEnabled);
        var hello = Assert.Single(mux.Hellos);
        Assert.Contains(
            AttachEndpointProtocol.RenderCreditCapability,
            hello.GetProperty("optional_capabilities").EnumerateArray().Select(c => c.GetString()));

        var live = Live("pane_b");
        AttachSession.AckPaintedCells(client, live, [Cells("pane_a", 3), Cells("pane_a", 5), Cells("pane_b", 2)]);

        var ack = (await mux.WaitForAcksAsync(1))[0];
        // The ack names no focused pane. The mux takes the focus from input.
        Assert.False(ack.TryGetProperty("focused_pane_id", out _));
        var acks = ack.GetProperty("acks").EnumerateArray()
            .ToDictionary(a => a.GetProperty("pane_id").GetString()!, a => a.GetProperty("generation").GetInt64());
        Assert.Equal(2, acks.Count);
        Assert.Equal(5, acks["pane_a"]);
        Assert.Equal(2, acks["pane_b"]);
    }

    [SkippableFact]
    public async Task A_mux_without_render_credit_gets_no_ack_and_no_new_hello_field_breaks_it()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var mux = FakeMux.Listen(listsRenderCredit: false);
        await using var client = await mux.ConnectAsync();
        var (welcome, _) = await new AttachEndpointRpcClient(client).HelloAsync(Hello(offerCredit: true));
        Assert.Null(welcome.Error);
        Assert.False(client.RenderCreditEnabled);

        var live = Live("pane_a");
        AttachSession.AckPaintedCells(client, live, [Cells("pane_a", 1)]);
        AttachSession.AckConsumedRender(client, CellsElement("pane_a", 2));
        // A later line on the same writer: when it arrives, an ack sent
        // before it would have arrived first.
        await client.NotifyAsync(ProtocolMethods.Ping);
        await mux.WaitForMethodAsync(ProtocolMethods.Ping);
        Assert.Empty(mux.Acks);
        Assert.Equal(0, client.RenderAcksSent);
    }

    [SkippableFact]
    public async Task A_hello_that_does_not_offer_render_credit_sends_no_ack_to_a_new_mux()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var mux = FakeMux.Listen(listsRenderCredit: true);
        await using var client = await mux.ConnectAsync();
        await new AttachEndpointRpcClient(client).HelloAsync(Hello(offerCredit: false));
        Assert.False(client.RenderCreditEnabled);
        // The hello offers other optional names (echo mode), but not render credit.
        var hello = Assert.Single(mux.Hellos);
        var offered = hello.TryGetProperty("optional_capabilities", out var optional)
            ? optional.EnumerateArray().Select(name => name.GetString()).ToList()
            : [];
        Assert.DoesNotContain(AttachEndpointProtocol.RenderCreditCapability, offered);

        AttachSession.AckPaintedCells(client, Live("pane_a"), [Cells("pane_a", 1)]);
        await client.NotifyAsync(ProtocolMethods.Ping);
        await mux.WaitForMethodAsync(ProtocolMethods.Ping);
        Assert.Empty(mux.Acks);
    }

    [SkippableFact]
    public async Task Each_snapshot_slice_that_the_loop_takes_is_acked_with_its_generation()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var mux = FakeMux.Listen(listsRenderCredit: true);
        await using var client = await mux.ConnectAsync();
        await new AttachEndpointRpcClient(client).HelloAsync(Hello(offerCredit: true));

        var slice = JsonSerializer.SerializeToElement(
            new TerminalRenderSnapshotPayload
            {
                PaneId = "pane_a",
                Kind = TerminalRenderSnapshotPayload.KindSnapshot,
                Generation = 9,
                RowStart = 0,
                RowEnd = 1,
                Complete = false,
                GridCols = 2,
                GridRows = 2,
                Snapshot = JsonDocument.Parse("{}").RootElement,
            },
            ProtocolJsonContext.Default.TerminalRenderSnapshotPayload);
        AttachSession.AckConsumedRender(client, slice);

        var ack = (await mux.WaitForAcksAsync(1))[0];
        var entry = Assert.Single(ack.GetProperty("acks").EnumerateArray());
        Assert.Equal("pane_a", entry.GetProperty("pane_id").GetString());
        Assert.Equal(9, entry.GetProperty("generation").GetInt64());
        // The ack covers the slices up to this one, not the whole snapshot.
        Assert.Equal(1, entry.GetProperty("row_end").GetInt32());
    }

    [SkippableFact]
    public async Task Acks_noted_while_a_write_runs_go_in_one_later_ack()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var mux = FakeMux.Listen(listsRenderCredit: true);
        await using var client = await mux.ConnectAsync();
        await new AttachEndpointRpcClient(client).HelloAsync(Hello(offerCredit: true));
        var live = Live("pane_a");
        for (var generation = 1; generation <= 50; generation++)
            AttachSession.AckPaintedCells(client, live, [Cells("pane_a", generation)]);

        await Poll.UntilAsync(
            () => mux.Acks.Any(a => a.GetProperty("acks")[0].GetProperty("generation").GetInt64() == 50),
            "the ack of generation 50");
        var acks = mux.Acks;
        Assert.InRange(acks.Count, 1, 50);
        var generations = acks.Select(a => a.GetProperty("acks")[0].GetProperty("generation").GetInt64()).ToList();
        Assert.Equal(generations.OrderBy(g => g), generations);
    }

    private static AttachEndpointHello Hello(bool offerCredit) =>
        AttachEndpointPreflight.BuildHello(
            "client_1",
            new AttachGeometry
            {
                Columns = 80,
                Rows = 24,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            surfaceActive: true,
            renderCredit: offerCredit);

    private static TerminalRenderCellsPayload Cells(string paneId, long generation) =>
        new()
        {
            PaneId = paneId,
            Kind = TerminalRenderCellsPayload.KindCells,
            Generation = generation,
        };

    private static JsonElement CellsElement(string paneId, long generation) =>
        JsonSerializer.SerializeToElement(Cells(paneId, generation), ProtocolJsonContext.Default.TerminalRenderCellsPayload);

    private static AttachLiveState Live(string focusedPane)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            PaneId = focusedPane,
        };
    }

    /// <summary>
    /// Answers <c>hypa.attach.hello.v1</c> with a welcome and records each
    /// line that it reads.
    /// </summary>
    private sealed class FakeMux : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly bool _listsRenderCredit;
        private readonly object _gate = new();
        private readonly List<JsonElement> _lines = [];
        private NetworkStream? _stream;
        private Task? _reader;

        private FakeMux(Socket listener, string path, bool listsRenderCredit)
        {
            _listener = listener;
            Path = path;
            _listsRenderCredit = listsRenderCredit;
        }

        private string Path { get; }

        public static FakeMux Listen(bool listsRenderCredit)
        {
            var path = "/tmp/hypa-credit-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                return new FakeMux(listener, path, listsRenderCredit);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        public List<JsonElement> Hellos => Params(ProtocolMethods.AttachHello);

        public List<JsonElement> Acks => Params(ProtocolMethods.RenderAck);

        public async Task<ControlPlaneClient> ConnectAsync()
        {
            var client = new ControlPlaneClient(
                Path, connectTimeout: TestWait.HangGuard, callTimeout: TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            await client.ConnectAsync();
            _stream = new NetworkStream(await accept, ownsSocket: true);
            _reader = ReadAsync();
            return client;
        }

        public async Task<List<JsonElement>> WaitForAcksAsync(int count)
        {
            await Poll.UntilAsync(() => Acks.Count >= count, $"{count} render.ack");
            return Acks;
        }

        public Task WaitForMethodAsync(string method) =>
            Poll.UntilAsync(() => Lines().Any(l => l.GetProperty("method").GetString() == method), method);

        public async ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            _listener.Dispose();
            if (_reader is not null)
            {
                try
                {
                    await _reader.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception)
                {
                }
            }

            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }

        private List<JsonElement> Lines()
        {
            lock (_gate)
                return [.. _lines];
        }

        private List<JsonElement> Params(string method) =>
            Lines()
                .Where(l => l.GetProperty("method").GetString() == method)
                .Select(l => l.GetProperty("params"))
                .ToList();

        private async Task ReadAsync()
        {
            var stream = _stream ?? throw new InvalidOperationException("mux is not connected");
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement.Clone();
                    lock (_gate)
                        _lines.Add(root);
                    if (root.GetProperty("method").GetString() == ProtocolMethods.AttachHello
                        && root.TryGetProperty("id", out var id))
                    {
                        await WriteWelcomeAsync(stream, id.GetString()!);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task WriteWelcomeAsync(Stream stream, string id)
        {
            string[] capabilities = _listsRenderCredit
                ? [.. AttachEndpointProtocol.RequiredCapabilities, AttachEndpointProtocol.RenderCreditCapability]
                : [.. AttachEndpointProtocol.RequiredCapabilities];
            var welcome = new AttachEndpointWelcome
            {
                EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
                ProtocolMajor = (uint)ProtocolVersion.Major,
                ProtocolMinor = (uint)ProtocolVersion.Minor,
                BootId = "boot_1",
                MuxIdentity = "fake",
                SnapshotCodec = AttachEndpointProtocol.SnapshotCodec,
                SurfaceCodec = AttachEndpointProtocol.SurfaceCodec,
                InputCodec = AttachEndpointProtocol.InputCodec,
                Methods = [.. AttachEndpointProtocol.Methods],
                Capabilities = capabilities,
                ConnectionGeneration = 1,
            };
            var reply = new JsonObject
            {
                ["id"] = id,
                ["result"] = JsonSerializer.SerializeToNode(welcome, ProtocolJsonContext.Default.AttachEndpointWelcome),
            };
            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }
}
