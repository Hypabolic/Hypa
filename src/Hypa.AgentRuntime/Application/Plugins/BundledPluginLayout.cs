namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Staged first-class plugin roots under the user config directory.
/// </summary>
public static class BundledPluginLayout
{
    public const string AnnotatePluginId = "annotate";
    public const string SlotsPluginId = "slots";
    public const string CopyContextActionId = "copy-context";
    public const string CopyArchiveActionId = "copy-archive";
    public const string CaptureActionId = "capture";
    public const string ManageActionId = "manage";
    public const string LastActionId = "last";
    public const string EditorPaneId = "editor";
    public const string ManagerPaneId = "manager";
    public const string LastReviewPaneId = "last-review";
    public const string BinaryPlaceholder = "__HYPA_BIN__";
    public const string BundledDirectoryName = "bundled";
    public const string AnnotateBinDirectoryName = "bin";
    public const string AnnotateProgramFileName = "hypa-annotate.exe";
    public const string AnnotateProductFileName = "hypa-annotate";
    public const string AnnotateProgramRelativeCommand = "./bin/hypa-annotate.exe";

    public static string BundledRoot(IPluginPathRoots paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.ConfigRoot, "plugins", BundledDirectoryName);
    }

    public static string AnnotateRoot(IPluginPathRoots paths) =>
        Path.Combine(BundledRoot(paths), AnnotatePluginId);

    public static string AnnotateManifestPath(IPluginPathRoots paths) =>
        Path.Combine(AnnotateRoot(paths), PluginHostService.ManifestFileName);

    public static string AnnotateProgramStagePath(string pluginRoot) =>
        Path.Combine(pluginRoot, AnnotateBinDirectoryName, AnnotateProgramFileName);

    /// <summary>
    /// Hypa copies the product sibling into that relative name.
    /// </summary>
    public static string? FindAnnotateProgramBeside(string hypaPath, IPluginFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (string.IsNullOrWhiteSpace(hypaPath))
            return null;

        string dir;
        try
        {
            dir = Path.GetDirectoryName(files.GetFullPath(hypaPath)) ?? "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (dir.Length == 0)
            return null;

        foreach (var name in new[] { AnnotateProductFileName, AnnotateProgramFileName })
        {
            var candidate = files.GetFullPath(Path.Combine(dir, name));
            if (files.FileExists(candidate))
                return candidate;
        }

        return null;
    }

    public static string SlotsRoot(IPluginPathRoots paths) =>
        Path.Combine(BundledRoot(paths), SlotsPluginId);

    public static string SlotsManifestPath(IPluginPathRoots paths) =>
        Path.Combine(SlotsRoot(paths), PluginHostService.ManifestFileName);
}
