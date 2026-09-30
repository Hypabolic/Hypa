using Hypa.AgentRuntime.Infrastructure.Plugins;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Stages bundled plugin roots. Annotate copies <c>hypa-annotate</c>
/// beside product hypa. Slots is a Hypa-owned chrome fixture.
/// </summary>
public sealed class BundledPluginService : IBundledPluginService
{
    private readonly IPluginFiles _files;
    private readonly IPluginPathRoots _paths;

    public BundledPluginService(IPluginFiles files, IPluginPathRoots paths)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public static BundledPluginService CreateSystem(string? configRoot = null) =>
        new(new SystemPluginFiles(), new SystemPluginPathRoots(configRoot));

    public PluginResult<BundledPluginStageResult> StageAnnotate(string executablePath)
    {
        var binary = NormalizeExecutable(executablePath);
        if (!binary.IsOk)
            return PluginResult<BundledPluginStageResult>.Fail(binary.Error);

        var annotateSource = BundledPluginLayout.FindAnnotateProgramBeside(binary.Value, _files);
        if (annotateSource is null)
        {
            return PluginResult<BundledPluginStageResult>.Fail(
                PluginError.InvalidCommand,
                "bundled plugin requires hypa-annotate beside hypa");
        }

        var root = BundledPluginLayout.AnnotateRoot(_paths);
        var manifestPath = BundledPluginLayout.AnnotateManifestPath(_paths);
        var stagedProgram = BundledPluginLayout.AnnotateProgramStagePath(root);
        var quotedPlaceholder = "\"" + BundledPluginLayout.BinaryPlaceholder + "\"";
        var template = OfficialPluginAssets.AnnotateManifest;
        if (!template.Contains(BundledPluginLayout.AnnotateProgramRelativeCommand, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "embedded annotate manifest is missing " + BundledPluginLayout.AnnotateProgramRelativeCommand);
        }

        var content = template.Contains(quotedPlaceholder, StringComparison.Ordinal)
            ? template.Replace(quotedPlaceholder, TomlString(binary.Value), StringComparison.Ordinal)
            : template;
        try
        {
            _files.CreateDirectory(root);
            _files.CreateDirectory(Path.Combine(root, BundledPluginLayout.AnnotateBinDirectoryName));
            _files.CopyFile(annotateSource, stagedProgram);
            _files.WriteAllText(manifestPath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginResult<BundledPluginStageResult>.Fail(PluginError.UserDirFailed, ex.Message);
        }

        return PluginResult<BundledPluginStageResult>.Ok(new BundledPluginStageResult
        {
            PluginId = BundledPluginLayout.AnnotatePluginId,
            PluginRoot = _files.GetFullPath(root),
            ManifestPath = _files.GetFullPath(manifestPath),
            Command0 = _files.GetFullPath(stagedProgram),
        });
    }

    public PluginResult<BundledPluginRemoveResult> RemoveAnnotate() =>
        Remove(BundledPluginLayout.AnnotatePluginId, BundledPluginLayout.AnnotateRoot(_paths));

    public BundledPluginDiskStatus InspectAnnotate() =>
        Inspect(BundledPluginLayout.AnnotatePluginId, BundledPluginLayout.AnnotateRoot(_paths), BundledPluginLayout.AnnotateManifestPath(_paths));

    public PluginResult<BundledPluginStageResult> StageSlots()
    {
        var root = BundledPluginLayout.SlotsRoot(_paths);
        var manifestPath = BundledPluginLayout.SlotsManifestPath(_paths);
        var content = OfficialPluginAssets.SlotsManifest;
        try
        {
            _files.CreateDirectory(root);
            _files.WriteAllText(manifestPath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginResult<BundledPluginStageResult>.Fail(PluginError.UserDirFailed, ex.Message);
        }

        return PluginResult<BundledPluginStageResult>.Ok(new BundledPluginStageResult
        {
            PluginId = BundledPluginLayout.SlotsPluginId,
            PluginRoot = _files.GetFullPath(root),
            ManifestPath = _files.GetFullPath(manifestPath),
            Command0 = "/bin/echo",
        });
    }

    public PluginResult<BundledPluginRemoveResult> RemoveSlots() =>
        Remove(BundledPluginLayout.SlotsPluginId, BundledPluginLayout.SlotsRoot(_paths));

    public BundledPluginDiskStatus InspectSlots() =>
        Inspect(BundledPluginLayout.SlotsPluginId, BundledPluginLayout.SlotsRoot(_paths), BundledPluginLayout.SlotsManifestPath(_paths));

    private PluginResult<BundledPluginRemoveResult> Remove(string pluginId, string root)
    {
        try
        {
            var removed = _files.DeleteDirectory(root);
            return PluginResult<BundledPluginRemoveResult>.Ok(new BundledPluginRemoveResult
            {
                PluginId = pluginId,
                PluginRoot = _files.GetFullPath(root),
                Removed = removed,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginResult<BundledPluginRemoveResult>.Fail(PluginError.UserDirFailed, ex.Message);
        }
    }

    private BundledPluginDiskStatus Inspect(string pluginId, string root, string manifestPath) =>
        new()
        {
            PluginId = pluginId,
            PluginRoot = _files.GetFullPath(root),
            ManifestPath = _files.GetFullPath(manifestPath),
            Staged = _files.FileExists(manifestPath),
        };

    private static PluginResult<string> NormalizeExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
        {
            return PluginResult<string>.Fail(
                PluginError.InvalidCommand,
                "bundled plugin command[0] must be an absolute hypa path");
        }

        try
        {
            return PluginResult<string>.Ok(Path.GetFullPath(executablePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PluginResult<string>.Fail(PluginError.InvalidCommand, ex.Message);
        }
    }

    private static string TomlString(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
    }
}
