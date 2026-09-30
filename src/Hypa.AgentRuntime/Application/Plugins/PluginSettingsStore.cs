using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Persists declared plugin settings keys to JSON under the plugin config dir.
/// The plugin owns extra keys in the same file.
/// </summary>
public sealed class PluginSettingsStore
{
    public const string ConfigFileName = "settings.json";

    private readonly IPluginFiles _files;
    private readonly IPluginPathRoots _paths;

    public PluginSettingsStore(IPluginFiles files, IPluginPathRoots paths)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string ConfigPath(string pluginId) =>
        Path.Combine(_paths.PluginConfigDir(pluginId), ConfigFileName);

    public PluginResult<PluginSettingsSnapshot> Read(
        InstalledPlugin plugin,
        IReadOnlyList<PluginManifestSettingsField> fields)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
            values[field.Key] = NormalizeStored(field, field.Default);

        var path = ConfigPath(plugin.PluginId);
        if (!_files.FileExists(path))
            return PluginResult<PluginSettingsSnapshot>.Ok(new PluginSettingsSnapshot
            {
                PluginId = plugin.PluginId,
                Values = values,
            });

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(_files.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException ex)
        {
            return PluginResult<PluginSettingsSnapshot>.Fail(
                PluginError.SettingsMalformed,
                ex.Message);
        }

        if (root is null)
        {
            return PluginResult<PluginSettingsSnapshot>.Fail(
                PluginError.SettingsMalformed,
                "plugin settings file must be a JSON object");
        }

        foreach (var field in fields)
        {
            if (!root.TryGetPropertyValue(field.Key, out var node) || node is null)
                continue;
            if (!TryNormalizeNode(field, node, out var stored, out var error))
                return PluginResult<PluginSettingsSnapshot>.Fail(error);
            values[field.Key] = stored;
        }

