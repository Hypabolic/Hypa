namespace Hypa.Cli.Attach.Input;

/// <summary>
// / Owned stdin producer.
/// <c>src/client/input.rs:33-37</c> reads on a dedicated thread.
/// The attach loop consumes bytes on the existing serialized path.
/// </summary>
internal interface IAttachStdinSource : IAsyncDisposable
{
    ValueTask<AttachStdinRead> ReadAsync(byte[] buffer, int timeoutMs, CancellationToken ct);

    bool ThreadAlive { get; }
}
