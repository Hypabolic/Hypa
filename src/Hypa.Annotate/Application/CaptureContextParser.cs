using System.Text.Json;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Parse plugin invocation context. The host omits <c>selected_text</c>.
/// </summary>
public static class CaptureContextParser
{
    public static CaptureContext Parse(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return new CaptureContext();

        try
        {
            using var document = JsonDocument.Parse(contextJson);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return new CaptureContext();
        }
    }

    public static CaptureContext Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return new CaptureContext();

        return new CaptureContext
        {
            WorkspaceId = OptionalString(value, "workspace_id"),
            WorkspaceLabel = OptionalString(value, "workspace_label"),
            TabId = OptionalString(value, "tab_id"),
            TabLabel = OptionalString(value, "tab_label"),
            FocusedPaneId = OptionalString(value, "focused_pane_id"),
            FocusedPaneCwd = OptionalString(value, "focused_pane_cwd"),
            FocusedPaneAgent = OptionalString(value, "focused_pane_agent"),
        };
    }

    private static string? OptionalString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
