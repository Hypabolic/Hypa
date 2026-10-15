using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class AcceptJoinRejectionReplyTests
{
    [Fact]
    public async Task A_failing_rejection_log_does_not_stop_the_reply_to_the_peer()
    {
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new LocalUnixBridge(
            Path.Combine(Path.GetTempPath(), "hypa-unused-" + Guid.NewGuid().ToString("N") + ".sock"),
            new AcceptPathJoin(coordinator));
        var handler = new AcceptJoinStreamHandler(
            bridge,
            coordinator,
            _ => throw new InvalidOperationException("no mux bootstrap expected"),
            trustGate: null,
            TimeProvider.System,
            onJoinRejected: _ => throw new IOException("stderr is closed"));
        var stream = new DuplexStream("this is not a join\n");

        var session = await handler.AdmitClientAsync(stream, CancellationToken.None, "10.0.0.7:1");

        Assert.Null(session);
        var reply = stream.Written;
        Assert.Contains("\"ok\":false", reply, StringComparison.Ordinal);
        Assert.Contains(ConnectivityReasons.BootstrapInvalid, reply, StringComparison.Ordinal);
    }

    private sealed class DuplexStream(string input) : Stream
    {
        private readonly MemoryStream _in = new(Encoding.UTF8.GetBytes(input));
        private readonly MemoryStream _out = new();

        public string Written => Encoding.UTF8.GetString(_out.ToArray());

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
    }
}
