namespace Hypa.Terminal.Pty;

public interface IPtyProcess : IAsyncDisposable
{
    int Pid { get; }
    bool IsRunning { get; }
    int? ExitCode { get; }

    Stream StandardInput { get; }
    Stream StandardOutput { get; }

    void Resize(int cols, int rows);
    Task WaitForExitAsync(CancellationToken ct);
}

/// <summary>
/// Optional PTY input seam that reports bytes accepted by the underlying
/// transport. Production PTY streams may implement this when the transport can
/// expose partial-write progress; ordinary Stream writes are all-or-fail.
/// </summary>
public interface IPtyInputProgress
{
    ValueTask<int> WriteAcceptedAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}
