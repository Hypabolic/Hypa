namespace Hypa.Cli.Attach;

internal static class TtyWriteFailure
{
    internal const int Eio = 5;
    internal const int Epipe = 32;

    internal static Exception Create(nint n, int errno)
    {
        if (n < 0)
        {
            if (errno is Eio or Epipe)
                return new TtyDisconnectException();

            return new InvalidOperationException("write to TTY failed.");
        }

        return new InvalidOperationException("write to TTY returned 0.");
    }
}
