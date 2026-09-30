namespace Hypa.Cli.Attach.Settings;

public enum SettingsPageKind
{
    Theme = 0,
    Indicators,
    Sound,
    Toasts,
    PaneLabels,
    Integrations,
    Hosted,
    Startup,
    ReleaseNotes,
}

public sealed record SettingsPage(string Id, string Label, SettingsPageKind Kind, string? PluginId = null);

/// <summary>
/// Paint, cycle, and hit-test iterate <see cref="Pages"/>.
/// </summary>
public sealed class SettingsPageRegistry
{
    public const string ThemeId = "theme";
    public const string IndicatorsId = "indicators";
    public const string SoundId = "sound";
    public const string ToastsId = "toasts";
    public const string PaneLabelsId = "pane_labels";
    public const string IntegrationsId = "integrations";
    public const string StartupId = "startup";
    public const string ReleaseNotesId = "release_notes";

    public const string ThemeLabel = "theme";
    public const string IndicatorsLabel = "indicators";
    public const string SoundLabel = "sound";
    public const string ToastsLabel = "toasts";
    public const string PaneLabelsLabel = "pane labels";
    public const string IntegrationsLabel = "integrations";
    public const string StartupLabel = "startup";
    public const string ReleaseNotesLabel = "release notes";

    private readonly SettingsPage[] _pages;

    private SettingsPageRegistry(SettingsPage[] pages) => _pages = pages;

    public IReadOnlyList<SettingsPage> Pages => _pages;

    public static SettingsPageRegistry Core() =>
        new(
        [
            new SettingsPage(ThemeId, ThemeLabel, SettingsPageKind.Theme),
            new SettingsPage(IndicatorsId, IndicatorsLabel, SettingsPageKind.Indicators),
            new SettingsPage(SoundId, SoundLabel, SettingsPageKind.Sound),
            new SettingsPage(ToastsId, ToastsLabel, SettingsPageKind.Toasts),
            new SettingsPage(PaneLabelsId, PaneLabelsLabel, SettingsPageKind.PaneLabels),
            new SettingsPage(IntegrationsId, IntegrationsLabel, SettingsPageKind.Integrations),
        ]);

    public static SettingsPageRegistry Product() =>
        Core()
            .WithExtra(new SettingsPage(ReleaseNotesId, ReleaseNotesLabel, SettingsPageKind.ReleaseNotes))
            .WithExtra(new SettingsPage(StartupId, StartupLabel, SettingsPageKind.Startup));

    public static SettingsPageRegistry ProductWithPlugins(IReadOnlyList<PluginSettingsPageView> pluginPages)
    {
        if (pluginPages is null || pluginPages.Count == 0)
            return Product();
        var pages = new SettingsPage[pluginPages.Count];
        for (var i = 0; i < pluginPages.Count; i++)
        {
            var plugin = pluginPages[i];
            pages[i] = new SettingsPage(
                "plugin:" + plugin.PluginId,
                plugin.Name,
                SettingsPageKind.Hosted,
                plugin.PluginId);
        }

        return Product().WithPluginPages(pages);
    }

    public SettingsPageRegistry WithExtra(SettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var next = new SettingsPage[_pages.Length + 1];
        Array.Copy(_pages, next, _pages.Length);
        next[_pages.Length] = page;
        return new SettingsPageRegistry(next);
    }

    public SettingsPageRegistry WithPluginPages(IReadOnlyList<SettingsPage> pluginPages)
    {
        if (pluginPages is null || pluginPages.Count == 0)
            return this;
        var next = new SettingsPage[_pages.Length + pluginPages.Count];
        Array.Copy(_pages, next, _pages.Length);
        for (var i = 0; i < pluginPages.Count; i++)
            next[_pages.Length + i] = pluginPages[i];
        return new SettingsPageRegistry(next);
    }

    public bool TryGet(string id, out SettingsPage page)
    {
        foreach (var item in _pages)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                page = item;
                return true;
            }
        }

        page = _pages[0];
        return false;
    }

    public int IndexOf(string id)
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            if (string.Equals(_pages[i].Id, id, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }
}
