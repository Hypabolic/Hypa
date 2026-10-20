using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Envelopes;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The input lane of one connection: a <c>pane.send_keys</c> notification
/// does not wait behind a slow request that does not fence it, and the key
/// order contract holds for keys, resizes, focus changes and occupant swaps.
/// </summary>
public sealed class SendKeysInputLaneTests
{
    private const double AdmitLimitMs = 20;

    [SkippableFact]
    public async Task Send_keys_behind_a_slow_rpc_on_the_same_connection_is_admitted_promptly()
    {
        SkipUnlessUnixSockets();
        var service = new ScriptedService();
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long slowDoneAt = 0;
        service.On(ProtocolMethods.SessionSnapshot, async (_, _) =>
        {
            slowEntered.TrySetResult();
            await releaseSlow.Task;
            Interlocked.Exchange(ref slowDoneAt, Stopwatch.GetTimestamp());
            return Empty();
        });
        var admitted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.On(ProtocolMethods.PaneSendKeys, (_, _) =>
        {
            admitted.TrySetResult(Stopwatch.GetTimestamp());
            return Task.FromResult(Empty());
        });

        using var capture = AttachPathTrace.BeginCapture();
        await using var harness = await SocketHarness.StartAsync(service);
        harness.OpenOnDispose.Add(releaseSlow);
        await harness.SendAsync(Request("slow", ProtocolMethods.SessionSnapshot, new JsonObject()));
        await Poll.GuardAsync(slowEntered.Task, "the slow request to start");

        var sentAt = Stopwatch.GetTimestamp();
        await harness.SendAsync(Key("p1", 1));
        var admittedAt = await admitted.Task.WaitAsync(Poll.HangGuard);

        var admitMs = Stopwatch.GetElapsedTime(sentAt, admittedAt).TotalMilliseconds;
        Assert.True(
            admitMs < AdmitLimitMs,
            $"the key was admitted {admitMs:F1} ms after the send, behind a held request");

        // The slow request is still held, so the key did not wait for it.
        Assert.Equal(0, Interlocked.Read(ref slowDoneAt));
        releaseSlow.TrySetResult();

        var queued = capture.Input.Where(r => r.Stage == AttachPathTrace.StageDispatchQueue).ToArray();
        var hop = Assert.Single(queued);
        Assert.True(
            hop.ElapsedUs < AdmitLimitMs * 1000,
            $"dispatch-queue was {hop.ElapsedUs} us");
    }

    [SkippableFact]
    public async Task A_key_waits_for_an_earlier_resize_of_its_pane_and_passes_a_resize_of_another_pane()
    {
        SkipUnlessUnixSockets();
        var service = new ScriptedService();
        var resizeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resizeApplied = 0;
        service.On(ProtocolMethods.PaneResize, async (_, _) =>
        {
            resizeEntered.TrySetResult();
            await releaseResize.Task;
            Volatile.Write(ref resizeApplied, 1);
            return Empty();
        });
        var keyForOther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyForResized = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.On(ProtocolMethods.PaneSendKeys, (p, _) =>
        {
            if (PaneOf(p) == "p2")
                keyForOther.TrySetResult();
            else
                keyForResized.TrySetResult(Volatile.Read(ref resizeApplied));
            return Task.FromResult(Empty());
        });

        await using var harness = await SocketHarness.StartAsync(service);
        harness.OpenOnDispose.Add(releaseResize);
        await harness.SendAsync(Request("r1", ProtocolMethods.PaneResize, new JsonObject
        {
            ["pane_id"] = "p1",
            ["cols"] = 100,
            ["rows"] = 30,
        }));
        await Poll.GuardAsync(resizeEntered.Task, "the resize to start");
        await harness.SendAsync(Key("p2", 1));
        await harness.SendAsync(Key("p1", 1));

        // The key for the other pane goes while the resize is held. The key for the
        // resized pane stays queued until the resize has applied. The value of
        // its task is the proof: the resize sets the flag only after the release.
        await Poll.GuardAsync(keyForOther.Task, "the key for the other pane");
        Assert.False(keyForResized.Task.IsCompleted);

        releaseResize.TrySetResult();
        Assert.Equal(1, await keyForResized.Task.WaitAsync(Poll.HangGuard));
    }

