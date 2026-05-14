using System.Text.Json;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks;

public sealed class HookIoAdapter
{
    private const int MaxStdinBytes = 1024 * 1024;

    public async Task<JsonElement?> ReadStdinAsync(CancellationToken ct = default)
    {
        try
        {
            using var limited = new LimitedStream(Console.OpenStandardInput(), MaxStdinBytes);
            using var doc = await JsonDocument.ParseAsync(limited, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"hypa hook: failed to read stdin: {ex.Message}");
            return null;
        }
    }

    public static void WriteOutput(AgentHookOutput output)
    {
        if (output.JsonBody is not null)
            Console.WriteLine(output.JsonBody);
    }

    private sealed class LimitedStream(Stream inner, int maxBytes) : Stream
    {
        private int _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = maxBytes - _read;
            if (remaining <= 0) return 0;
            var toRead = Math.Min(count, remaining);
            var n = inner.Read(buffer, offset, toRead);
            _read += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var remaining = maxBytes - _read;
            if (remaining <= 0) return 0;
            var toRead = Math.Min(count, remaining);
            var n = await inner.ReadAsync(buffer.AsMemory(offset, toRead), ct);
            _read += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
