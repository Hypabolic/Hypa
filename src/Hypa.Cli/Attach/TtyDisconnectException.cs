namespace Hypa.Cli.Attach;

internal sealed class TtyDisconnectException : IOException
{
    public TtyDisconnectException()
        : base("TTY disconnected.")
    {
    }
}
