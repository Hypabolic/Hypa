namespace Hypa.Annotate.Application;

/// <summary>
/// Plugin toast through <c>HYPA_BIN_PATH</c>. Source is <c>plugin:&lt;id&gt;</c>.
/// </summary>
public static class HypaBinNotifier
{
    public static IReadOnlyList<string> BuildShowArguments(string title, string? body, string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        var arguments = new List<string>
        {
            "notification",
            "show",
            "--title",
            title,
            "--source",
            "plugin:" + pluginId,
        };
        if (!string.IsNullOrEmpty(body))
        {
            arguments.Add("--body");
            arguments.Add(body);
        }

        return arguments;
    }
}
