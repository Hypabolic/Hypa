using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using static Hypa.AgentRuntime.Infrastructure.Config.TomlSubsetParser;

namespace Hypa.AgentRuntime.Infrastructure.Occupants;

/// <summary>Parses occupant TOML through the attach TOML subset. Fail closed.</summary>
public static class OccupantManifestToml
{
    public static AttachConfigResult<OccupantManifest> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parsed = TomlSubsetParser.Parse(text);
        if (!parsed.IsOk)
            return AttachConfigResult<OccupantManifest>.Fail(parsed.Errors);

        return Bind(parsed.Value!);
    }

    internal static AttachConfigResult<OccupantManifest> Bind(TomlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<AttachConfigError>();
        string? id = null;
        string? cwd = null;
        string? transcriptSource = null;
        string? transcriptRoot = null;
        IReadOnlyList<string>? command = null;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var assignment in document.Assignments)
        {
            switch (assignment.Path)
            {
                case "id":
                    TryString(assignment, errors, out id);
                    break;
                case "cwd":
                    TryString(assignment, errors, out cwd);
                    break;
                case "command":
                    TryStringArray(assignment, errors, out command);
                    break;
                case "transcript.source":
                    TryString(assignment, errors, out transcriptSource);
                    break;
                case "transcript.root":
                    TryString(assignment, errors, out transcriptRoot);
                    break;
                default:
                    if (assignment.Path.StartsWith("env.", StringComparison.Ordinal))
                    {
                        var key = assignment.Path["env.".Length..];
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            errors.Add(AttachConfigError.Value(
                                assignment.Path, "env key is empty.", assignment.Line));
                            break;
                        }

                        if (TryString(assignment, errors, out var value) && value is not null)
                            env[key] = value;
                        break;
                    }

                    errors.Add(AttachConfigError.Unknown(assignment.Path, assignment.Line));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(id))
            errors.Add(AttachConfigError.Value("id", "id is required.", 1));
        if (command is null || command.Count == 0 || string.IsNullOrWhiteSpace(command[0]))
            errors.Add(AttachConfigError.Value("command", "command must be a non-empty array.", 1));
        if (string.IsNullOrWhiteSpace(transcriptSource))
            errors.Add(AttachConfigError.Value("transcript.source", "transcript.source is required.", 1));
        if (string.IsNullOrWhiteSpace(transcriptRoot))
            errors.Add(AttachConfigError.Value("transcript.root", "transcript.root is required.", 1));

        if (errors.Count > 0)
            return AttachConfigResult<OccupantManifest>.Fail(errors);

        return AttachConfigResult<OccupantManifest>.Ok(new OccupantManifest
        {
            Id = id!.Trim(),
            Command = command!,
            Cwd = string.IsNullOrWhiteSpace(cwd) ? "{workspace}" : cwd.Trim(),
            Env = env,
            TranscriptSource = transcriptSource!.Trim(),
            TranscriptRoot = transcriptRoot!.Trim(),
        });
    }

    private static bool TryString(
        TomlAssignment assignment,
        List<AttachConfigError> errors,
        out string? value)
    {
        if (assignment.Value is TomlStringValue s)
        {
            value = s.Value;
            return true;
        }

        errors.Add(AttachConfigError.Type(assignment.Path, "a string", assignment.Line));
        value = null;
        return false;
    }

    private static bool TryStringArray(
        TomlAssignment assignment,
        List<AttachConfigError> errors,
        out IReadOnlyList<string>? value)
    {
        if (assignment.Value is not TomlArrayValue array)
        {
            errors.Add(AttachConfigError.Type(assignment.Path, "an array", assignment.Line));
            value = null;
            return false;
        }

        var list = new List<string>(array.Items.Count);
        foreach (var item in array.Items)
        {
            if (item is not TomlStringValue s)
            {
                errors.Add(AttachConfigError.Type(assignment.Path, "an array of strings", assignment.Line));
                value = null;
                return false;
            }

            list.Add(s.Value);
        }

        value = list;
        return true;
    }
}

/// <summary>Bundled first-party occupants embedded in <c>Hypa.AgentRuntime</c>.</summary>
public sealed class BundledOccupantManifestRegistry : IOccupantManifestRegistry
{
    private readonly Dictionary<string, OccupantManifest> _byId;

    public BundledOccupantManifestRegistry(IEnumerable<OccupantManifest>? extra = null)
    {
        _byId = new Dictionary<string, OccupantManifest>(StringComparer.Ordinal);
        foreach (var manifest in LoadBundled())
            _byId[manifest.Id] = manifest;
        if (extra is not null)
        {
            foreach (var manifest in extra)
                _byId[manifest.Id] = manifest;
        }
    }

    public static BundledOccupantManifestRegistry Default { get; } = new();

    public bool TryGet(string id, out OccupantManifest? manifest)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            manifest = null;
            return false;
        }

        var trimmed = id.Trim();
        if (_byId.TryGetValue(trimmed, out manifest))
            return true;

        if (AgentKindCatalog.TryResolve(trimmed, out var canonical)
            && _byId.TryGetValue(canonical, out manifest))
        {
            return true;
        }

        manifest = null;
        return false;
    }

    private static IEnumerable<OccupantManifest> LoadBundled()
    {
        var loaded = new Dictionary<string, OccupantManifest>(StringComparer.Ordinal);
        var asm = typeof(BundledOccupantManifestRegistry).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.Contains(".Resources.occupants.", StringComparison.Ordinal)
                || !name.EndsWith(".toml", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded occupant resource: {name}");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var parsed = OccupantManifestToml.Parse(text);
            if (!parsed.IsOk)
            {
                var detail = string.Join("; ", parsed.Errors.Select(e => e.ToString()));
                throw new InvalidOperationException($"Invalid bundled occupant '{name}': {detail}");
            }

            loaded[parsed.Value!.Id] = parsed.Value;
        }

        foreach (var id in AgentKindCatalog.All)
        {
            if (!loaded.ContainsKey(id))
                loaded[id] = AgentKindCatalog.CreateOccupantManifest(id);
        }

        return loaded.Values;
    }
}
