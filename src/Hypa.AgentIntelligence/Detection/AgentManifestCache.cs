using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.AgentIntelligence.Detection;

/// <summary>
/// Instance cache. No static mutable product state.
/// </summary>
internal sealed class AgentManifestCache
{
    private const string ResourcePrefix = "Hypa.AgentIntelligence.Resources.agent-detection.";
    private const int MaxFetchBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly IAttachConfigFiles? _files;
    private readonly AgentDetectionPaths? _paths;
    private readonly IAgentManifestTextFetcher? _fetcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private Dictionary<string, CompiledManifest?> _loaded = new(StringComparer.Ordinal);
    private ManifestUpdateStatus _status = ManifestUpdateStatus.Empty;

    public AgentManifestCache(
        IAttachConfigFiles? files = null,
        AgentDetectionPaths? paths = null,
        IAgentManifestTextFetcher? fetcher = null,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        _files = files;
        _paths = paths;
        _fetcher = fetcher;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        ReloadUnlocked();
    }

    public static AgentManifestCache BundledOnly(ILogger? logger = null) =>
        new(files: null, paths: null, fetcher: null, time: null, logger: logger);

    public IReadOnlyList<AgentManifestSummary> ListSummaries()
    {
        lock (_gate)
            return SummariesUnlocked();
    }

    public IReadOnlyList<AgentManifestSummary> Reload()
    {
        lock (_gate)
        {
            ReloadUnlocked();
            return SummariesUnlocked();
        }
    }

    public long? LastCheckUnix
    {
        get
        {
            lock (_gate)
                return _status.LastCheckUnix;
        }
    }

    public string? LastResult
    {
        get
        {
            lock (_gate)
                return _status.LastResult;
        }
    }

    public CompiledManifest? Get(string agentId)
    {
        lock (_gate)
        {
            return _loaded.TryGetValue(agentId, out var loaded) ? loaded : null;
        }
    }

    public void CheckRemoteUpdates(bool enabled)
    {
        if (!enabled)
            return;
        var url = _paths?.CatalogUrl;
        if (string.IsNullOrWhiteSpace(url) || _fetcher is null || _files is null || _paths is null)
            return;

        lock (_gate)
        {
            var now = UnixNow();
            try
            {
                var fetch = _fetcher.Fetch(url, MaxFetchBytes);
                if (!fetch.Ok)
                {
                    _status = _status with
                    {
                        LastCheckUnix = now,
                        LastResult = "failed: " + fetch.Error,
                    };
                    SaveStatusUnlocked();
                    return;
                }

                ApplyCatalogUnlocked(url, fetch.Body, now);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent detection manifest update failed");
                _status = _status with
                {
                    LastCheckUnix = now,
                    LastResult = "failed: " + ex.Message,
                };
                SaveStatusUnlocked();
            }
        }
    }

    private List<AgentManifestSummary> SummariesUnlocked()
    {
        var list = new List<AgentManifestSummary>(ScreenAgentCatalog.Ids.Length);
        foreach (var id in ScreenAgentCatalog.Ids)
        {
            if (!_loaded.TryGetValue(id, out var loaded) || loaded is null)
                continue;
            var remote = _status.Agent(id);
            list.Add(new AgentManifestSummary
            {
                Agent = id,
                Source = loaded.Origin.Label,
                SourceKind = loaded.Origin.SourceKind,
                ActiveVersion = loaded.Manifest.Version,
                CachedRemoteVersion = loaded.CachedRemoteVersion,
                LocalOverrideShadowingRemote = loaded.LocalOverrideShadowingRemote,
                RemoteUpdateResult = remote?.LastResult,
                RemoteUpdateError = remote?.LastError,
                RemoteLastCheckedUnix = remote?.LastCheckedUnix,
                Warning = loaded.Warning,
            });
        }

        return list;
    }

    private void ReloadUnlocked()
    {
        _status = LoadStatusUnlocked();
        var next = new Dictionary<string, CompiledManifest?>(StringComparer.Ordinal);
        foreach (var id in ScreenAgentCatalog.Ids)
            next[id] = LoadOneUnlocked(id);
        _loaded = next;
    }

