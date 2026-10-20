namespace Hypa.Cli.Attach.Input;

/// <summary>
/// One stdin wait. Count is byte length. Idle is a poll timeout.
/// EOF is a closed reader. ReadTimestamp is the monotonic time at which the
/// stdin thread read the bytes. Zero when the source does not know it.
/// </summary>
internal readonly record struct AttachStdinRead(int Count, bool Idle, bool Eof, long ReadTimestamp = 0)
{
    public static AttachStdinRead Bytes(int count, long readTimestamp = 0) =>
        new(count, Idle: false, Eof: false, readTimestamp);

    public static AttachStdinRead Timeout { get; } = new(0, Idle: true, Eof: false);

    public static AttachStdinRead End { get; } = new(0, Idle: false, Eof: true);
}
