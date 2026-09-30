using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>Looks up first-party and test occupant manifests by id.</summary>
public interface IOccupantManifestRegistry
{
    bool TryGet(string id, out OccupantManifest? manifest);
}

/// <summary>Resolves isolated cube HOME for occupant env and transcript roots.</summary>
public static class CubeHomePaths
{
    public const string EnvName = "HYPA_CUBE_HOME";
    public const string DirectoryName = "cube-home";

    /// <summary>
    /// Prefer <c>HYPA_CUBE_HOME</c>. Else use <c>{stateDirectory}/cube-home</c>.
    /// Never default to the laptop user profile.
    /// </summary>
    public static string Resolve(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var env = Environment.GetEnvironmentVariable(EnvName);
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env.Trim());

        return Path.Combine(Path.GetFullPath(stateDirectory), DirectoryName);
    }
}
