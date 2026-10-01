using Hypa.AgentRuntime.Infrastructure.Logging;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Resolves the agent-runtime state root and <c>runtime.db</c> path.
/// Separate from compression <c>~/.hypa/hypa.db</c>.
/// </summary>
public sealed record RuntimeStatePaths
{
    public const string DatabaseFileName = "runtime.db";
    public const string StateDirEnv = "HYPA_RUNTIME_STATE_DIR";
    public const string SocketEnv = "HYPA_RUNTIME_SOCKET";

    public const string JournalDirectoryName = "journal";
    public const string CheckpointsDirectoryName = "checkpoints";
    public const string HistoryFileName = "session-history.json";

    public required string StateDirectory { get; init; }
    public string DatabasePath => Path.Combine(StateDirectory, DatabaseFileName);

    /// <summary>Directory for HYJR segment files under the state root.</summary>
    public string JournalDirectory => Path.Combine(StateDirectory, JournalDirectoryName);

    /// <summary>Directory for G1 checkpoint trees under the state root.</summary>
    public string CheckpointsDirectory => Path.Combine(StateDirectory, CheckpointsDirectoryName);

    public void EnsureJournalDirectory() => Directory.CreateDirectory(JournalDirectory);

    public void EnsureCheckpointsDirectory() => Directory.CreateDirectory(CheckpointsDirectory);


    /// <summary>
    /// Resolve state directory from explicit override, <c>HYPA_RUNTIME_STATE_DIR</c>,
    /// directory of <c>HYPA_RUNTIME_SOCKET</c> / socket path, or default
    /// <c>~/.config/hypa/runtime/&lt;session&gt;/</c>.
    /// </summary>
    public static RuntimeStatePaths Resolve(string sessionName, string? socketPath = null, string? stateDirOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(stateDirOverride))
        {
            return new RuntimeStatePaths
            {
                StateDirectory = Path.GetFullPath(stateDirOverride),
            };
        }

        var envState = Environment.GetEnvironmentVariable(StateDirEnv);
        if (!string.IsNullOrWhiteSpace(envState))
        {
            return new RuntimeStatePaths
            {
                StateDirectory = Path.GetFullPath(envState),
            };
        }

        var resolvedSocket = socketPath;
        if (string.IsNullOrWhiteSpace(resolvedSocket))
        {
            var envSocket = Environment.GetEnvironmentVariable(SocketEnv);
            if (!string.IsNullOrWhiteSpace(envSocket))
                resolvedSocket = Path.GetFullPath(envSocket);
        }

        if (!string.IsNullOrWhiteSpace(resolvedSocket))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(resolvedSocket));
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return new RuntimeStatePaths { StateDirectory = dir };
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Path.GetTempPath();

        var safeName = string.IsNullOrWhiteSpace(sessionName) ? "default" : sessionName.Trim();
        return new RuntimeStatePaths
        {
            StateDirectory = Path.Combine(home, ".config", "hypa", "runtime", safeName),
        };
    }

    /// <summary>
    /// Create the state root owner-only (0700) on Unix. It is also the default
    /// socket parent, and the socket guard refuses a group-writable parent
    /// such as a 0775 directory made under umask 002. An existing directory
    /// keeps its mode.
    /// </summary>
    public void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(StateDirectory);
        else
            Directory.CreateDirectory(StateDirectory, UnixLogPathSecurity.OwnerDirectoryMode);
    }
}
