namespace Hypa.Cli.Attach.Input;

/// <summary>
/// One stdin wait. Count is byte length. Idle is a poll timeout.
/// EOF is a closed reader.
/// </summary>
internal readonly record struct AttachStdinRead(int Count, bool Idle, bool Eof)
{
    public static AttachStdinRead Bytes(int count) => new(count, Idle: false, Eof: false);

    public static AttachStdinRead Timeout { get; } = new(0, Idle: true, Eof: false);

    public static AttachStdinRead End { get; } = new(0, Idle: false, Eof: true);
}
