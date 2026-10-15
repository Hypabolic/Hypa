namespace Hypa.AgentRuntime.Application.Attach.Status;

/// <summary>One status line that an owner set. <see cref="Sequence"/> orders the entries by time.</summary>
public sealed record StatusEntry(
    StatusOwner Owner,
    StatusSeverity Severity,
    string Text,
    long Sequence);
