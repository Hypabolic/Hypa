using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.Cli.Attach.Chrome;

public sealed record TabBarRightItem(
    TabBarRightKind Kind,
    string Text,
    int Width);

/// <summary>Renders <c>ui.tab_bar_right</c>. Command entries are display-only.</summary>
public static class TabBarRightRenderer
{
    public static IReadOnlyList<TabBarRightItem> Render(
        IReadOnlyList<AttachTabBarRightEntry> entries,
        TimeProvider? time = null,
        string? hostname = null,
        string? commandCache = null,
        IReadOnlyDictionary<string, string>? commandOutputs = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var clock = time ?? TimeProvider.System;
        var host = SafeDisplayText.Encode(hostname ?? Environment.MachineName);
        var list = new List<TabBarRightItem>(entries.Count);
        foreach (var entry in entries)
        {
            var text = entry.Kind switch
            {
                TabBarRightKind.Zoom => "Z",
                TabBarRightKind.Hostname => host,
                TabBarRightKind.DateTime => SafeDisplayText.Encode(FormatDateTime(clock, entry.Format)),
                TabBarRightKind.Text => SafeDisplayText.Encode(entry.Text),
                TabBarRightKind.Command => SafeDisplayText.Encode(
                    ResolveCommand(entry, commandCache, commandOutputs)),
                TabBarRightKind.Resource => "",
                _ => "",
            };
            list.Add(new TabBarRightItem(entry.Kind, text, SafeDisplayText.Width(text)));
        }

        return list;
    }

    public static string Join(IReadOnlyList<TabBarRightItem> items, string? separator)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sep = SafeDisplayText.Encode(separator ?? " ");
        if (sep.Length == 0)
            sep = " ";
        return string.Join(sep, items.Select(i => i.Text));
    }

    private static string ResolveCommand(
        AttachTabBarRightEntry entry,
        string? commandCache,
        IReadOnlyDictionary<string, string>? commandOutputs)
    {
        if (commandOutputs is not null
            && entry.Command is { Length: > 0 } command
            && commandOutputs.TryGetValue(command, out var text))
        {
            return text;
        }

        return commandCache ?? "";
    }

    private static string FormatDateTime(TimeProvider time, string? format)
    {
        var local = time.GetLocalNow().DateTime;
        return StrftimeClock.Format(local, format);
    }
}