        return PluginResult<PluginSettingsSnapshot>.Ok(new PluginSettingsSnapshot
        {
            PluginId = plugin.PluginId,
            Values = values,
        });
    }

    public PluginResult<PluginSettingsWriteOutcome> Write(
        InstalledPlugin plugin,
        IReadOnlyList<PluginManifestSettingsField> fields,
        string key,
        string rawValue)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (string.IsNullOrWhiteSpace(key))
        {
            return PluginResult<PluginSettingsWriteOutcome>.Fail(
                PluginError.InvalidParams,
                "settings key is required");
        }

        var field = fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
        if (field is null)
        {
            return PluginResult<PluginSettingsWriteOutcome>.Fail(
                PluginError.SettingsKeyUnknown,
                "unknown settings key '" + key + "'");
        }

        if (!TryNormalizeInput(field, rawValue, out var normalized, out var validationError))
        {
            var current = Read(plugin, fields);
            if (!current.IsOk)
                return PluginResult<PluginSettingsWriteOutcome>.Fail(current.Error);
            return PluginResult<PluginSettingsWriteOutcome>.Ok(new PluginSettingsWriteOutcome
            {
                Applied = false,
                Values = current.Value.Values,
            });
        }

        var path = ConfigPath(plugin.PluginId);
        JsonObject root;
        if (_files.FileExists(path))
        {
            try
            {
                root = JsonNode.Parse(_files.ReadAllText(path)) as JsonObject
                    ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                return PluginResult<PluginSettingsWriteOutcome>.Fail(
                    PluginError.SettingsMalformed,
                    ex.Message);
            }
        }
        else
        {
            _files.CreateDirectory(_paths.PluginConfigDir(plugin.PluginId));
            root = new JsonObject();
        }

        root[field.Key] = ToJsonNode(field, normalized);
        _files.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var snapshot = Read(plugin, fields);
        if (!snapshot.IsOk)
            return PluginResult<PluginSettingsWriteOutcome>.Fail(snapshot.Error);
        return PluginResult<PluginSettingsWriteOutcome>.Ok(new PluginSettingsWriteOutcome
        {
            Applied = true,
            Values = snapshot.Value.Values,
        });
    }

    private static bool TryNormalizeInput(
        PluginManifestSettingsField field,
        string rawValue,
        out string normalized,
        out PluginError? error)
    {
        error = null;
        normalized = "";
        switch (field.Type)
        {
            case PluginSettingsFieldTypes.String:
                normalized = rawValue ?? "";
                return true;
            case PluginSettingsFieldTypes.Integer:
                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    error = new PluginError(PluginError.InvalidSettingsValue, "settings value must be an integer");
                    return false;
                }

                normalized = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case PluginSettingsFieldTypes.Boolean:
                if (rawValue is "true" or "false")
                {
                    normalized = rawValue;
                    return true;
                }

                error = new PluginError(PluginError.InvalidSettingsValue, "settings value must be true or false");
                return false;
            case PluginSettingsFieldTypes.Choice:
                if (field.Choices.Count == 0)
                {
                    error = new PluginError(PluginError.InvalidSettingsField, "choice field has no choices");
                    return false;
                }

                if (!field.Choices.Contains(rawValue, StringComparer.Ordinal))
                {
                    error = new PluginError(PluginError.InvalidSettingsValue, "settings value is not an allowed choice");
                    return false;
                }

                normalized = rawValue;
                return true;
            default:
                error = new PluginError(PluginError.UnknownSettingsFieldType, "unknown settings field type");
                return false;
        }
    }

    private static bool TryNormalizeNode(
        PluginManifestSettingsField field,
        JsonNode node,
        out string normalized,
        out PluginError error)
    {
        normalized = "";
        error = default!;
        switch (field.Type)
        {
            case PluginSettingsFieldTypes.String:
                if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
                {
                    error = new PluginError(PluginError.SettingsMalformed, "settings value must be a string");
                    return false;
                }

                normalized = text ?? "";
                return true;
            case PluginSettingsFieldTypes.Integer:
                if (node is JsonValue intValue && intValue.TryGetValue<int>(out var number))
                {
                    normalized = number.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                if (node is JsonValue longValue && longValue.TryGetValue<long>(out var longNumber))
                {
                    normalized = longNumber.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                error = new PluginError(PluginError.SettingsMalformed, "settings value must be an integer");
                return false;
            case PluginSettingsFieldTypes.Boolean:
                if (node is JsonValue boolValue && boolValue.TryGetValue<bool>(out var flag))
                {
                    normalized = flag ? "true" : "false";
                    return true;
                }

                error = new PluginError(PluginError.SettingsMalformed, "settings value must be a boolean");
                return false;
            case PluginSettingsFieldTypes.Choice:
                if (node is not JsonValue choiceValue || !choiceValue.TryGetValue<string>(out var choice))
                {
                    error = new PluginError(PluginError.SettingsMalformed, "settings value must be a string");
                    return false;
                }

                if (!field.Choices.Contains(choice ?? "", StringComparer.Ordinal))
                {
                    error = new PluginError(PluginError.SettingsMalformed, "settings value is not an allowed choice");
                    return false;
                }

                normalized = choice ?? "";
                return true;
            default:
                error = new PluginError(PluginError.UnknownSettingsFieldType, "unknown settings field type");
                return false;
        }
    }

    private static string NormalizeStored(PluginManifestSettingsField field, string rawDefault)
    {
        if (TryNormalizeInput(field, rawDefault, out var normalized, out _))
            return normalized;
        return rawDefault;
    }

    private static JsonNode ToJsonNode(PluginManifestSettingsField field, string normalized) =>
        field.Type switch
        {
            PluginSettingsFieldTypes.Integer => JsonValue.Create(int.Parse(normalized, CultureInfo.InvariantCulture)),
            PluginSettingsFieldTypes.Boolean => JsonValue.Create(string.Equals(normalized, "true", StringComparison.Ordinal)),
            _ => JsonValue.Create(normalized),
        };
}
