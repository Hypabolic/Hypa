using System.Buffers;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Pooled <see cref="IBufferWriter{T}"/> over <see cref="ArrayPool{T}.Shared"/>.
/// One instance is one owner. Dispose returns the array. No static mutable state.
/// </summary>
public sealed class PooledUtf8Buffer : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    public PooledUtf8Buffer(int initialSize = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialSize, 256));
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public int WrittenCount => _written;

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_written + count > _buffer.Length)
            throw new InvalidOperationException("Advanced past the rented buffer.");
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var buffer = _buffer;
        var written = _written;
        _buffer = [];
        _written = 0;
        if (written > 0)
            Array.Clear(buffer, 0, written);
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 0)
            sizeHint = 0;
        var remaining = _buffer.Length - _written;
        if (sizeHint == 0)
            sizeHint = 1;
        if (remaining >= sizeHint)
            return;

        var needed = _written + sizeHint;
        var newSize = _buffer.Length <= 0 ? 256 : _buffer.Length;
        while (newSize < needed)
            newSize *= 2;

        var next = ArrayPool<byte>.Shared.Rent(newSize);
        if (_written > 0)
            Buffer.BlockCopy(_buffer, 0, next, 0, _written);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}
