using Hypa.Infrastructure.Hooks;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Doctor;

public sealed class CodexStorageCheck : IDoctorCheck
{
    private readonly HypaDataOptions _dataOptions;
    private readonly IFileSystem _fileSystem;
    private readonly Func<string, bool> _writeProbe;

    private readonly string _configPath;

    public CodexStorageCheck(HypaDataOptions dataOptions, IFileSystem fileSystem)
        : this(dataOptions, fileSystem, ProbeWritable, CodexConfigPaths.ResolveConfigPath()) { }

    internal CodexStorageCheck(
        HypaDataOptions dataOptions,
        IFileSystem fileSystem,
        Func<string, bool> writeProbe,
        string configPath)
    {
        _dataOptions = dataOptions;
        _fileSystem = fileSystem;
        _writeProbe = writeProbe;
        _configPath = configPath;
    }

    public string Category => "Codex Storage";

    public DoctorCheckResult Run()
    {
        var dataDir = _dataOptions.DataDirectory;
        var writable = _writeProbe(dataDir);
        var warnings = new List<string>();

        if (!writable)
            warnings.Add("Data directory is not writable on host");

        if (_fileSystem.FileExists(_configPath))
        {
            var content = _fileSystem.ReadAllText(_configPath);
            if (!ConfigContainsWritableRoot(content, dataDir))
                warnings.Add($"Add to {_configPath}:\n{BuildTomlSnippet(dataDir)}");
        }

        if (warnings.Count > 0)
            return new DoctorCheckResult("Codex storage", dataDir, DoctorStatus.Warn,
                string.Join("\n\n", warnings));

        return new DoctorCheckResult("Codex storage", dataDir, DoctorStatus.Ok);
    }

    private static bool ConfigContainsWritableRoot(string content, string dataDir)
    {
        var inSection = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = trimmed == "[sandbox_workspace_write]";
                continue;
            }
            if (!inSection) continue;
            if (trimmed.Contains($"\"{dataDir}\"", StringComparison.Ordinal) ||
                trimmed.Contains($"'{dataDir}'", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string BuildTomlSnippet(string dataDir) =>
        $"""
        sandbox_mode = "workspace-write"

        [sandbox_workspace_write]
        writable_roots = [
          "{dataDir}"
        ]
        """;

    private static bool ProbeWritable(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        var tmp = Path.Combine(directory, $".hypa-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tmp, "");
            File.Delete(tmp);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
