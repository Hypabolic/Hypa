using System.Text.Json;

namespace Hypa.Annotate.Application;

/// <summary>
/// Parse <c>agent_session</c> from plugin invocation context JSON.
/// </summary>
public static class AgentSessionContextParser
{
    public const string KindPath = "path";
    public const string KindId = "id";

    public static AgentSessionContext? Parse(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(contextJson);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AgentSessionContext? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("agent_session", out var session)
            || session.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var kind = OptionalString(session, "kind");
        var sessionValue = OptionalString(session, "value");
        var agent = OptionalString(session, "agent");
        if (string.IsNullOrWhiteSpace(kind)
            || string.IsNullOrWhiteSpace(sessionValue)
            || string.IsNullOrWhiteSpace(agent))
        {
            return null;
        }

        return new AgentSessionContext
        {
            Kind = kind,
            Value = sessionValue,
            Agent = agent,
            Source = OptionalString(session, "source"),
        };
    }

    private static string? OptionalString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record AgentSessionContext
{
    public required string Kind { get; init; }

    public required string Value { get; init; }

    public required string Agent { get; init; }

    public string? Source { get; init; }
}
