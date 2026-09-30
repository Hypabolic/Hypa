using System.Text;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application.Status;

/// <summary>
/// Binds <c>ui.tab_bar_right</c> type <c>resource</c> into status chip extras.
/// A missing resource hides the chip. Core chips stay first in
/// <see cref="StatusChipComposer"/>.
/// </summary>
public static class StatusResourceChipComposer
{
    public const int DefaultMaxItems = 1;
    public const int MaxItemsCap = 200;

    public static IReadOnlyList<StatusChip> Compose(
        IReadOnlyList<AttachTabBarRightEntry> entries,
        IReadOnlyList<PluginResourceDto>? resources)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return [];

        var lookup = BuildLookup(resources);
        var chips = new List<StatusChip>();
        foreach (var entry in entries)
        {
            if (entry.Kind is not TabBarRightKind.Resource)
                continue;
            var text = FormatEntry(entry, lookup);
            if (text.Length == 0)
                continue;
            chips.Add(new StatusChip
            {
                Kind = StatusChipKind.Resource,
                Text = text,
                DisplayWidth = SafeDisplayText.Width(text),
            });
        }

        return chips;
    }

    internal static string FormatEntry(
        AttachTabBarRightEntry entry,
        IReadOnlyDictionary<string, PluginResourceDto> lookup)
    {
        var resourceId = SafeDisplayText.Encode(entry.Resource);
        if (resourceId.Length == 0)
            return "";
        if (!lookup.TryGetValue(resourceId, out var resource))
            return "";
        if (string.Equals(resource.Freshness, "unavailable", StringComparison.OrdinalIgnoreCase))
            return "";

        var value = resource.Value;
        if (value is null)
            return "";

        var format = string.IsNullOrWhiteSpace(entry.Format)
            ? SafeDisplayText.Encode(value.Summary)
            : entry.Format.Trim();
        if (format.Length == 0)
            return "";

        var maxItems = Math.Clamp(entry.MaxItems ?? DefaultMaxItems, 1, MaxItemsCap);
        var tokens = BuildTokens(value, maxItems);
        var rendered = RenderFormat(format, tokens);
        return SafeDisplayText.Encode(rendered);
    }

    internal static IReadOnlyDictionary<string, string> BuildTokens(
        PluginCollectionValueDto value,
        int maxItems)
    {
        var items = SelectItems(value.Items, maxItems);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attention_count"] = items.Sum(i => i.Attention).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["item_count"] = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var summary = SafeDisplayText.Encode(value.Summary);
        if (summary.Length > 0)
            tokens["summary"] = summary;

        foreach (var item in items)
        {
            if (item.Tokens is null)
                continue;
            foreach (var pair in item.Tokens)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;
                var encoded = SafeDisplayText.Encode(pair.Value);
                if (encoded.Length == 0)
                    continue;
                tokens.TryAdd(pair.Key, encoded);
            }
        }

        return tokens;
    }

    internal static string RenderFormat(string format, IReadOnlyDictionary<string, string> tokens)
    {
        if (format.Length == 0)
            return "";

        var output = new StringBuilder(format.Length);
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '$')
            {
                output.Append(format[i]);
                continue;
            }

            if (i + 1 >= format.Length)
            {
                output.Append('$');
                break;
            }

            var start = i + 1;
            var end = start;
            while (end < format.Length)
            {
                var ch = format[end];
                if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
                {
                    end++;
                    continue;
                }

                break;
            }

            if (end == start)
            {
                output.Append('$');
                continue;
            }

            var name = format[start..end];
            if (tokens.TryGetValue(name, out var text))
                output.Append(text);
            i = end - 1;
        }

        return output.ToString().Trim();
    }

    private static IReadOnlyList<PluginCollectionItemDto> SelectItems(
        IReadOnlyList<PluginCollectionItemDto>? items,
        int maxItems)
    {
        if (items is null || items.Count == 0)
            return [];

        return items
            .OrderByDescending(i => i.Attention)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .Take(maxItems)
            .ToArray();
    }

    private static Dictionary<string, PluginResourceDto> BuildLookup(IReadOnlyList<PluginResourceDto>? resources)
    {
        var lookup = new Dictionary<string, PluginResourceDto>(StringComparer.Ordinal);
        if (resources is null)
            return lookup;
        foreach (var resource in resources)
        {
            var id = SafeDisplayText.Encode(resource.ResourceId);
            if (id.Length == 0)
                continue;
            lookup[id] = resource;
        }

        return lookup;
    }
}
