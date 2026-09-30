using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Config;

namespace Hypa.AgentIntelligence.Detection;

/// <summary>Bind detection TOML through the attach subset parser. No reflection.</summary>
internal static class ManifestTomlBinder
{
    public static bool TryParse(string content, out AgentManifestDocument? manifest, out string error)
    {
        manifest = null;
        var parsed = TomlSubsetParser.Parse(content);
        if (!parsed.IsOk)
        {
            error = parsed.Error.Message;
            return false;
        }

        try
        {
            manifest = Bind(parsed.Value);
            error = "";
            return true;
        }
        catch (ManifestBindException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static AgentManifestDocument Bind(TomlSubsetParser.TomlDocument doc)
    {
        string? id = null;
        string? version = null;
        uint? minEngine = null;
        IReadOnlyList<string> aliases = [];
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "version", "min_engine_version", "updated_at", "aliases",
        };

        foreach (var assignment in doc.Assignments)
        {
            if (!known.Contains(assignment.Path))
                throw new ManifestBindException($"unknown field {assignment.Path}");
            switch (assignment.Path)
            {
                case "id":
                    id = RequireString(assignment.Value, "id");
                    break;
                case "version":
                    version = RequireString(assignment.Value, "version");
                    ManifestVersion.Parse(version);
                    break;
                case "min_engine_version":
                    minEngine = RequireUint(assignment.Value, "min_engine_version");
                    break;
                case "updated_at":
                    _ = RequireString(assignment.Value, "updated_at");
                    break;
                case "aliases":
                    aliases = RequireStringList(assignment.Value, "aliases");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(id))
            throw new ManifestBindException("manifest id is required");

        var rules = new List<ManifestRuleDocument>();
        foreach (var table in doc.ArrayTables)
        {
            if (table.Path != "rules")
                throw new ManifestBindException($"unknown array table {table.Path}");
            rules.Add(BindRule(table));
        }

        return new AgentManifestDocument
        {
            Id = id,
            Version = version,
            MinEngineVersion = minEngine,
            Aliases = aliases,
            Rules = rules,
        };
    }

    private static ManifestRuleDocument BindRule(TomlSubsetParser.TomlArrayTable table)
    {
        string? id = null;
        AgentStatus? state = null;
        var priority = 0;
        var region = "whole_recent";
        var visibleIdle = false;
        var visibleBlocker = false;
        var visibleWorking = false;
        var skip = false;
        IReadOnlyList<ManifestGateDocument> all = [];
        IReadOnlyList<ManifestGateDocument> any = [];
        IReadOnlyList<ManifestGateDocument> notGate = [];
        IReadOnlyList<string> contains = [];
        IReadOnlyList<string> regex = [];
        IReadOnlyList<string> lineRegex = [];

        foreach (var field in table.Fields)
        {
            var local = LocalName(field.Path, "rules");
            switch (local)
            {
                case "id":
                    id = RequireString(field.Value, "id");
                    break;
                case "state":
                    state = ParseState(RequireString(field.Value, "state"));
                    break;
                case "priority":
                    priority = (int)RequireLong(field.Value, "priority");
                    break;
                case "region":
                    region = RequireString(field.Value, "region");
                    break;
                case "visible_idle":
                    visibleIdle = RequireBool(field.Value, "visible_idle");
                    break;
                case "visible_blocker":
                    visibleBlocker = RequireBool(field.Value, "visible_blocker");
                    break;
                case "visible_working":
                    visibleWorking = RequireBool(field.Value, "visible_working");
                    break;
                case "skip_state_update":
                    skip = RequireBool(field.Value, "skip_state_update");
                    break;
                case "all":
                    all = RequireGateList(field.Value, "all");
                    break;
                case "any":
                    any = RequireGateList(field.Value, "any");
                    break;
                case "not":
                    notGate = RequireGateList(field.Value, "not");
                    break;
                case "contains":
                    contains = RequireStringList(field.Value, "contains");
                    break;
                case "regex":
                    regex = RequireStringList(field.Value, "regex");
                    break;
                case "line_regex":
                    lineRegex = RequireStringList(field.Value, "line_regex");
                    break;
                default:
                    throw new ManifestBindException($"unknown field {local}");
            }
        }

        if (string.IsNullOrWhiteSpace(id))
            throw new ManifestBindException("manifest rule id must not be empty");

        return new ManifestRuleDocument
        {
            Id = id,
            State = state,
            Priority = priority,
            Region = region,
            VisibleIdle = visibleIdle,
            VisibleBlocker = visibleBlocker,
            VisibleWorking = visibleWorking,
            SkipStateUpdate = skip,
            Gate = new ManifestGateDocument
            {
                All = all,
                Any = any,
                Not = notGate,
                Contains = contains,
                Regex = regex,
                LineRegex = lineRegex,
            },
        };
    }

    private static ManifestGateDocument BindGate(TomlSubsetParser.TomlInlineTableValue table)
    {
        IReadOnlyList<ManifestGateDocument> all = [];
        IReadOnlyList<ManifestGateDocument> any = [];
        IReadOnlyList<ManifestGateDocument> notGate = [];
        IReadOnlyList<string> contains = [];
        IReadOnlyList<string> regex = [];
        IReadOnlyList<string> lineRegex = [];

        foreach (var field in table.Fields)
        {
            switch (field.Path)
            {
                case "all":
                    all = RequireGateList(field.Value, "all");
                    break;
                case "any":
                    any = RequireGateList(field.Value, "any");
                    break;
                case "not":
                    notGate = RequireGateList(field.Value, "not");
                    break;
                case "contains":
                    contains = RequireStringList(field.Value, "contains");
                    break;
                case "regex":
                    regex = RequireStringList(field.Value, "regex");
                    break;
                case "line_regex":
                    lineRegex = RequireStringList(field.Value, "line_regex");
                    break;
                default:
                    throw new ManifestBindException($"unknown field {field.Path}");
            }
        }

        return new ManifestGateDocument
        {
            All = all,
            Any = any,
            Not = notGate,
            Contains = contains,
            Regex = regex,
            LineRegex = lineRegex,
        };
    }

    private static IReadOnlyList<ManifestGateDocument> RequireGateList(TomlSubsetParser.TomlValue value, string key)
    {
        if (value is not TomlSubsetParser.TomlArrayValue array)
            throw new ManifestBindException($"{key} must be an array");
        var list = new List<ManifestGateDocument>(array.Items.Count);
        foreach (var item in array.Items)
        {
            if (item is not TomlSubsetParser.TomlInlineTableValue inline)
                throw new ManifestBindException($"{key} entries must be inline tables");
            list.Add(BindGate(inline));
        }

        return list;
    }

    private static IReadOnlyList<string> RequireStringList(TomlSubsetParser.TomlValue value, string key)
    {
        if (value is not TomlSubsetParser.TomlArrayValue array)
            throw new ManifestBindException($"{key} must be an array");
        var list = new List<string>(array.Items.Count);
        foreach (var item in array.Items)
            list.Add(RequireString(item, key));
        return list;
    }

    private static string RequireString(TomlSubsetParser.TomlValue value, string key)
    {
        if (value is TomlSubsetParser.TomlStringValue s)
            return s.Value;
        throw new ManifestBindException($"{key} must be a string");
    }

    private static bool RequireBool(TomlSubsetParser.TomlValue value, string key)
    {
        if (value is TomlSubsetParser.TomlBoolValue b)
            return b.Value;
        throw new ManifestBindException($"{key} must be a boolean");
    }

    private static long RequireLong(TomlSubsetParser.TomlValue value, string key)
    {
        if (value is TomlSubsetParser.TomlIntValue n)
            return n.Value;
        throw new ManifestBindException($"{key} must be an integer");
    }

    private static uint RequireUint(TomlSubsetParser.TomlValue value, string key)
    {
        var n = RequireLong(value, key);
        if (n < 0)
            throw new ManifestBindException($"{key} must be unsigned");
        return (uint)n;
    }

    private static AgentStatus ParseState(string value) =>
        value switch
        {
            "idle" => AgentStatus.Idle,
            "working" => AgentStatus.Working,
            "blocked" => AgentStatus.Blocked,
            "unknown" => AgentStatus.Unknown,
            _ => throw new ManifestBindException($"unknown state {value}"),
        };

    private static string LocalName(string path, string table)
    {
        var prefix = table + ".";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    private sealed class ManifestBindException(string message) : Exception(message);
}
