using System.Text;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>One bounded UTF-8 join line. Terminal bytes use framed multiplex after join.</summary>
public static class BoundedJoinLine
{
    public const int MaxBytes = 1_048_576;

    public static async Task<ConnectivityOutcome<string>> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[MaxBytes];
        var n = 0;
        while (n < MaxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(n, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (n == 0)
                {
                    return ConnectivityOutcome<string>.Failure(
                        ConnectivityReasons.PeerUnavailable,
                        "peer closed",
                        stage: RelayFailureStageWire.Join,
                        retryable: true);
                }

                return ConnectivityOutcome<string>.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "join line is incomplete");
            }

            if (buffer[n] == (byte)'\n')
            {
                var len = n;
                if (len > 0 && buffer[len - 1] == (byte)'\r')
                    len--;
                return ConnectivityOutcome<string>.Success(Encoding.UTF8.GetString(buffer, 0, len));
            }

            n++;
        }

        return ConnectivityOutcome<string>.Failure(
            ConnectivityReasons.BootstrapInvalid,
            "join line exceeds bound");
    }

    public static async Task WriteAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(line);
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length > MaxBytes)
            throw new InvalidOperationException("join line exceeds bound");

        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new ReadOnlyMemory<byte>([(byte)'\n']), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
