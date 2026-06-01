namespace Hypa.Runtime.Domain.Hooks;

public abstract record UninstallOperation
{
    public sealed record RemoveJsonHook(
        string FilePath,
        string HookEventName,
        string HookJson
    ) : UninstallOperation;

    public sealed record RemoveTomlKey(
        string FilePath,
        string Section,
        string Key
    ) : UninstallOperation;

    public sealed record RemoveCodexHooksFeatureIfUnused(
        string ConfigFilePath,
        string HooksFilePath
    ) : UninstallOperation;

    public sealed record DeleteFile(string FilePath) : UninstallOperation;

    public sealed record DeleteDirectory(string DirectoryPath) : UninstallOperation;

    public sealed record RemoveLine(string FilePath, string Line) : UninstallOperation;

    public sealed record RemoveJsonObject(
        string FilePath,
        string TopLevelKey,
        string ObjectKey
    ) : UninstallOperation;

    /// <summary>Remove a scalar string value from a top-level JSON array.</summary>
    public sealed record RemoveJsonArrayValue(
        string FilePath,
        string TopLevelKey,
        string Value
    ) : UninstallOperation;

    public sealed record RemoveFencedBlock(string FilePath, string Marker) : UninstallOperation;

    /// <summary>Remove an entire TOML section by section path.</summary>
    public sealed record RemoveTomlSection(
        string FilePath,
        string SectionPath
    ) : UninstallOperation;

    public sealed record NotSupported(string Message) : UninstallOperation;
}
