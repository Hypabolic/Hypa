namespace Hypa.Cli.Attach.Settings;

public enum SettingsHitKind
{
    Tab = 0,
    Item,
    Selection,
    Apply,
    Close,
    Ignore,
    Outside,
}

public sealed record SettingsHit(
    SettingsHitKind Kind,
    string? PageId = null,
    int? Index = null);

public static class SettingsHitTest
{
    public static SettingsHit Hit(SettingsLayout? layout, int col, int row)
    {
        if (layout is null)
            return new SettingsHit(SettingsHitKind.Outside);

        foreach (var tab in layout.Tabs)
        {
            if (tab.Rect.Contains(col, row))
                return new SettingsHit(SettingsHitKind.Tab, tab.Id);
        }

        foreach (var item in layout.Items)
        {
            if (item.Box is { } box && box.Contains(col, row))
                return new SettingsHit(SettingsHitKind.Selection, Index: item.Index);
            if (item.Rect.Contains(col, row))
                return new SettingsHit(SettingsHitKind.Item, Index: item.Index);
        }

        if (layout.Apply.Contains(col, row))
            return new SettingsHit(SettingsHitKind.Apply);

        if (layout.Close.Contains(col, row))
            return new SettingsHit(SettingsHitKind.Close);

        if (!layout.Panel.Contains(col, row))
            return new SettingsHit(SettingsHitKind.Outside);

        return new SettingsHit(SettingsHitKind.Ignore);
    }
}
