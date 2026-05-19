namespace Hypa.Runtime.Domain.Hooks;

public abstract record InstallOperation
{
    public sealed record PatchJsonHook(
        string FilePath,
        string HookEventName,
        string HookJson
    ) : InstallOperation;

    public sealed record PatchTomlKey(
        string FilePath,
        string Section,
        string Key,
        string Value
    ) : InstallOperation;

    public sealed record EnsureCodexHooksFeature(
        string FilePath
    ) : InstallOperation;

    public sealed record WriteFile(
        string FilePath,
        string Content
    ) : InstallOperation;

    public sealed record InjectLine(
        string FilePath,
        string Line,
        bool CreateIfMissing = true
    ) : InstallOperation;

    public sealed record NotSupported(string Message) : InstallOperation;

    /// <summary>Inject a fenced HTML-comment block into a file, replacing it if already present.</summary>
    public sealed record InjectFencedBlock(
        string FilePath,
        string Marker,
        string Content,
        bool CreateIfMissing = true
    ) : InstallOperation;

    /// <summary>Merge a JSON object at a top-level key in a JSON settings file.</summary>
    public sealed record PatchJsonObject(
        string FilePath,
        string TopLevelKey,
        string ObjectKey,
        string ObjectJson
    ) : InstallOperation;

    /// <summary>Write or replace a complete TOML section by section path.</summary>
    public sealed record PatchTomlSection(
        string FilePath,
        string SectionPath,
        string Content
    ) : InstallOperation;
}
