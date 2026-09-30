using System.Text.Json;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

internal static class AnnotationParser
{
    /// <summary>
    /// Match ECMAScript <c>String.prototype.trim</c> for blank-value checks.
    /// </summary>
    internal static string JavascriptTrim(string value)
    {
        var start = 0;
        while (start < value.Length && IsJavascriptTrimChar(value[start]))
            start++;

        var end = value.Length;
        while (end > start && IsJavascriptTrimChar(value[end - 1]))
            end--;

        return value[start..end];
    }

    private static bool IsJavascriptTrimChar(char character) =>
        character == '\uFEFF' || char.IsWhiteSpace(character);

    internal static bool TryParseAnnotation(JsonElement value, out Annotation? annotation)
    {
        annotation = null;
        if (value.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryReadString(value, "selectedText", out var selectedText))
            return false;
        if (!TryReadString(value, "capturedAt", out var capturedAt))
            return false;
        if (!TryReadString(value, "id", out var id) || id.Length == 0)
            return false;
        if (!TryReadString(value, "comment", out var comment) || JavascriptTrim(comment).Length == 0)
            return false;
        if (!TryReadString(value, "createdAt", out var createdAt) || createdAt.Length == 0)
            return false;

        var context = value.TryGetProperty("context", out var contextElement)
            ? ParseCaptureContext(contextElement)
            : new CaptureContext();

        annotation = new Annotation
        {
            SelectedText = selectedText,
            CapturedAt = capturedAt,
            Context = context,
            Id = id,
            Comment = comment,
            CreatedAt = createdAt,
        };
        return true;
    }

    internal static bool TryParseArchivedSet(JsonElement value, out ArchivedAnnotationSet? archive)
    {
        archive = null;
        if (value.ValueKind != JsonValueKind.Object)
            return false;

        if (!value.TryGetProperty("version", out var versionElement)
            || !TryReadVersionOne(versionElement))
            return false;

        if (!TryReadString(value, "id", out var id) || id.Length == 0)
            return false;
        if (!TryReadString(value, "archivedAt", out var archivedAt) || archivedAt.Length == 0)
            return false;
        if (!value.TryGetProperty("annotations", out var annotationsElement)
            || annotationsElement.ValueKind != JsonValueKind.Array)
            return false;

        var annotations = new List<Annotation>();
        foreach (var item in annotationsElement.EnumerateArray())
        {
            if (!TryParseAnnotation(item, out var parsed) || parsed is null)
                return false;
            annotations.Add(parsed);
        }

        if (annotations.Count == 0)
            return false;

        archive = new ArchivedAnnotationSet
        {
            Version = 1,
            Id = id,
            ArchivedAt = archivedAt,
            Annotations = annotations.ToArray(),
        };
        return true;
    }

    private static CaptureContext ParseCaptureContext(JsonElement value)
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

    private static bool TryReadVersionOne(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var number)
            && number == 1.0;
    }

    private static bool TryReadString(JsonElement value, string propertyName, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        result = property.GetString() ?? string.Empty;
        return true;
    }

    private static string? OptionalString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
