namespace Hypa.AgentRuntime.Application;

/// <summary>Environment port for attach-config path resolve and nested-attach guard.</summary>
public interface IAttachConfigEnvironment
{
    string? GetVariable(string name);

    string UserHome { get; }

    string? AppData { get; }

    bool IsWindows { get; }

    bool IsMacOs { get; }
}
