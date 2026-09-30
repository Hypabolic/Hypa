using System.Text.Json;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Transient Agents-view validation and projection.
/// </summary>
public static class AgentViewProjection
{
    public const int MaxFilterDepth = 8;
    public const int MaxFilterNodes = 64;
    public const int MaxFilterValues = 32;
    public const int MaxSortFields = 8;
    public const int MaxSourceChars = 120;
    public const int MaxLabelChars = 32;

    public static bool TryValidate(
        string? source,
        string? label,
        JsonElement filter,
        JsonElement sort,
        out AgentViewSpec spec,
        out string error)
    {
        spec = null!;
        if (!TryNormalizeSource(source, out var normalizedSource, out error))
            return false;
        if (!TryNormalizeLabel(label, out var normalizedLabel, out error))
            return false;

        var nodes = 0;
        if (filter.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            && !TryValidateFilter(filter, depth: 1, ref nodes, out error))
            return false;

        if (sort.ValueKind is JsonValueKind.Array)
        {
            if (sort.GetArrayLength() > MaxSortFields)
            {
                error = $"agent view sort may contain at most {MaxSortFields} fields";
                return false;
            }

            foreach (var item in sort.EnumerateArray())
            {
                if (!TryReadSortField(item, out _, out error))
                    return false;
            }
        }
        else if (sort.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            error = "agent view sort must be an array";
            return false;
        }

        spec = new AgentViewSpec
        {
            Source = normalizedSource,
            Label = normalizedLabel,
            Filter = CloneElement(filter),
            Sort = CloneElement(sort),
        };
        error = "";
        return true;
    }

    public static bool TryNormalizeSource(string? source, out string normalized, out string error)
    {
        normalized = "";
        var trimmed = source?.Trim() ?? "";
        if (trimmed.Length == 0
            || trimmed.Length > MaxSourceChars
            || !trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ':' or '.' or '_' or '-'))
        {
            error =
                $"agent view source must be non-empty, at most {MaxSourceChars} characters, and contain only ASCII letters, digits, colon, dot, underscore, or hyphen";
            return false;
        }

