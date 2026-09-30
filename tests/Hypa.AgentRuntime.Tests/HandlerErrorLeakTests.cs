using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class HandlerErrorLeakTests
{




    [SkippableFact]
    public async Task Unexpected_handler_throw_returns_internal_error_without_canary_on_wire()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        const string canary = "H23-HANDLER-CANARY secret-token /tmp/id_rsa leaked-ex.Message";
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine("/tmp", "h23-handler-leak-" + id);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        try
        {
            var service = new ThrowingControlPlane(canary);
            await using var server = new UnixSocketServer(service, sock);
            await server.StartAsync(CancellationToken.None);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            await writer.WriteLineAsync(
                """{"id":"1","method":"ping","params":{}}""");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await reader.ReadLineAsync(cts.Token);
            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.Contains("-32603", line, StringComparison.Ordinal);
            Assert.Contains("handler failed", line, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, line, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-token", line, StringComparison.Ordinal);
            Assert.DoesNotContain("/tmp/id_rsa", line, StringComparison.Ordinal);
            Assert.DoesNotContain("-32000", line, StringComparison.Ordinal);
            Assert.DoesNotContain("ex.Message", line, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperationException", line, StringComparison.Ordinal);

            using var doc = JsonDocument.Parse(line);
            var error = doc.RootElement.GetProperty("error");
            var code = error.GetProperty("code").GetInt32();
            Assert.Equal(ProtocolErrorCodes.InternalError, code);
            Assert.NotEqual(ProtocolErrorCodes.ServerShuttingDown, code);
            Assert.Equal(-32603, code);
            Assert.Equal(ProtocolErrors.InternalError, ProtocolErrors.SymbolOf(code));
            Assert.Equal(
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.InternalError),
                error.GetProperty("message").GetString());
            Assert.Equal("handler failed", error.GetProperty("message").GetString());
            if (error.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("retryable", out var retryable))
            {
                Assert.False(retryable.GetBoolean());
            }

            await using var typed = new ControlPlaneClient(sock);
            await typed.ConnectAsync();
            var typedEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                typed.CallAsync(ProtocolMethods.Ping));
            Assert.Equal(ProtocolErrorCodes.InternalError, typedEx.Code);
            Assert.NotEqual(ProtocolErrorCodes.ServerShuttingDown, typedEx.Code);
            Assert.Equal(
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.InternalError),
                typedEx.Message);
            Assert.DoesNotContain(canary, typedEx.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch
            {
                // teardown
            }
        }
    }

    [SkippableFact]
    public async Task Unexpected_handler_throw_on_notification_writes_no_rpc_response()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        const string canary = "H23-HANDLER-CANARY secret-token /tmp/id_rsa leaked-ex.Message";
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine("/tmp", "notify-handler-leak-" + id);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        try
        {
            var service = new ThrowingControlPlane(canary);
            await using var server = new UnixSocketServer(service, sock);
            await server.StartAsync(CancellationToken.None);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            await writer.WriteLineAsync(
                """{"method":"pane.send_keys","params":{"pane_id":"p1","lease_id":"l1","encoding":"base64","data":"eA=="}}""");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            string? line = null;
            try
            {
                line = await reader.ReadLineAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                line = null;
            }

            Assert.True(string.IsNullOrWhiteSpace(line), "notification must not receive an RpcResponse");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch
            {
                // teardown
            }
        }
    }

    [SkippableFact]
    public async Task Subscribe_replay_failure_does_not_leak_exception_text_on_wire()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        const string canary = "H23-REPLAY-CANARY /secret/.env Access to the path is denied";
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine("/tmp", "h23-leak-" + id);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        try
        {
            var service = new ReplayFailingControlPlane(canary);
            await using var server = new UnixSocketServer(service, sock);
            await server.StartAsync(CancellationToken.None);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            await writer.WriteLineAsync(
                """{"id":"1","method":"events.subscribe","params":{"from_seq":0,"live":true}}""");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await reader.ReadLineAsync(cts.Token);
            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.DoesNotContain(canary, line, StringComparison.Ordinal);
            Assert.DoesNotContain("/secret/.env", line, StringComparison.Ordinal);

            using var doc = JsonDocument.Parse(line);
            var error = doc.RootElement.GetProperty("error");
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, error.GetProperty("code").GetInt32());
            Assert.Equal(
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.PersistenceUnavailable),
                error.GetProperty("message").GetString());

            await using var typed = new ControlPlaneClient(sock);
            await typed.ConnectAsync();
            var typedEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                typed.CallAsync(ProtocolMethods.EventsSubscribe));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, typedEx.Code);
            Assert.Equal(
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.PersistenceUnavailable),
                typedEx.Message);
            Assert.DoesNotContain(canary, typedEx.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch
            {
                // teardown
            }
        }
    }


    private sealed class ReplayFailingControlPlane : IControlPlaneService
    {
        private readonly string _canary;

        public ReplayFailingControlPlane(string canary) => _canary = canary;

        public Task<JsonElement> DispatchAsync(
            string method,
            JsonElement? parameters,
            IClientConnection? connection,
            CancellationToken ct)
        {
            using var doc = JsonDocument.Parse("""{"subscription_id":"sub_leak"}""");
            return Task.FromResult(doc.RootElement.Clone());
        }

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> PrepareEventsSubscribeAsync(
            string subscriptionId,
            CancellationToken ct) =>
            Task.FromResult(
                RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Fail(
                    RuntimePersistenceError.Io(_canary)));

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

    private sealed class ThrowingControlPlane : IControlPlaneService
    {
        private readonly string _canary;

        public ThrowingControlPlane(string canary) => _canary = canary;

        public Task<JsonElement> DispatchAsync(
            string method,
            JsonElement? parameters,
            IClientConnection? connection,
            CancellationToken ct) =>
            throw new InvalidOperationException(_canary);

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> PrepareEventsSubscribeAsync(
            string subscriptionId,
            CancellationToken ct) =>
            Task.FromResult(
                RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Ok(
                    Array.Empty<RuntimeEventRecord>()));

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
}
