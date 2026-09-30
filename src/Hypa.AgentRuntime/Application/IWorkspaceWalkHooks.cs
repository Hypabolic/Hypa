namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Optional walk hooks for checkpoint export tests. Production uses <see cref="NoOpWorkspaceWalkHooks"/>.
/// </summary>
public interface IWorkspaceWalkHooks
{
    bool ThrowOnList { get; }

    void AfterWalkRootLstat(string path);

    void AfterPinClassify(string path);
}

/// <summary>Production no-op. Host composition injects this (or null, treated as no-op).</summary>
public sealed class NoOpWorkspaceWalkHooks : IWorkspaceWalkHooks
{
    public static readonly NoOpWorkspaceWalkHooks Instance = new();

    public bool ThrowOnList => false;

    public void AfterWalkRootLstat(string path)
    {
    }

    public void AfterPinClassify(string path)
    {
    }
}
