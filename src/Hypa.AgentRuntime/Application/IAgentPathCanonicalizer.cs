namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Canonical absolute path for an executable token.
/// Resolves every symlink, including a parent directory.
/// Returns null when the path is absent.
/// </summary>
public interface IAgentPathCanonicalizer
{
    string? TryCanonicalize(string path);
}
