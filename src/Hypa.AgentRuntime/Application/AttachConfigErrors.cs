using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application;

/// <summary>Shared fail-closed attach-config load and error lines.</summary>
public static class AttachConfigErrors
{
    public const string IssuesFound = "config: issues found";

    public static AttachConfigResult<AttachClientConfig> LoadRequired(IAttachConfigLoader? loader)
    {
        if (loader is null)
            return AttachConfigResult<AttachClientConfig>.Ok(AttachClientConfig.Default);
        return loader.Load();
    }

    public static bool TryLoad(
        IAttachConfigLoader? loader,
        TextWriter errors,
        out AttachClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var loaded = LoadRequired(loader);
        if (loaded.IsOk)
        {
            config = loaded.Value;
            return true;
        }

        Write(errors, loaded.Errors);
        config = AttachClientConfig.Default;
        return false;
    }

    public static void Write(TextWriter writer, IReadOnlyList<AttachConfigError> errors)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(errors);
        writer.WriteLine(IssuesFound);
        foreach (var error in errors)
            writer.WriteLine(error.ToString());
    }
}
