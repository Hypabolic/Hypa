namespace Hypa.Cli.Attach.Input;

internal readonly record struct AttachIoLoops(
    Task Input,
    Task Renew,
    Task Beat,
    Task TabBar);
