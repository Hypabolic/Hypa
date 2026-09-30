namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Optional git probe hooks for checkpoint tests. Production uses <see cref="NoOpGitProbeHooks"/>.
/// </summary>
public interface IGitProbeHooks
{
    void BeforeProcessStart(string workingDirectory);
}

/// <summary>Production no-op.</summary>
public sealed class NoOpGitProbeHooks : IGitProbeHooks
{
    public static readonly NoOpGitProbeHooks Instance = new();

    public void BeforeProcessStart(string workingDirectory)
    {
    }
}
