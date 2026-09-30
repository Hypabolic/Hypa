using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// One attach-config loader for mux host and attach client.
/// Compression JSON stays at ~/.hypa/config.json. Attach config is TOML at
/// ~/.config/hypa/config.toml. Two formats. Do not merge the files.
/// </summary>
public interface IAttachConfigLoader
{
    string ResolvePath();

    AttachConfigResult<AttachClientConfig> Load();

    AttachConfigResult<AttachClientConfig> Parse(string text);

    string DefaultToml();

    AttachConfigResult<AttachConfigResetResult> ResetKeys();

    AttachConfigResult<AttachClientConfig> Patch(IReadOnlyList<AttachConfigAssignment> assignments) =>
        AttachConfigResult<AttachClientConfig>.Fail(
            AttachConfigError.Value(
                "patch",
                "Attach config patch is not supported.",
                line: null));
}

public sealed record AttachConfigResetResult(
    string Message,
    string? Path,
    string? BackupPath,
    bool Wrote);
