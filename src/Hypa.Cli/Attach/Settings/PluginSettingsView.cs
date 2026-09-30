namespace Hypa.Cli.Attach.Settings;

public sealed record PluginSettingsFieldView(
    string Key,
    string Type,
    string Title,
    IReadOnlyList<string> Choices,
    string Value);

public sealed record PluginSettingsPageView(
    string PluginId,
    string Name,
    IReadOnlyList<PluginSettingsFieldView> Fields);

public static class PluginSettingsFieldControl
{
    public static IReadOnlyList<SettingsListItem> ItemsFor(PluginSettingsPageView page)
    {
        var list = new SettingsListItem[page.Fields.Count];
        for (var i = 0; i < page.Fields.Count; i++)
        {
            var field = page.Fields[i];
            list[i] = new SettingsListItem(field.Key, FormatLabel(field));
        }

        return list;
    }

    public static string FormatLabel(PluginSettingsFieldView field) =>
        field.Title + ": " + DisplayValue(field);

    public static string DisplayValue(PluginSettingsFieldView field) =>
        field.Type switch
        {
            "boolean" => field.Value == "true" ? "on" : "off",
            _ => string.IsNullOrEmpty(field.Value) ? "(empty)" : field.Value,
        };

    public static string NextValue(PluginSettingsFieldView field)
    {
        switch (field.Type)
        {
            case "boolean":
                return field.Value == "true" ? "false" : "true";
            case "choice":
                if (field.Choices.Count == 0)
                    return field.Value;
                var index = 0;
                for (var i = 0; i < field.Choices.Count; i++)
                {
                    if (string.Equals(field.Choices[i], field.Value, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }

                return field.Choices[(index + 1) % field.Choices.Count];
            case "integer":
                if (!int.TryParse(field.Value, out var number))
                    number = 0;
                return (number + 1).ToString();
            default:
                return field.Value switch
                {
                    "" => "value",
                    "value" => "",
                    _ => "",
                };
        }
    }
}
