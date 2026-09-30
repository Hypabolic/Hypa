namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>Environment port for official integration paths and PATH lookup.</summary>
public interface IIntegrationEnvironment
{
    string? GetVariable(string name);

    string? UserHome { get; }

    string? PathVariable { get; }

    bool FileIsExecutable(string path);
}