    [SkippableFact]
    public async Task A_focus_change_waits_for_an_earlier_key_and_a_read_does_not()
    {
        SkipUnlessUnixSockets();
        var service = new ScriptedService();
        var keyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseKey = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyDone = 0;
        service.On(ProtocolMethods.PaneSendKeys, async (_, _) =>
        {
            keyEntered.TrySetResult();
            await releaseKey.Task;
            Volatile.Write(ref keyDone, 1);
            return Empty();
        });
        var readDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.On(ProtocolMethods.SessionSnapshot, (_, _) =>
        {
            readDone.TrySetResult();
            return Task.FromResult(Empty());
        });
        var focusSawKey = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.On(ProtocolMethods.PaneFocus, (_, _) =>
        {
            focusSawKey.TrySetResult(Volatile.Read(ref keyDone));
            return Task.FromResult(Empty());
        });

        await using var harness = await SocketHarness.StartAsync(service);
        harness.OpenOnDispose.Add(releaseKey);
        await harness.SendAsync(Key("p1", 1));
        await Poll.GuardAsync(keyEntered.Task, "the key to start");
        await harness.SendAsync(Request("s1", ProtocolMethods.SessionSnapshot, new JsonObject()));
        await harness.SendAsync(Request("f1", ProtocolMethods.PaneFocus, new JsonObject { ["pane_id"] = "p2" }));

        // The read is not a fence, so it runs while the key is held. The focus
        // change is a fence, so it waits until the key is done. The value of
        // its task is the proof: the key sets the flag only after the release.
        await Poll.GuardAsync(readDone.Task, "the read");
        Assert.False(focusSawKey.Task.IsCompleted);

        releaseKey.TrySetResult();
        Assert.Equal(1, await focusSawKey.Task.WaitAsync(Poll.HangGuard));
    }

