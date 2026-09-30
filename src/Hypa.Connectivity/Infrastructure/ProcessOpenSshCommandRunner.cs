using System.Diagnostics;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// The only type that may spawn <c>ssh</c>.
/// </summary>
public sealed class ProcessOpenSshCommandRunner : IOpenSshCommandRunner
{
    public async Task<OpenSshCaptureResult> CaptureAsync(
        OpenSshRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var process = Start(request, redirectStdin: false, redirectStdout: true, redirectStderr: true);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new OpenSshCaptureResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdout.ConfigureAwait(false),
            StandardError = await stderr.ConfigureAwait(false),
        };
    }

    public Task<OpenSshStdioSession> StartStdioAsync(
        OpenSshRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var process = Start(request, redirectStdin: true, redirectStdout: true, redirectStderr: true);
        var stream = new ProcessStdioStream(process);
        return Task.FromResult(new OpenSshStdioSession
        {
            Stream = stream,
            Lifetime = stream,
        });
    }

    internal static List<string> BuildArguments(OpenSshRunRequest request)
    {
        var args = new List<string>();
        if (request.BatchMode)
        {
            args.Add("-o");
            args.Add("BatchMode=yes");
            args.Add("-o");
            args.Add("StrictHostKeyChecking=yes");
            args.Add("-o");
            args.Add("ConnectTimeout=10");
            args.Add("-o");
            args.Add("ConnectionAttempts=1");
        }

        if (!string.IsNullOrWhiteSpace(request.ConfigPath))
        {
            args.Add("-F");
            args.Add(request.ConfigPath);
        }

        if (!string.IsNullOrWhiteSpace(request.ControlPath))
        {
            args.Add("-S");
            args.Add(request.ControlPath);
            args.Add("-o");
            args.Add("ControlMaster=auto");
            args.Add("-o");
            args.Add("ControlPersist=yes");
        }

        args.Add("-T");
        args.Add(request.Target);
        args.Add(request.RemoteCommand);
        return args;
    }

    private static Process Start(
        OpenSshRunRequest request,
        bool redirectStdin,
        bool redirectStdout,
        bool redirectStderr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = redirectStderr,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArguments(request))
            psi.ArgumentList.Add(arg);

        var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start OpenSSH.");
        return process;
    }
}

/// <summary>Duplex stdin/stdout over a child process.</summary>
internal sealed class ProcessStdioStream : Stream, IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private int _disposed;

    public ProcessStdioStream(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _stdin.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _stdin.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        _stdout.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _stdout.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stdout.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        _stdin.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _stdin.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stdin.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (disposing)
            Shutdown();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Shutdown();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void Shutdown()
    {
        try { _stdin.Dispose(); } catch { /* ignore */ }
        try { _stdout.Dispose(); } catch { /* ignore */ }
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }
        try { _process.Dispose(); } catch { /* ignore */ }
    }
}

internal static class OpenSshFailure
{
    public static ConnectivityOutcome<BytePathHandle> Transport(string detail) =>
        ConnectivityOutcome<BytePathHandle>.Failure(
            ConnectivityReasons.PeerUnavailable,
            detail);
}
