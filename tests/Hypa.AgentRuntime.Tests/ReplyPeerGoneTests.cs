using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A client that closes the connection while a slow handler runs. The handler
/// succeeds, and the reply write then fails. The server logs that as peer gone,
/// with the elapsed time, and not as a handler fault.
/// </summary>
public class ReplyPeerGoneTests
{
    [SkippableFact]
    public async Task A_client_that_leaves_before_a_slow_reply_gives_a_peer_gone_record_with_elapsed_ms()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var dir = Path.Combine("/tmp", "peer-gone-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        try
        {
            var service = new GatedControlPlane();
            var logs = new CapturingProcessLogSink(ProcessLogLevel.Debug);
            var logger = new CapturingLogger();
            await using var server = new UnixSocketServer(
                service,
                sock,
                UnixSocketServerOptions.Default,
                logger,
                processLog: logs,
                sessionId: "s_peer_gone");
            await server.StartAsync(CancellationToken.None);

            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await client.SendAsync(
                Encoding.UTF8.GetBytes("""{"id":"slow-1","method":"runtime.lease.claim","params":{}}""" + "\n"),
                SocketFlags.None);

            var connection = await service.Entered.Task.WaitAsync(Poll.HangGuard);
            var held = Stopwatch.StartNew();
            client.Shutdown(SocketShutdown.Both);
            client.Dispose();
            // The input lane ends when the read loop of the server reads the
            // end of the stream. The handler is still held then.
            var lanes = ((ClientConnection)connection!).RequestLanes!;
            await Poll.GuardAsync(lanes.InputCompletion, "the server to read the end of the stream");
            var heldMs = (long)held.Elapsed.TotalMilliseconds;
            service.Release.TrySetResult();

            var record = await Poll.UntilAsync(
                () => logs.Records.FirstOrDefault(r => r.Outcome == ProcessLogEvents.OutcomePeerGone),
                "the peer gone record");

            Assert.Equal(ProcessLogEvents.ApiRequestComplete, record.Event);
            Assert.Equal("runtime.lease.claim", record.Method);
            Assert.Equal("slow-1", record.RequestId);
            Assert.NotNull(record.ElapsedMs);
            Assert.True(
                record.ElapsedMs >= heldMs,
                $"elapsed {record.ElapsedMs} ms is below the {heldMs} ms the handler was held");
            Assert.DoesNotContain(logs.Records, r => r.Event == ProcessLogEvents.ApiRequestFail);
            Assert.DoesNotContain(
                logger.Entries,
                e => e.Message.Contains("Handler failed", StringComparison.Ordinal));
            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Warning
                    && e.Message.Contains("Peer gone", StringComparison.Ordinal)
                    && e.Message.Contains("runtime.lease.claim", StringComparison.Ordinal));
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

    private sealed class GatedControlPlane : IControlPlaneService
    {
        public TaskCompletionSource<IClientConnection?> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonElement> DispatchAsync(
            string method,
            JsonElement? parameters,
            IClientConnection? connection,
            CancellationToken ct)
        {
            Entered.TrySetResult(connection);
            await Release.Task.WaitAsync(ct);
            using var doc = JsonDocument.Parse("""{"outcome":"granted","lease_id":"l_1"}""");
            return doc.RootElement.Clone();
        }

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

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
