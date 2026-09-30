using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Infrastructure.Config;

/// <summary>
/// Attach TOML loader. Compression JSON stays at ~/.hypa/config.json.
/// This type reads ~/.config/hypa/config.toml (or HYPA_CONFIG_PATH).
/// Two formats. Do not merge the files.
/// </summary>
public sealed class FileAttachConfigLoader : IAttachConfigLoader
{
    public const string ConfigPathVariable = "HYPA_CONFIG_PATH";
    public const string XdgConfigHomeVariable = "XDG_CONFIG_HOME";

    private readonly IAttachConfigEnvironment _env;
    private readonly IAttachConfigFiles _files;
    private readonly Func<DateTimeOffset> _clock;

    public FileAttachConfigLoader(
        IAttachConfigEnvironment? env = null,
        IAttachConfigFiles? files = null,
        Func<DateTimeOffset>? clock = null)
    {
        _env = env ?? new SystemAttachConfigEnvironment();
        _files = files ?? new SystemAttachConfigFiles();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string ResolvePath()
    {
        var explicitPath = _env.GetVariable(ConfigPathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return CanonicalizePath(explicitPath);

        var xdg = _env.GetVariable(XdgConfigHomeVariable);
        if (!string.IsNullOrWhiteSpace(xdg))
            return CanonicalizePath(Path.Combine(xdg.Trim(), "hypa", "config.toml"));

        if (_env.IsWindows && !string.IsNullOrWhiteSpace(_env.AppData))
            return CanonicalizePath(Path.Combine(_env.AppData, "hypa", "config.toml"));

        return CanonicalizePath(Path.Combine(_env.UserHome, ".config", "hypa", "config.toml"));
    }

    /// <summary>
    /// Bind relative <c>HYPA_CONFIG_PATH</c> and <c>XDG_CONFIG_HOME</c> to this
    /// process cwd. Mux serve inherits <c>--cwd</c> and must not re-resolve.
    /// </summary>
    public static void PinResolvedPaths(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        PinResolvedPath(environment, ConfigPathVariable);
        PinResolvedPath(environment, XdgConfigHomeVariable);
    }

    internal static string CanonicalizePath(string path)
    {
        path = path.Trim();
        if (path.Length == 0)
            return path;
        // Windows drive paths are not rooted on Unix. Keep them for test doubles.
        if (!Path.IsPathRooted(path) && path.Length >= 2 && path[1] == ':')
            return path;
        return Path.GetFullPath(path);
    }

    private static void PinResolvedPath(IDictionary<string, string?> environment, string name)
    {
        if (!environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            return;
        environment[name] = CanonicalizePath(value);
    }

    public AttachConfigResult<AttachClientConfig> Load()
    {
        var explicitPath = _env.GetVariable(ConfigPathVariable);
        var path = ResolvePath();
        if (!_files.FileExists(path))
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return AttachConfigResult<AttachClientConfig>.Fail(AttachConfigError.Missing(path));
            return AttachConfigResult<AttachClientConfig>.Ok(AttachClientConfig.Default);
        }

        string text;
        try
        {
            text = _files.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return AttachConfigResult<AttachClientConfig>.Fail(
                new AttachConfigError(AttachConfigError.IoError, ex.Message, path, Line: null));
        }

        return Parse(text);
    }

    public AttachConfigResult<AttachClientConfig> Parse(string text) =>
        TomlAttachConfigBinder.Bind(text);

    public string DefaultToml() => AttachConfigDefaults.Toml;

    public AttachConfigResult<AttachConfigResetResult> ResetKeys()
    {
        var path = ResolvePath();
        if (!_files.FileExists(path))
        {
            return AttachConfigResult<AttachConfigResetResult>.Ok(new AttachConfigResetResult(
                "No attach config file. Built-in keys apply.",
                path,
                BackupPath: null,
                Wrote: false));
        }

        string text;
        try
        {
            text = _files.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return AttachConfigResult<AttachConfigResetResult>.Fail(
                new AttachConfigError(AttachConfigError.IoError, ex.Message, path, Line: null));
        }

        var rewrite = KeySectionRewriter.RemoveKeySections(text);
        if (!rewrite.IsOk)
            return AttachConfigResult<AttachConfigResetResult>.Fail(rewrite.Errors);

        if (!rewrite.Value.RemovedKeys)
        {
            return AttachConfigResult<AttachConfigResetResult>.Ok(new AttachConfigResetResult(
                "No key tables. Built-in keys apply.",
                path,
                BackupPath: null,
                Wrote: false));
        }

        var backup = $"{path}.{_clock().UtcDateTime.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture)}Z.bak";
        try
        {
            _files.CopyFile(path, backup);
            _files.WriteAllText(path, rewrite.Value.Text);
        }
        catch (Exception ex)
        {
            return AttachConfigResult<AttachConfigResetResult>.Fail(
                new AttachConfigError(AttachConfigError.IoError, ex.Message, path, Line: null));
        }

        return AttachConfigResult<AttachConfigResetResult>.Ok(new AttachConfigResetResult(
            $"Copied {path} to {backup}. Removed key tables. Built-in keys apply.",
            path,
            backup,
            Wrote: true));
    }

    public AttachConfigResult<AttachClientConfig> Patch(IReadOnlyList<AttachConfigAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        var path = ResolvePath();
        string text;
        if (!_files.FileExists(path))
            text = "";
        else
        {
            try
            {
                text = _files.ReadAllText(path);
            }
            catch (Exception ex)
            {
                return AttachConfigResult<AttachClientConfig>.Fail(
                    new AttachConfigError(AttachConfigError.IoError, ex.Message, path, Line: null));
            }
        }

        var rewrite = TomlKeyRewriter.Upsert(text, assignments);
        if (!rewrite.IsOk)
            return AttachConfigResult<AttachClientConfig>.Fail(rewrite.Errors);

        var bound = TomlAttachConfigBinder.Bind(rewrite.Value);
        if (!bound.IsOk)
            return bound;

        try
        {
            _files.WriteAllText(path, rewrite.Value);
        }
        catch (Exception ex)
        {
            return AttachConfigResult<AttachClientConfig>.Fail(
                new AttachConfigError(AttachConfigError.IoError, ex.Message, path, Line: null));
        }

        return bound;
    }
}
