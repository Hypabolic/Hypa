namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>File-system port for plugin manifests, registry, and user dirs.</summary>
public interface IPluginFiles
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    void CopyFile(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var destDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);
        File.Copy(source, destination, overwrite: true);
    }

    void CreateDirectory(string path);

    bool DeleteFile(string path);

    bool DeleteDirectory(string path);

    string GetFullPath(string path);
}

/// <summary>Clock port for grant expiry and command log timestamps.</summary>
public interface IPluginClock
{
    DateTimeOffset UtcNow { get; }
}

// / <summary>User-global Hypa plugin path roots.
public interface IPluginPathRoots
{
    string ConfigRoot { get; }

    string RegistryPath { get; }

    string RegistryLockPath { get; }

    string PluginConfigDir(string pluginId);

    string PluginStateDir(string pluginId);
}

/// <summary>
// / Out-of-process argv launcher.
/// <c>start_plugin_command</c> (lines 16-181): spawn, do not wait on the mux.
/// </summary>
public interface IPluginProcessLauncher
{
    bool TryStart(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        int outputCapBytes,
        Action<PluginProcessExit> onExit,
        out string? error);
}

public sealed record PluginProcessExit(
    int? ExitCode,
    string Stdout,
    string Stderr,
    string? Error,
    int? Pid = null);

/// <summary>Wait for a plugin doctor argv. The host does not invoke a shell.</summary>
public interface IPluginDoctorRunner
{
    PluginDoctorRunResult Run(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout);
}

public sealed record PluginDoctorRunResult(
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut);

/// <summary>Persistent user-global plugin registry.</summary>
public interface IPluginRegistry
{
    PluginResult<IReadOnlyList<InstalledPlugin>> Load();

    PluginResult<IReadOnlyList<InstalledPlugin>> Update(
        Func<List<InstalledPlugin>, PluginResult<IReadOnlyList<InstalledPlugin>>> mutation);
}

/// <summary>
/// Application port. Infrastructure parses with the internal TOML subset.
/// </summary>
public interface IPluginManifestParser
{
    PluginResult<PluginManifest> Parse(string content);
}

/// <summary>Optional snapshot for redacted invocation context.</summary>
public interface IPluginContextSource
{
    PluginInvocationContext Current(string correlationId);

    PluginInvocationContext ForEvent(string hookName, string eventJson, string correlationId);
}

/// <summary>Host-owned panes, reports, and popup cleanup.</summary>
public interface IPluginSessionEffects
{
    Task CloseOwnedPanesAsync(IReadOnlyList<string> paneIds, CancellationToken ct);

    Task CloseOwnedPopupAsync(string pluginId, CancellationToken ct);

    void ClearPluginReports(string pluginId);
}

/// <summary>Plugin host use cases.</summary>
public interface IPluginHost
{
    PluginResult<PluginLinkResult> Link(string path, bool enabled);

    PluginResult<IReadOnlyList<InstalledPlugin>> List(string? pluginId);

    PluginResult<PluginUnlinkResult> Unlink(string pluginId);

    PluginResult<PluginEnableResult> SetEnabled(string pluginId, bool enabled);

    PluginResult<IReadOnlyList<PluginActionInfo>> ListActions(string? pluginId);

    PluginResult<PluginActionInvokeResult> InvokeAction(
        string actionId,
        string? pluginId,
        PluginInvocationContext? context);

    /// <summary>
    // True when a matching
    /// enabled handler started its action. False when no handler matched.
    /// </summary>
    PluginResult<bool> ActivateLink(string url, PluginInvocationContext? context);

    PluginResult<IReadOnlyList<PluginCommandLog>> ListLogs(string? pluginId, int? limit);

    PluginResult<PluginPaneOpenPlan> PlanPaneOpen(
        string pluginId,
        string entrypoint,
        string? placement,
        PluginInvocationContext context,
        IReadOnlyDictionary<string, string>? extraEnv);

    void NoteOwnedPane(string pluginId, string entrypoint, string paneId);

    void ForgetOwnedPane(string paneId);

    IReadOnlyList<string> OwnedPaneIds(string pluginId);

    void NoteDeliveryTarget(string pluginId, string paneId);

    string? ResolveSendTextPluginId(string paneId);

    bool CanDeliverText(string pluginId, string paneId);

    void RecordPaneTextDelivery(string pluginId, string paneId);

    void NoteOwnedPopup(string pluginId);

    void ForgetOwnedPopup(string pluginId);

    bool OwnsPopup(string pluginId);

    void RunStartupHooks();

    void HandleRuntimeEvent(string eventType, string payloadJson);

    PluginGrantDecision CheckGrant(
        string? token,
        string method,
        string? source,
        string? targetPluginId,
        bool pluginConnection = false);

    string? ResolveActionOwnerPluginId(string actionId, string? pluginId);

    bool IsLivePluginProcess(int pid);

    string? PeekGrantToken(string pluginId);

    PluginTrustPreview TrustPreview(InstalledPlugin plugin);

    PluginResult<PluginCommandLog> RunDeclaredRefresh(string pluginId, string localId);

    IReadOnlyList<PluginManifestResource> DeclaredResources(string pluginId);

    PluginResult<PluginSettingsSnapshot> GetSettings(string pluginId);

    PluginResult<PluginSettingsWriteOutcome> SetSettings(string pluginId, string key, string value);

    bool HasAction(string pluginId, string actionId);

    string CurrentVersion { get; }
}