    [Fact]
    public async Task A_key_waits_only_until_the_fence_request_applies_its_change()
    {
        var releaseReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectionRequestLanes? lanes = null;
        lanes = new ConnectionRequestLanes(
            async (_, _, _) =>
            {
                // The handler applied the resize. The reply write is still held.
                lanes!.MarkActiveRequestApplied();
                await releaseReply.Task;
                return true;
            },
            (_, _, _) =>
            {
                keyRan.TrySetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await lanes.PostAsync(
            RpcRequestOf("r1", ProtocolMethods.PaneResize, new JsonObject { ["pane_id"] = "p1" }),
            Stopwatch.GetTimestamp(),
            CancellationToken.None);
        await lanes.PostAsync(KeyRequest("p1", 1), Stopwatch.GetTimestamp(), CancellationToken.None);

        await Poll.GuardAsync(keyRan.Task, "the key after the applied resize");
        releaseReply.TrySetResult();
        lanes.Complete();
        await lanes.DispatchCompletion.WaitAsync(Poll.HangGuard);
        await lanes.InputCompletion.WaitAsync(Poll.HangGuard);
    }

    [Fact]
    public async Task Keys_resize_and_focus_keep_the_contract_order_under_random_interleaving()
    {
        const int Interleavings = 10_000;
        for (var seed = 0; seed < Interleavings; seed++)
        {
            var rng = new Random(seed);
            var script = ContractScript.Generate(rng, rng.Next(6, 17));
            var model = new ContractModel();
            ConnectionRequestLanes? lanes = null;
            lanes = new ConnectionRequestLanes(
                async (request, _, _) =>
                {
                    await model.RunAsync(request.Method, request.Params, () => lanes!.MarkActiveRequestApplied());
                    return true;
                },
                (request, _, _) => model.RunAsync(request.Method, request.Params, markApplied: null),
                CancellationToken.None);

            foreach (var request in script.Requests)
                await lanes.PostAsync(request, Stopwatch.GetTimestamp(), CancellationToken.None);
            lanes.Complete();
            await lanes.DispatchCompletion.WaitAsync(Poll.HangGuard);
            await lanes.InputCompletion.WaitAsync(Poll.HangGuard);

            var violations = model.Violations;
            Assert.True(
                violations.Count == 0,
                $"seed {seed}: {string.Join("; ", violations)}\n{script.Describe()}");
            Assert.True(
                model.Handled == script.Requests.Count,
                $"seed {seed}: handled {model.Handled} of {script.Requests.Count}");
        }
    }

    [SkippableFact]
    public async Task Keys_resize_and_focus_keep_the_contract_order_on_one_socket_connection()
    {
        SkipUnlessUnixSockets();
        const int Operations = 10_000;
        var rng = new Random(396);
        var script = ContractScript.Generate(rng, Operations);
        var model = new ContractModel();
        var service = new ScriptedService();
        foreach (var method in ContractScript.Methods)
        {
            service.On(method, async (p, connection) =>
            {
                var client = (ClientConnection)connection!;
                await model.RunAsync(
                    method,
                    p,
                    method == ProtocolMethods.PaneSendKeys ? null : client.MarkRequestApplied);
                return Empty();
            });
        }

        await using var harness = await SocketHarness.StartAsync(service);
        var batch = new StringBuilder();
        foreach (var request in script.Requests)
        {
            batch.Append(JsonSerializer.Serialize(
                request,
                Hypa.AgentRuntime.Protocol.Json.ProtocolJsonContext.Default.RpcRequest));
            batch.Append('\n');
            if (batch.Length > 32 * 1024)
            {
                await harness.SendRawAsync(batch.ToString());
                batch.Clear();
            }
        }

        await harness.SendRawAsync(batch.ToString());
        await Poll.UntilAsync(() => model.Handled >= script.Requests.Count, "every request of the script");

        Assert.True(
            model.Violations.Count == 0,
            string.Join("; ", model.Violations.Take(20)));
        Assert.Equal(script.Requests.Count, model.Handled);
    }

    private static void SkipUnlessUnixSockets() =>
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

    private static string? PaneOf(JsonElement? p) => SendKeysOrderRules.TryReadPaneId(p);

    private static JsonElement Empty()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static JsonObject Request(string id, string method, JsonObject p) =>
        new() { ["id"] = id, ["method"] = method, ["params"] = p };

    private static JsonObject Key(string paneId, int n) =>
        new()
        {
            ["method"] = ProtocolMethods.PaneSendKeys,
            ["params"] = new JsonObject
            {
                ["pane_id"] = paneId,
                ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("k" + n)),
            },
        };

    private static RpcRequest RpcRequestOf(string? id, string method, JsonObject p)
    {
        using var doc = JsonDocument.Parse(p.ToJsonString());
        return new RpcRequest { Id = id, Method = method, Params = doc.RootElement.Clone() };
    }

    private static RpcRequest KeyRequest(string paneId, int n) =>
        RpcRequestOf(null, ProtocolMethods.PaneSendKeys, new JsonObject
        {
            ["pane_id"] = paneId,
            ["n"] = n,
        });

    /// <summary>
    /// A random request stream of one client. Each key names the state that the
    /// client expects at the mux when the key arrives: the focused pane, the
    /// last resize of its pane and the occupant of its pane.
    /// </summary>
    private sealed class ContractScript
    {
        public static readonly string[] Methods =
        [
            ProtocolMethods.PaneSendKeys,
            ProtocolMethods.PaneResize,
            ProtocolMethods.PaneFocus,
            ProtocolMethods.AgentStart,
            ProtocolMethods.SessionSnapshot,
        ];

        private static readonly string[] Panes = ["p1", "p2", "p3"];

        public List<RpcRequest> Requests { get; } = [];

        public static ContractScript Generate(Random rng, int count)
        {
            var script = new ContractScript();
            var focused = "p1";
            var size = Panes.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
            var occupant = Panes.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
            var keyN = Panes.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
            var nextId = 0;
            var slowReads = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var roll = rng.Next(100);
                var pane = Panes[rng.Next(Panes.Length)];
                var yields = rng.Next(4);
                if (roll < 50)
                {
                    keyN[focused]++;
                    script.Requests.Add(RpcRequestOf(null, ProtocolMethods.PaneSendKeys, new JsonObject
                    {
                        ["pane_id"] = focused,
                        ["n"] = keyN[focused],
                        ["expect_size"] = size[focused],
                        ["expect_occupant"] = occupant[focused],
                        ["yields"] = yields,
                    }));
                }
                else if (roll < 70)
                {
                    size[pane]++;
                    script.Requests.Add(RpcRequestOf("r" + nextId++, ProtocolMethods.PaneResize, new JsonObject
                    {
                        ["pane_id"] = pane,
                        ["version"] = size[pane],
                        ["yields"] = yields,
                    }));
                }
                else if (roll < 82)
                {
                    focused = pane;
                    script.Requests.Add(RpcRequestOf("f" + nextId++, ProtocolMethods.PaneFocus, new JsonObject
                    {
                        ["pane_id"] = pane,
                        ["yields"] = yields,
                    }));
                }
                else if (roll < 88)
                {
                    occupant[pane]++;
                    script.Requests.Add(RpcRequestOf("a" + nextId++, ProtocolMethods.AgentStart, new JsonObject
                    {
                        ["pane_id"] = pane,
                        ["occupant"] = occupant[pane],
                        ["yields"] = yields,
                    }));
                }
                else
                {
                    if (rng.Next(20) == 0)
                        slowReads.Add(script.Requests.Count);
                    script.Requests.Add(RpcRequestOf("s" + nextId++, ProtocolMethods.SessionSnapshot, new JsonObject
                    {
                        ["yields"] = yields + 2,
                    }));
                }
            }

            // A slow read holds the dispatch lane until each key right after it
            // is admitted. A key does not wait for a read, and its fences come
            // before the read, so the keys pass the read.
            foreach (var index in slowReads)
            {
                string? lastKey = null;
                for (var next = index + 1;
                     next < script.Requests.Count && script.Requests[next].Method == ProtocolMethods.PaneSendKeys;
                     next++)
                {
                    lastKey = KeyName(script.Requests[next].Params!.Value);
                }

                if (lastKey is null)
                    continue;
                var read = script.Requests[index];
                var readParams = JsonNode.Parse(read.Params!.Value.GetRawText())!.AsObject();
                readParams["wait_key"] = lastKey;
                script.Requests[index] = RpcRequestOf(read.Id, read.Method, readParams);
            }

            return script;
        }

        public static string KeyName(JsonElement p) =>
            p.GetProperty("pane_id").GetString() + "#" + p.GetProperty("n").GetInt32();

        public string Describe() =>
            string.Join(
                "\n",
                Requests.Select(r => r.Method + " " + (r.Params?.GetRawText() ?? "")));
    }

