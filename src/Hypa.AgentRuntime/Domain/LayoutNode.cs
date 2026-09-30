using System.Globalization;
using System.Text.Json.Nodes;

namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Immutable BSP layout tree. Wire and persist shape is portable JSON
/// (<c>type</c> = <c>pane</c>|<c>split</c>). Domain has no Sqlite or file IO.
/// </summary>
public abstract record LayoutNode
{
    public const string TypePane = "pane";
    public const string TypeSplit = "split";
    public const string DirectionRight = "right";
    public const string DirectionDown = "down";
    public const string DirectionLeft = "left";
    public const string DirectionUp = "up";

    public abstract string Type { get; }

    /// <summary>
    /// Canonical JSON object. Ratio uses invariant culture, max 6 fraction digits,
    /// no scientific notation. Child order is first then second.
    /// </summary>
    public abstract JsonObject ToJsonObject(bool includePaneId);

    public string ToCanonicalJson(bool includePaneId = false) =>
        ToJsonObject(includePaneId).ToJsonString();

    public static bool IsSplitDirection(string? direction) =>
        direction is DirectionRight or DirectionDown;

    public static bool IsFocusDirection(string? direction) =>
        direction is DirectionRight or DirectionDown or DirectionLeft or DirectionUp;

    public static bool IsValidRatio(double ratio) => ratio > 0 && ratio < 1;

    public static string FormatRatio(double ratio)
    {
        var rounded = Math.Round(ratio, 6, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.######", CultureInfo.InvariantCulture);
    }

    public static JsonNode RatioNode(double ratio)
    {
        var text = FormatRatio(ratio);
        return JsonValue.Create(double.Parse(text, CultureInfo.InvariantCulture))!;
    }

    public static LayoutNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var node = JsonNode.Parse(json);
            return node is JsonObject obj ? FromJsonObject(obj) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static LayoutNode? FromJsonObject(JsonObject? obj)
    {
        if (obj is null)
            return null;
        var type = obj["type"]?.GetValue<string>();
        if (type == TypeSplit)
        {
            var direction = obj["direction"]?.GetValue<string>() ?? DirectionRight;
            var ratio = ReadRatio(obj["ratio"]) ?? 0.5;
            var first = obj["first"] as JsonObject;
            var second = obj["second"] as JsonObject;
            if (first is null || second is null)
                return null;
            var firstNode = FromJsonObject(first);
            var secondNode = FromJsonObject(second);
            if (firstNode is null || secondNode is null)
                return null;
            return new LayoutSplitNode
            {
                Direction = direction,
                Ratio = ratio,
                First = firstNode,
                Second = secondNode,
            };
        }

        if (type == TypePane || type is null)
        {
            var paneId = obj["pane_id"]?.GetValue<string>();
            var command = ReadCommand(obj["command"]);
            return new LayoutPaneNode
            {
                PaneId = string.IsNullOrWhiteSpace(paneId) ? null : new PaneId(paneId),
                Label = obj["label"]?.GetValue<string>() ?? string.Empty,
                Cwd = obj["cwd"]?.GetValue<string>() ?? string.Empty,
                Command = command,
            };
        }

        return null;
    }

    private static double? ReadRatio(JsonNode? node)
    {
        if (node is null)
            return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d))
                return d;
            if (v.TryGetValue<string>(out var s)
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadCommand(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            var list = new List<string>(arr.Count);
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
                    list.Add(s);
            }

            return list;
        }

        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var one) && !string.IsNullOrEmpty(one))
            return [one];
        return [];
    }
}

public sealed record LayoutPaneNode : LayoutNode
{
    public override string Type => TypePane;

    public PaneId? PaneId { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Cwd { get; init; } = string.Empty;
    public IReadOnlyList<string> Command { get; init; } = [];

    public override JsonObject ToJsonObject(bool includePaneId)
    {
        var obj = new JsonObject { ["type"] = TypePane };
        if (includePaneId && PaneId is { } id)
            obj["pane_id"] = id.Value;
        if (!string.IsNullOrEmpty(Label))
            obj["label"] = Label;
        if (!string.IsNullOrEmpty(Cwd))
            obj["cwd"] = Cwd;
        if (Command.Count > 0)
        {
            JsonNode?[] nodes = new JsonNode?[Command.Count];
            for (var i = 0; i < Command.Count; i++)
                nodes[i] = JsonValue.Create(Command[i]);
            obj["command"] = new JsonArray(nodes);
        }

        return obj;
    }
}

public sealed record LayoutSplitNode : LayoutNode
{
    public override string Type => TypeSplit;

    public required string Direction { get; init; }
    public required double Ratio { get; init; }
    public required LayoutNode First { get; init; }
    public required LayoutNode Second { get; init; }

    public override JsonObject ToJsonObject(bool includePaneId) => new()
    {
        ["type"] = TypeSplit,
        ["direction"] = Direction,
        ["ratio"] = RatioNode(Ratio),
        ["first"] = First.ToJsonObject(includePaneId),
        ["second"] = Second.ToJsonObject(includePaneId),
    };
}