        if (trimmed.StartsWith("plugin:", StringComparison.Ordinal))
        {
            var pluginId = trimmed["plugin:".Length..];
            if (pluginId.Length == 0
                || pluginId.Length > MaxSourceChars
                || !pluginId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ':' or '.' or '_' or '-'))
            {
                error = "plugin-owned agent view source has an invalid plugin id";
                return false;
            }
        }

        normalized = trimmed;
        error = "";
        return true;
    }

    public static IReadOnlyList<string> Apply(
        IReadOnlyList<AgentViewRow> rows,
        AgentViewSpec spec,
        AgentViewEvalContext context,
        AgentPanelSort panelSort = AgentPanelSort.Spaces)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);

        IEnumerable<AgentViewRow> filtered = rows;
        if (spec.Filter.ValueKind is JsonValueKind.Object)
            filtered = rows.Where(row => MatchesFilter(row, spec.Filter, context));

        var list = filtered.ToList();
        if (spec.HasSort)
        {
            list.Sort((left, right) => CompareRows(left, right, spec.Sort, context));
        }
        else if (panelSort is AgentPanelSort.Priority)
        {
            list.Sort(ComparePriority);
        }

        var ids = new string[list.Count];
        for (var i = 0; i < list.Count; i++)
            ids[i] = list[i].PaneId;
        return ids;
    }

    /// <summary>
    /// idle + unseen presents as done.
    /// </summary>
    public static string StatusName(AgentStatus status, bool seen)
    {
        if (status is AgentStatus.Idle && !seen)
            return "done";
        return status.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// </summary>
    public static int AttentionPriority(AgentStatus status, bool seen) =>
        (status, seen) switch
        {
            (AgentStatus.Blocked, _) => 4,
            (AgentStatus.Idle, false) => 3,
            (AgentStatus.Done, false) => 3,
            (AgentStatus.Working, _) => 2,
            (AgentStatus.Idle, true) => 1,
            (AgentStatus.Done, true) => 1,
            _ => 0,
        };

    private static bool TryNormalizeLabel(string? label, out string? normalized, out string error)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(label))
        {
            error = "";
            return true;
        }

        var cleaned = new string(label.Trim().Where(ch => !char.IsControl(ch)).ToArray());
        if (cleaned.Length == 0 || cleaned.Length > MaxLabelChars)
        {
            error = $"agent view label must be non-empty and at most {MaxLabelChars} characters";
            return false;
        }

        normalized = cleaned;
        error = "";
        return true;
    }

    private static bool TryValidateFilter(JsonElement filter, int depth, ref int nodes, out string error)
    {
        if (depth > MaxFilterDepth)
        {
            error = $"agent view filter may be nested at most {MaxFilterDepth} levels";
            return false;
        }

        nodes++;
        if (nodes > MaxFilterNodes)
        {
            error = $"agent view filter may contain at most {MaxFilterNodes} nodes";
            return false;
        }

        if (filter.ValueKind != JsonValueKind.Object
            || !filter.TryGetProperty("op", out var opEl)
            || opEl.ValueKind != JsonValueKind.String)
        {
            error = "agent view filter requires op";
            return false;
        }

        var op = opEl.GetString();
        switch (op)
        {
            case "all":
            case "any":
                {
                    if (!filter.TryGetProperty("filters", out var filters)
                        || filters.ValueKind != JsonValueKind.Array
                        || filters.GetArrayLength() == 0)
                    {
                        error = "agent view all/any filters must not be empty";
                        return false;
                    }

                    foreach (var child in filters.EnumerateArray())
                    {
                        if (!TryValidateFilter(child, depth + 1, ref nodes, out error))
                            return false;
                    }

                    error = "";
                    return true;
                }
            case "not":
                {
                    if (!filter.TryGetProperty("filter", out var child)
                        || child.ValueKind != JsonValueKind.Object)
                    {
                        error = "agent view not filter requires filter";
                        return false;
                    }

                    return TryValidateFilter(child, depth + 1, ref nodes, out error);
                }
            case "eq":
                {
                    if (!TryReadField(filter, out var field, out error))
                        return false;
                    if (!filter.TryGetProperty("value", out var value))
                    {
                        error = "agent view eq filter requires value";
                        return false;
                    }

                    return TryValidateFieldValue(field, value, out error);
                }
            case "in":
                {
                    if (!TryReadField(filter, out var field, out error))
                        return false;
                    if (!filter.TryGetProperty("values", out var values)
                        || values.ValueKind != JsonValueKind.Array
                        || values.GetArrayLength() == 0
                        || values.GetArrayLength() > MaxFilterValues)
                    {
                        error = $"agent view in filters require 1 to {MaxFilterValues} values";
                        return false;
                    }

                    foreach (var value in values.EnumerateArray())
                    {
                        if (!TryValidateFieldValue(field, value, out error))
                            return false;
                    }

                    error = "";
                    return true;
                }
            case "exists":
                return TryReadField(filter, out _, out error);
            default:
                error = $"unknown agent view filter op `{op}`";
                return false;
        }
    }

    private static bool TryReadField(JsonElement parent, out ViewField field, out string error)
    {
        field = default;
        if (!parent.TryGetProperty("field", out var fieldEl))
        {
            error = "agent view filter requires field";
            return false;
        }

        return TryParseField(fieldEl, out field, out error);
    }

    private static bool TryParseField(JsonElement fieldEl, out ViewField field, out string error)
    {
        field = default;
        if (fieldEl.ValueKind == JsonValueKind.String)
        {
            var name = fieldEl.GetString() ?? "";
            if (!IsBuiltinField(name))
            {
                error = $"unknown agent view field `{name}`";
                return false;
            }

            field = new ViewField(name, Token: null);
            error = "";
            return true;
        }

        if (fieldEl.ValueKind == JsonValueKind.Object
            && fieldEl.TryGetProperty("token", out var tokenEl)
            && tokenEl.ValueKind == JsonValueKind.String)
        {
            var token = tokenEl.GetString() ?? "";
            if (!IsValidToken(token))
            {
                error = $"invalid agent view token `{token}`";
                return false;
            }

            field = new ViewField(Builtin: null, token);
            error = "";
            return true;
        }

        error = "agent view field is invalid";
        return false;
    }

    private static bool TryValidateFieldValue(ViewField field, JsonElement value, out string error)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("context", out var ctxEl)
            && ctxEl.ValueKind == JsonValueKind.String)
        {
            var ctx = ctxEl.GetString();
            if (field.Builtin == "workspace_id" && ctx == "current_workspace_id")
            {
                error = "";
                return true;
            }

            if (field.Builtin == "tab_id" && ctx == "current_tab_id")
            {
                error = "";
                return true;
            }

            error = "agent view context type does not match the selected field";
            return false;
        }

        if (field.Builtin == "seen")
        {
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                error = "";
                return true;
            }

            error = "agent view value type does not match the selected field";
            return false;
        }

        if (field.Builtin == "state_change_seq")
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                error = "";
                return true;
            }

            error = "agent view value type does not match the selected field";
            return false;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            error = "agent view value type does not match the selected field";
            return false;
        }

        if (field.Builtin == "status")
        {
            var status = value.GetString();
            if (status is not ("idle" or "working" or "blocked" or "done" or "unknown"))
            {
                error = $"unknown agent status `{status}`";
                return false;
            }
        }

        error = "";
        return true;
    }

    private static bool TryReadSortField(JsonElement item, out ViewField field, out string error)
    {
        field = default;
        if (item.ValueKind != JsonValueKind.Object)
        {
            error = "agent view sort field is invalid";
            return false;
        }

        if (!item.TryGetProperty("field", out var fieldEl))
        {
            error = "agent view sort requires field";
            return false;
        }

        if (fieldEl.ValueKind == JsonValueKind.String)
        {
            var name = fieldEl.GetString() ?? "";
            if (!IsBuiltinSortField(name))
            {
                error = $"unknown agent view sort field `{name}`";
                return false;
            }

            field = new ViewField(name, Token: null);
            error = "";
            return true;
        }

        return TryParseField(fieldEl, out field, out error);
    }

    private static bool MatchesFilter(AgentViewRow row, JsonElement filter, AgentViewEvalContext context)
    {
        var op = filter.GetProperty("op").GetString();
        switch (op)
        {
            case "all":
                {
                    foreach (var child in filter.GetProperty("filters").EnumerateArray())
                    {
                        if (!MatchesFilter(row, child, context))
                            return false;
                    }

                    return true;
                }
            case "any":
                {
                    foreach (var child in filter.GetProperty("filters").EnumerateArray())
                    {
                        if (MatchesFilter(row, child, context))
                            return true;
                    }

                    return false;
                }
            case "not":
                return !MatchesFilter(row, filter.GetProperty("filter"), context);
            case "eq":
                {
                    if (!TryParseField(filter.GetProperty("field"), out var field, out _))
                        return false;
                    var actual = FieldValue(row, field);
                    var expected = OperandValue(filter.GetProperty("value"), context);
                    return ValuesEqual(actual, expected);
                }
            case "in":
                {
                    if (!TryParseField(filter.GetProperty("field"), out var field, out _))
                        return false;
                    var actual = FieldValue(row, field);
                    foreach (var value in filter.GetProperty("values").EnumerateArray())
                    {
                        if (ValuesEqual(actual, OperandValue(value, context)))
                            return true;
                    }

                    return false;
                }
            case "exists":
                {
                    if (!TryParseField(filter.GetProperty("field"), out var field, out _))
                        return false;
                    return FieldValue(row, field) is not null;
                }
            default:
                return false;
        }
    }

    private static int CompareRows(
        AgentViewRow left,
        AgentViewRow right,
        JsonElement sort,
        AgentViewEvalContext context)
    {
        _ = context;
        foreach (var item in sort.EnumerateArray())
        {
            if (!TryReadSortField(item, out var field, out _))
                continue;
            var descending = item.TryGetProperty("order", out var orderEl)
                && orderEl.ValueKind == JsonValueKind.String
                && string.Equals(orderEl.GetString(), "desc", StringComparison.Ordinal);
            var cmp = CompareOptional(SortValue(left, field), SortValue(right, field), descending);
            if (cmp == 0)
                continue;
            return cmp;
        }

        return 0;
    }

    /// <summary>
    /// lines 270-286. Reverse only when both values exist. Missing stays last.
    /// </summary>
    private static int CompareOptional(EvalValue? left, EvalValue? right, bool descending)
    {
        if (left is { } l && right is { } r)
        {
            var ordering = l.CompareTo(r);
            return descending ? -ordering : ordering;
        }

        if (left is not null)
            return -1;
        if (right is not null)
            return 1;
        return 0;
    }

    /// <summary>
    /// <c>src/app/agent_view.rs</c> lines 64-76.
    /// </summary>
    private static int ComparePriority(AgentViewRow left, AgentViewRow right)
    {
        var att = right.Attention.CompareTo(left.Attention);
        if (att != 0)
            return att;
        return CompareOptional(SeqValue(left), SeqValue(right), descending: true);
    }

    private static EvalValue? SeqValue(AgentViewRow row) =>
        row.StateChangeSeq is ulong seq ? EvalValue.FromNumber(seq) : null;

    private static EvalValue? FieldValue(AgentViewRow row, ViewField field)
    {
        if (field.Token is { } token)
        {
            return row.Tokens.TryGetValue(token, out var value)
                ? EvalValue.FromString(value)
                : null;
        }

        return field.Builtin switch
        {
            "status" => EvalValue.FromString(row.Status),
            "workspace_id" => EvalValue.FromString(row.WorkspaceId),
            "tab_id" => EvalValue.FromString(row.TabId),
            "pane_id" => EvalValue.FromString(row.PaneId),
            "agent" => row.Agent is null ? null : EvalValue.FromString(row.Agent),
            "seen" => EvalValue.FromBool(row.Seen),
            "state_change_seq" => SeqValue(row),
            _ => null,
        };
    }

    private static EvalValue? SortValue(AgentViewRow row, ViewField field)
    {
        if (field.Token is not null)
            return FieldValue(row, field);
        return field.Builtin switch
        {
            "workspace_order" => EvalValue.FromNumber((ulong)row.WorkspaceOrder),
            "tab_order" => EvalValue.FromNumber((ulong)row.TabOrder),
            "pane_order" => EvalValue.FromNumber((ulong)row.PaneOrder),
            "attention" => EvalValue.FromNumber((ulong)row.Attention),
            "status" => EvalValue.FromString(row.Status),
            "agent" => row.Agent is null ? null : EvalValue.FromString(row.Agent),
            "seen" => EvalValue.FromBool(row.Seen),
            "state_change_seq" => SeqValue(row),
            _ => FieldValue(row, field),
        };
    }

    private static EvalValue? OperandValue(JsonElement value, AgentViewEvalContext context)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("context", out var ctxEl)
            && ctxEl.ValueKind == JsonValueKind.String)
        {
            return ctxEl.GetString() switch
            {
                "current_workspace_id" => context.CurrentWorkspaceId is null
                    ? null
                    : EvalValue.FromString(context.CurrentWorkspaceId),
                "current_tab_id" => context.CurrentTabId is null
                    ? null
                    : EvalValue.FromString(context.CurrentTabId),
                _ => null,
            };
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => EvalValue.FromString(value.GetString() ?? ""),
            JsonValueKind.True => EvalValue.FromBool(true),
            JsonValueKind.False => EvalValue.FromBool(false),
            JsonValueKind.Number when value.TryGetUInt64(out var n) => EvalValue.FromNumber(n),
            JsonValueKind.Number when value.TryGetInt64(out var i) && i >= 0 =>
                EvalValue.FromNumber((ulong)i),
            _ => null,
        };
    }

    private static bool ValuesEqual(EvalValue? left, EvalValue? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return left.Value.CompareTo(right.Value) == 0;
    }

    private static bool IsBuiltinField(string name) =>
        name is "status" or "workspace_id" or "tab_id" or "pane_id" or "agent" or "seen"
            or "state_change_seq";

    private static bool IsBuiltinSortField(string name) =>
        IsBuiltinField(name)
        || name is "workspace_order" or "tab_order" or "pane_order" or "attention";

    private static bool IsValidToken(string token) =>
        token.Length is > 0 and <= 32
        && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    private static JsonElement CloneElement(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : element.Clone();

    private readonly record struct ViewField(string? Builtin, string? Token);

    private readonly struct EvalValue : IComparable<EvalValue>
    {
        private readonly byte _kind;
        private readonly string? _text;
        private readonly bool _flag;
        private readonly ulong _number;

        private EvalValue(byte kind, string? text, bool flag, ulong number)
        {
            _kind = kind;
            _text = text;
            _flag = flag;
            _number = number;
        }

        public static EvalValue FromString(string value) => new(0, value, false, 0);
        public static EvalValue FromBool(bool value) => new(1, null, value, 0);
        public static EvalValue FromNumber(ulong value) => new(2, null, false, value);

        public int CompareTo(EvalValue other)
        {
            var kind = _kind.CompareTo(other._kind);
            if (kind != 0)
                return kind;
            return _kind switch
            {
                0 => string.Compare(_text, other._text, StringComparison.Ordinal),
                1 => _flag.CompareTo(other._flag),
                _ => _number.CompareTo(other._number),
            };
        }
    }
}