    /// <summary>
    /// The mux state that the contract protects, with a check at each key
    /// admission. A resize, a focus change and an occupant swap apply their
    /// change between random yields, as a real handler does around its gate.
    /// </summary>
    private sealed class ContractModel
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _size = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _occupant = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _lastKey = new(StringComparer.Ordinal);
        private readonly List<string> _violations = [];
        private readonly Dictionary<string, TaskCompletionSource> _admitted = new(StringComparer.Ordinal);
        private string _focused = "p1";
        private int _handled;

        public int Handled => Volatile.Read(ref _handled);

        public IReadOnlyList<string> Violations
        {
            get { lock (_gate) return [.. _violations]; }
        }

        public async Task RunAsync(string method, JsonElement? parameters, Action? markApplied)
        {
            var p = parameters!.Value;
            var yields = p.TryGetProperty("yields", out var y) ? y.GetInt32() : 0;
            var pane = p.TryGetProperty("pane_id", out var paneEl) ? paneEl.GetString()! : "";
            var before = yields / 2;
            for (var i = 0; i < before; i++)
                await Task.Yield();

            lock (_gate)
            {
                switch (method)
                {
                    case ProtocolMethods.PaneSendKeys:
                        CheckKeyUnderGate(pane, p);
                        break;
                    case ProtocolMethods.PaneResize:
                        _size[pane] = p.GetProperty("version").GetInt32();
                        break;
                    case ProtocolMethods.PaneFocus:
                        _focused = pane;
                        break;
                    case ProtocolMethods.AgentStart:
                        _occupant[pane] = p.GetProperty("occupant").GetInt32();
                        break;
                }
            }

            // The change is applied. The rest of the handler (a snapshot, a reply) can still run.
            if (method == ProtocolMethods.PaneResize)
                markApplied?.Invoke();

            for (var i = before; i < yields; i++)
                await Task.Yield();
            if (p.TryGetProperty("wait_key", out var waitKey))
                await Admitted(waitKey.GetString()!);

            Interlocked.Increment(ref _handled);
        }

        /// <summary>Completes when the model admits the key. The caller holds no gate.</summary>
        private Task Admitted(string key)
        {
            lock (_gate)
                return AdmittedUnderGate(key).Task;
        }