    private CompiledManifest? LoadOneUnlocked(string agentId)
    {
        AgentManifestDocument? bundled;
        try
        {
            bundled = BundledDocument(agentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bundled agent detection manifest {Agent} is invalid", agentId);
            return null;
        }

        if (bundled is null)
            return null;

        var remote = ReadRemoteUnlocked(agentId, bundled);
        var cachedRemoteVersion = remote?.CachedRemoteVersion
            ?? (remote?.Origin.Kind == ManifestOriginKind.Remote ? remote.Manifest.Version : null);
        if (remote is not null)
            remote = remote with { CachedRemoteVersion = cachedRemoteVersion };

        if (_paths is null || _files is null)
            return remote ?? CompileBundled(agentId, bundled, null, cachedRemoteVersion, false);

        var overridePath = _paths.OverridePath(agentId);
        var localExists = _files.FileExists(overridePath);
        var localShadowing = localExists && cachedRemoteVersion is not null;
        if (remote is not null)
        {
            remote = remote with
            {
                CachedRemoteVersion = cachedRemoteVersion,
                LocalOverrideShadowingRemote = localShadowing,
            };
        }

        if (!localExists)
            return remote ?? CompileBundled(agentId, bundled, null, cachedRemoteVersion, localShadowing);

        try
        {
            var text = _files.ReadAllText(overridePath);
            if (!ManifestTomlBinder.TryParse(text, out var parsed, out var parseError) || parsed is null)
            {
                return WithWarning(
                    remote ?? CompileBundled(agentId, bundled, null, cachedRemoteVersion, localShadowing),
                    $"ignored override {overridePath} because it could not be loaded: {parseError}");
            }

            if (!ScreenAgentCatalog.ManifestMatchesAgent(parsed.Id, parsed.Aliases, agentId))
            {
                return WithWarning(
                    remote ?? CompileBundled(agentId, bundled, null, cachedRemoteVersion, localShadowing),
                    $"ignored override {overridePath} because manifest id {parsed.Id} does not match {agentId}");
            }

            try
            {
                return ManifestEngine.Compile(
                    parsed,
                    ManifestOrigin.Override(overridePath),
                    null,
                    cachedRemoteVersion,
                    localShadowing);
            }
            catch (Exception ex)
            {
                return WithWarning(
                    remote ?? CompileBundled(agentId, bundled, null, cachedRemoteVersion, localShadowing),
                    $"ignored override {overridePath} because it could not be compiled: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return WithWarning(
                remote ?? CompileBundled(agentId, bundled, null, cachedRemoteVersion, localShadowing),
                $"ignored override {overridePath} because it could not be loaded: {ex.Message}");
        }
    }

    private CompiledManifest? ReadRemoteUnlocked(string agentId, AgentManifestDocument bundled)
    {
        if (_paths is null || _files is null)
            return null;
        var path = _paths.RemotePath(agentId);
        if (!_files.FileExists(path))
            return null;

        try
        {
            var text = _files.ReadAllText(path);
            if (!ManifestTomlBinder.TryParse(text, out var parsed, out var parseError) || parsed is null)
            {
                return CompileBundled(
                    agentId,
                    bundled,
                    $"ignored remote manifest {path} because it could not be loaded: {parseError}",
                    null,
                    false);
            }

            if (!ScreenAgentCatalog.ManifestMatchesAgent(parsed.Id, parsed.Aliases, agentId))
            {
                return CompileBundled(
                    agentId,
                    bundled,
                    $"ignored remote manifest {path} because it could not be loaded: manifest id {parsed.Id} does not match {agentId}",
                    null,
                    false);
            }

            if (parsed.Version is null)
            {
                return CompileBundled(
                    agentId,
                    bundled,
                    $"ignored remote manifest {path} because it could not be loaded: remote manifest must include version",
                    null,
                    false);
            }

            if (parsed.MinEngineVersion is null)
            {
                return CompileBundled(
                    agentId,
                    bundled,
                    $"ignored remote manifest {path} because it could not be loaded: remote manifest must include min_engine_version",
                    null,
                    false);
            }

            if (parsed.MinEngineVersion.Value > ManifestEngine.EngineVersion)
            {
                return CompileBundled(
                    agentId,
                    bundled,
                    $"ignored remote manifest {path} because it could not be loaded: manifest requires engine {parsed.MinEngineVersion}, current engine is {ManifestEngine.EngineVersion}",
                    null,
                    false);
            }

            if (ManifestVersion.TryParse(parsed.Version, out var remoteVer)
                && ManifestVersion.TryParse(bundled.Version, out var bundledVer)
                && remoteVer.CompareTo(bundledVer) < 0)
            {
                return CompileBundled(
                    agentId,
                    bundled,
                    $"ignored remote manifest {path} because cached version {parsed.Version} is older than bundled {bundled.Version}",
                    parsed.Version,
                    false);
            }

            try
            {
                return ManifestEngine.Compile(
                    parsed,
                    ManifestOrigin.Remote(path, parsed.Version),
                    null,
                    parsed.Version,
                    false);
            }
            catch (Exception ex)
            {
                return CompileBundled(
                    agentId,
                    bundled,
                    $"ignored remote manifest {path} because it could not be compiled: {ex.Message}",
                    null,
                    false);
            }
        }
        catch (Exception ex)
        {
            return CompileBundled(
                agentId,
                bundled,
                $"ignored remote manifest {path} because it could not be loaded: {ex.Message}",
                null,
                false);
        }
    }

    private void ApplyCatalogUnlocked(string catalogUrl, string catalogBody, long now)
    {
        if (!TryParseCatalog(catalogBody, out var entries, out var catalogError))
        {
            _status = _status with { LastCheckUnix = now, LastResult = "failed: " + catalogError };
            SaveStatusUnlocked();
            return;
        }

        var baseUrl = BaseUrl(catalogUrl);
        var agents = new Dictionary<string, AgentRemoteStatus>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var manifestUrl = JoinUrl(baseUrl, entry.Path);
            var fetch = _fetcher!.Fetch(manifestUrl, MaxFetchBytes);
            if (!fetch.Ok)
            {
                agents[entry.Id] = new AgentRemoteStatus(
                    CachedRemoteVersionUnlocked(entry.Id),
                    null,
                    now,
                    "failed",
                    fetch.Error);
                continue;
            }

            try
            {
                ProcessRemote(entry.Id, fetch.Body);
                agents[entry.Id] = new AgentRemoteStatus(
                    CachedRemoteVersionUnlocked(entry.Id),
                    CachedRemoteVersionUnlocked(entry.Id),
                    now,
                    "updated",
                    null);
            }
            catch (Exception ex)
            {
                agents[entry.Id] = new AgentRemoteStatus(
                    CachedRemoteVersionUnlocked(entry.Id),
                    null,
                    now,
                    "failed",
                    ex.Message);
            }
        }

        _status = new ManifestUpdateStatus(now, "checked", agents);
        SaveStatusUnlocked();
        foreach (var id in agents.Keys)
            _loaded[id] = LoadOneUnlocked(id);
    }

    private void ProcessRemote(string agentId, string content)
    {
        if (!ManifestTomlBinder.TryParse(content, out var parsed, out var error) || parsed is null)
            throw new InvalidOperationException(error);
        if (!ScreenAgentCatalog.ManifestMatchesAgent(parsed.Id, parsed.Aliases, agentId))
            throw new InvalidOperationException($"manifest id {parsed.Id} does not match {agentId}");
        if (parsed.Version is null)
            throw new InvalidOperationException("remote manifest must include version");
        if (parsed.MinEngineVersion is null)
            throw new InvalidOperationException("remote manifest must include min_engine_version");
        if (parsed.MinEngineVersion.Value > ManifestEngine.EngineVersion)
            throw new InvalidOperationException($"manifest requires engine {parsed.MinEngineVersion}, current engine is {ManifestEngine.EngineVersion}");
        ManifestEngine.Validate(parsed);
        _files!.WriteAllText(_paths!.RemotePath(agentId), content);
    }

    private string? CachedRemoteVersionUnlocked(string agentId)
    {
        if (_paths is null || _files is null)
            return null;
        var path = _paths.RemotePath(agentId);
        if (!_files.FileExists(path))
            return null;
        try
        {
            var text = _files.ReadAllText(path);
            return ManifestTomlBinder.TryParse(text, out var parsed, out _) ? parsed?.Version : null;
        }
        catch
        {
            return null;
        }
    }

    private ManifestUpdateStatus LoadStatusUnlocked()
    {
        if (_paths is null || _files is null || !_files.FileExists(_paths.StatusPath))
            return ManifestUpdateStatus.Empty;
        try
        {
            return ManifestUpdateStatus.Parse(_files.ReadAllText(_paths.StatusPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse agent detection manifest status");
            return ManifestUpdateStatus.Empty;
        }
    }

    private void SaveStatusUnlocked()
    {
        if (_paths is null || _files is null)
            return;
        try
        {
            _files.WriteAllText(_paths.StatusPath, _status.ToToml());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save agent detection manifest update status");
        }
    }

    private static CompiledManifest CompileBundled(
        string agentId,
        AgentManifestDocument bundled,
        string? warning,
        string? cachedRemoteVersion,
        bool localShadowing) =>
        ManifestEngine.Compile(bundled, ManifestOrigin.Bundled, warning, cachedRemoteVersion, localShadowing);

    private static CompiledManifest WithWarning(CompiledManifest loaded, string warning)
    {
        return loaded with { Warning = warning };
    }

    internal static AgentManifestDocument? BundledDocument(string agentId)
    {
        var name = ResourcePrefix + agentId + ".toml";
        var assembly = typeof(AgentManifestCache).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? assembly.GetManifestResourceStream(
                "Hypa.AgentIntelligence.Resources.agent_detection." + agentId + ".toml");
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        if (!ManifestTomlBinder.TryParse(text, out var parsed, out var error) || parsed is null)
            throw new InvalidOperationException($"bundled {agentId} manifest is invalid: {error}");
        ManifestEngine.Validate(parsed);
        return parsed;
    }

    internal static IReadOnlyList<string> BundledResourceNames()
    {
        return typeof(AgentManifestCache).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryParseCatalog(
        string content,
        out List<CatalogEntry> entries,
        out string error)
    {
        entries = [];
        var parsed = TomlSubsetParser.Parse(content);
        if (!parsed.IsOk)
        {
            error = parsed.Error.Message;
            return false;
        }

        uint? schema = null;
        foreach (var assignment in parsed.Value.Assignments)
        {
            if (assignment.Path == "schema_version"
                && assignment.Value is TomlSubsetParser.TomlIntValue n)
            {
                schema = (uint)n.Value;
            }
        }

        if (schema != 1)
        {
            error = $"unsupported catalog schema_version {schema}";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in parsed.Value.ArrayTables)
        {
            if (table.Path != "agents")
                continue;
            string? id = null;
            string? path = null;
            foreach (var field in table.Fields)
            {
                var local = field.Path.StartsWith("agents.", StringComparison.Ordinal)
                    ? field.Path["agents.".Length..]
                    : field.Path;
                if (local == "id" && field.Value is TomlSubsetParser.TomlStringValue sid)
                    id = sid.Value;
                if (local == "path" && field.Value is TomlSubsetParser.TomlStringValue sp)
                    path = sp.Value;
            }

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path))
            {
                error = "catalog entry is missing id or path";
                return false;
            }

            var agent = ScreenAgentCatalog.Identify(id) ?? (ScreenAgentCatalog.IsScreenManifest(id) ? id : null);
            if (agent is null)
                continue;
            if (path.Contains("://", StringComparison.Ordinal)
                || path.StartsWith('/')
                || path.Split('/').Contains(".."))
            {
                error = $"catalog entry {id} has an unsafe path {path}";
                return false;
            }

            if (!seen.Add(agent))
            {
                error = $"catalog contains duplicate agent {id}";
                return false;
            }

            entries.Add(new CatalogEntry(agent, path));
        }

        error = "";
        return true;
    }

    private static string BaseUrl(string url)
    {
        var slash = url.LastIndexOf('/');
        return slash <= 0 ? url : url[..slash];
    }

    private static string JoinUrl(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + "/" + path;

    private long UnixNow() =>
        (long)_time.GetUtcNow().ToUnixTimeSeconds();

    private sealed record CatalogEntry(string Id, string Path);
}

internal sealed record ManifestUpdateStatus(
    long? LastCheckUnix,
    string? LastResult,
    IReadOnlyDictionary<string, AgentRemoteStatus> Agents)
{
    public static ManifestUpdateStatus Empty { get; } =
        new(null, null, new Dictionary<string, AgentRemoteStatus>(StringComparer.Ordinal));

    public AgentRemoteStatus? Agent(string id) =>
        Agents.TryGetValue(id, out var status) ? status : null;

    public static ManifestUpdateStatus Parse(string text)
    {
        var parsed = TomlSubsetParser.Parse(text);
        if (!parsed.IsOk)
            return Empty;
        long? lastCheck = null;
        string? lastResult = null;
        foreach (var assignment in parsed.Value.Assignments)
        {
            if (assignment.Path == "last_check_unix" && assignment.Value is TomlSubsetParser.TomlIntValue n)
                lastCheck = n.Value;
            if (assignment.Path == "last_result" && assignment.Value is TomlSubsetParser.TomlStringValue s)
                lastResult = s.Value;
        }

        var agents = new Dictionary<string, AgentRemoteStatus>(StringComparer.Ordinal);
        foreach (var table in parsed.Value.ArrayTables)
        {
            if (table.Path != "agents")
                continue;
            string? id = null;
            string? cached = null;
            string? attempted = null;
            long? checkedUnix = null;
            var result = "";
            string? error = null;
            foreach (var field in table.Fields)
            {
                var local = field.Path.StartsWith("agents.", StringComparison.Ordinal)
                    ? field.Path["agents.".Length..]
                    : field.Path;
                switch (local)
                {
                    case "id" when field.Value is TomlSubsetParser.TomlStringValue s:
                        id = s.Value;
                        break;
                    case "cached_version" when field.Value is TomlSubsetParser.TomlStringValue s:
                        cached = s.Value;
                        break;
                    case "attempted_version" when field.Value is TomlSubsetParser.TomlStringValue s:
                        attempted = s.Value;
                        break;
                    case "last_checked_unix" when field.Value is TomlSubsetParser.TomlIntValue n:
                        checkedUnix = n.Value;
                        break;
                    case "last_result" when field.Value is TomlSubsetParser.TomlStringValue s:
                        result = s.Value;
                        break;
                    case "last_error" when field.Value is TomlSubsetParser.TomlStringValue s:
                        error = s.Value;
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(id))
                agents[id] = new AgentRemoteStatus(cached, attempted, checkedUnix, result, error);
        }

        return new ManifestUpdateStatus(lastCheck, lastResult, agents);
    }

    public string ToToml()
    {
        var sb = new StringBuilder();
        if (LastCheckUnix is long unix)
            sb.Append("last_check_unix = ").Append(unix).Append('\n');
        if (LastResult is not null)
            sb.Append("last_result = \"").Append(Escape(LastResult)).Append("\"\n");
        foreach (var (id, status) in Agents)
        {
            sb.Append("\n[[agents]]\n");
            sb.Append("id = \"").Append(Escape(id)).Append("\"\n");
            if (status.CachedVersion is not null)
                sb.Append("cached_version = \"").Append(Escape(status.CachedVersion)).Append("\"\n");
            if (status.AttemptedVersion is not null)
                sb.Append("attempted_version = \"").Append(Escape(status.AttemptedVersion)).Append("\"\n");
            if (status.LastCheckedUnix is long checkedUnix)
                sb.Append("last_checked_unix = ").Append(checkedUnix).Append('\n');
            sb.Append("last_result = \"").Append(Escape(status.LastResult)).Append("\"\n");
            if (status.LastError is not null)
                sb.Append("last_error = \"").Append(Escape(status.LastError)).Append("\"\n");
        }

        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

internal sealed record AgentRemoteStatus(
    string? CachedVersion,
    string? AttemptedVersion,
    long? LastCheckedUnix,
    string LastResult,
    string? LastError);