        private TaskCompletionSource AdmittedUnderGate(string key)
        {
            if (!_admitted.TryGetValue(key, out var admitted))
            {
                admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _admitted[key] = admitted;
            }

            return admitted;
        }

        private void CheckKeyUnderGate(string pane, JsonElement p)
        {
            AdmittedUnderGate(ContractScript.KeyName(p)).TrySetResult();
            var n = p.GetProperty("n").GetInt32();
            if (!string.Equals(_focused, pane, StringComparison.Ordinal))
                _violations.Add($"key {pane}#{n} admitted while {_focused} has the focus");

            var expectSize = p.GetProperty("expect_size").GetInt32();
            var size = _size.GetValueOrDefault(pane);
            if (size != expectSize)
                _violations.Add($"key {pane}#{n} saw size {size}, expected {expectSize}");

            var expectOccupant = p.GetProperty("expect_occupant").GetInt32();
            var occupant = _occupant.GetValueOrDefault(pane);
            if (occupant != expectOccupant)
                _violations.Add($"key {pane}#{n} saw occupant {occupant}, expected {expectOccupant}");

            var last = _lastKey.GetValueOrDefault(pane);
            if (n != last + 1)
                _violations.Add($"key {pane}#{n} admitted after {pane}#{last}");
            _lastKey[pane] = n;
        }
    }

    /// <summary>A service double that runs one delegate for each method.</summary>
    private sealed class ScriptedService : IControlPlaneService
    {
        private readonly Dictionary<string, Func<JsonElement?, IClientConnection?, Task<JsonElement>>> _handlers =
            new(StringComparer.Ordinal);

        public void On(string method, Func<JsonElement?, IClientConnection?, Task<JsonElement>> handler) =>
            _handlers[method] = handler;

        public Task<JsonElement> DispatchAsync(
            string method,
            JsonElement? parameters,
            IClientConnection? connection,
            CancellationToken ct) =>
            _handlers.TryGetValue(method, out var handler)
                ? handler(parameters, connection)
                : Task.FromResult(Empty());

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> PrepareEventsSubscribeAsync(
            string subscriptionId,
            CancellationToken ct) =>
            Task.FromResult(
                RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Ok(Array.Empty<RuntimeEventRecord>()));

        public Task CompleteEventsSubscribeAsync(
            string subscriptionId,
            IReadOnlyList<RuntimeEventRecord> replayRecords,
            IClientConnection connection,
            CancellationToken ct) =>
            Task.CompletedTask;

        public void OnClientDisconnected(IClientConnection connection)
        {
        }

        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// One server and one raw client connection. A background loop reads and
    /// drops each reply line, so the server writer never blocks.
    /// </summary>
    private sealed class SocketHarness : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly UnixSocketServer _server;
        private readonly Socket _client;
        private readonly NetworkStream _stream;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _drain;

        private SocketHarness(string dir, UnixSocketServer server, Socket client)
        {
            _dir = dir;
            _server = server;
            _client = client;
            _stream = new NetworkStream(client, ownsSocket: false);
            _drain = DrainAsync();
        }

        public static async Task<SocketHarness> StartAsync(IControlPlaneService service)
        {
            var dir = Path.Combine("/tmp", "keylane-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var sock = Path.Combine(dir, "s.sock");
            var server = new UnixSocketServer(service, sock);
            await server.StartAsync(CancellationToken.None);
            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            return new SocketHarness(dir, server, client);
        }

        /// <summary>Gates that dispose opens first, so a failed test does not hold a handler.</summary>
        public List<TaskCompletionSource> OpenOnDispose { get; } = [];

        public Task SendAsync(JsonObject line) => SendRawAsync(line.ToJsonString() + "\n");

        public async Task SendRawAsync(string text)
        {
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(text));
            await _stream.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var gate in OpenOnDispose)
                gate.TrySetResult();
            await _cts.CancelAsync();
            try { _client.Shutdown(SocketShutdown.Both); }
            catch (SocketException) { /* already closed */ }
            _client.Dispose();
            try { await _drain; }
            catch (Exception) { /* teardown */ }
            await _server.DisposeAsync();
            _stream.Dispose();
            _cts.Dispose();
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* teardown */ }
        }

        private async Task DrainAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (await _stream.ReadAsync(buffer, _cts.Token) > 0)
                {
                }
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
