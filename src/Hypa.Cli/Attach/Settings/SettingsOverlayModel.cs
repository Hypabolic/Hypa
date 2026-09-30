using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Settings;

public sealed record SettingsListItem(string Id, string Label);

public sealed record PendingPluginConfigWrite(string PluginId, string Key, string Value);

public enum IntegrationRowResultKind
{
    Installed,
    Unchanged,
    Failed,
}

public readonly record struct IntegrationRowResult(IntegrationRowResultKind Kind, string Detail);

public sealed class SettingsOverlayModel
{
    public const string IntegrationsTitle = "agent integrations";
    public const string IntegrationsDescription =
        "let agents report state directly instead of relying only on process detection";
    public const string IntegrationsLoading = " loading integrations…";
    public const string IntegrationsEmpty = " no integration targets available";
    public const string IntegrationsInstalling = " installing…";
    public const int IntegrationRetryLimit = 3;
    public const string IntegrationsFooter = "space toggle  u remove";
    public const string FilterUninstall = "uninstall";
    public const string ReleaseNotesTitle = "pack notes";
    public const string ReleaseNotesDescription = "Notes ship with the installed channel.";
    public const string ReleaseNotesFooter = "enter open";
    public const string FilterPage = "page";
    public const string FilterPreview = "preview";
    public const string FilterApply = "apply";

    private ThemeRuntime _theme = ThemeRuntime.Default;
    private AttachUiConfig _ui = AttachUiConfig.Default;
    private ThemeSnapshot? _snapshot;
    private int _pageIndex;
    private int _itemIndex;
    private IReadOnlyList<OfficialIntegrationStatus> _integrations = [];
    private IReadOnlyList<PluginSettingsPageView> _pluginPages = [];

    /// <summary>
    // Pass
    /// <see cref="SettingsPageRegistry.Product"/> for Hypa extra pages.
    /// </summary>
    public SettingsOverlayModel(SettingsPageRegistry? registry = null)
    {
        Registry = registry ?? SettingsPageRegistry.Core();
    }

    public SettingsPageRegistry Registry { get; private set; }

    public IReadOnlyList<SettingsPage> Pages => Registry.Pages;

    public bool IsOpen { get; private set; }

    public bool PreviewDirty { get; private set; }

    public SettingsPage ActivePage =>
        Pages.Count == 0 ? new SettingsPage("theme", "theme", SettingsPageKind.Theme) : Pages[_pageIndex];

    public string ActivePageId => ActivePage.Id;

    public bool ShowsApply =>
        ActivePage.Kind switch
        {
            SettingsPageKind.Integrations => HasInstallableIntegrations,
            SettingsPageKind.Hosted => ActivePluginPage() is { Fields.Count: > 0 },
            SettingsPageKind.ReleaseNotes => true,
            SettingsPageKind.Theme => false,
            _ => true,
        };

    public int ItemIndex => _itemIndex;

    public int ListOffset { get; private set; }

    /// <summary>
    /// Rows in the integrations body that scroll.
    /// The count includes install lines and skill lines under the rows.
    /// </summary>
    private int _integrationScrollSpan;

    private int _integrationVisibleSlots;

    public SettingsLayout? Layout { get; set; }

    public IReadOnlyList<AttachConfigAssignment>? PendingPatch { get; private set; }

    public bool PendingCloseAfterPatch { get; private set; }

    public bool PendingOpenReleaseNotes { get; private set; }

    public IReadOnlyList<OfficialIntegrationTarget>? PendingInstallTargets { get; private set; }

    public OfficialIntegrationTarget? PendingUninstallTarget { get; private set; }

    public IReadOnlyList<OfficialIntegrationStatus> Integrations => _integrations;

    public IReadOnlyList<PluginSettingsPageView> PluginPages => _pluginPages;

    public PendingPluginConfigWrite? PendingPluginConfigWrite { get; private set; }

    public IReadOnlyList<string> IntegrationMessages { get; private set; } = [];

    public bool LoadingIntegrations { get; private set; }

    public bool InstallingIntegrations { get; private set; }

    public string? IntegrationLoadError { get; private set; }

    public bool IntegrationRetryArmed { get; private set; }

    public int IntegrationRetryAttempts { get; private set; }

    private int _integrationLoadToken;

    private string? _integrationsEndpointId;

    private (ulong Source, ulong Destination) _integrationsGeneration;

    private bool _integrationsHaveEndpoint;

    private bool _remoteMuxHost;

    private string _muxHostName = "";

    private bool _selectionSeeded;

    private readonly HashSet<OfficialIntegrationTarget> _selected = [];

    private readonly Dictionary<OfficialIntegrationTarget, IntegrationRowResult> _results = [];

    public string? IntegrationSummary { get; private set; }

    public bool HasOutdatedIntegration
    {
        get
        {
            foreach (var status in _integrations)
            {
                if (status.State is OfficialIntegrationStatusKind.Outdated
                    || status.SkillState is OfficialIntegrationSkillState.Outdated)
                    return true;
            }

            return false;
        }
    }

    public bool HasInstallableIntegrations
    {
        get
        {
            foreach (var status in _integrations)
            {
                if (status.NeedsInstall())
                    return true;
            }

            return false;
        }
    }

    public string IntegrationsHint
    {
        get
        {
            if (ActivePage.Kind is SettingsPageKind.ReleaseNotes)
                return ReleaseNotesFooter;
            if (InstallingIntegrations)
                return "installing…";
            if (LoadingIntegrations)
                return "loading…";
            if (_integrations.Count > 0)
                return IntegrationsFooter;
            return "tab section";
        }
    }

    public void Bind(ThemeRuntime theme, AttachUiConfig ui)
    {
        _theme = theme ?? ThemeRuntime.Default;
        _ui = ui ?? AttachUiConfig.Default;
    }

    public void Open(string? pageId = null)
    {
        if (IsOpen)
            return;
        OpenAt(pageId ?? SettingsPageRegistry.ThemeId);
    }

    public void OpenAt(string pageId)
    {
        if (!IsOpen)
        {
            IsOpen = true;
            PreviewDirty = false;
            PendingPatch = null;
            PendingCloseAfterPatch = false;
            PendingOpenReleaseNotes = false;
            PendingInstallTargets = null;
            PendingUninstallTarget = null;
            ListOffset = 0;
            _snapshot = _theme.Capture();
        }

        SelectPage(pageId, seed: true);
        Layout = SettingsPainter.Measure(this, 80, 24);
    }

    public bool SelectPage(string pageId, bool seed = true)
    {
        if (Pages.Count == 0)
            return false;
        var index = Registry.IndexOf(pageId);
        if (!Registry.TryGet(pageId, out _) && index == 0
            && !string.Equals(Pages[0].Id, pageId, StringComparison.Ordinal))
        {
            index = 0;
        }

        var changed = index != _pageIndex || !IsOpen;
        SetPageIndex(Math.Clamp(index, 0, Pages.Count - 1));
        ListOffset = 0;
        if (seed)
            _itemIndex = SeedIndex(ActivePage);
        else
            _itemIndex = Math.Clamp(_itemIndex, 0, Math.Max(0, Items.Count - 1));
        return changed;
    }

    public bool SelectNext()
    {
        if (Pages.Count == 0)
            return false;
        var next = (_pageIndex + 1) % Pages.Count;
        SetPageIndex(next);
        ListOffset = 0;
        _itemIndex = SeedIndex(ActivePage);
        return true;
    }

    public bool SelectPrev()
    {
        if (Pages.Count == 0)
            return false;
        var prev = (_pageIndex - 1 + Pages.Count) % Pages.Count;
        SetPageIndex(prev);
        ListOffset = 0;
        _itemIndex = SeedIndex(ActivePage);
        return true;
    }

    /// <summary>
    /// Leaving the integrations page ends that visit. A list from it must not
    /// commit or count as current for the next visit.
    /// </summary>
    private void SetPageIndex(int index)
    {
        if (index != _pageIndex && ActivePage.Kind is SettingsPageKind.Integrations)
            _integrationLoadToken++;
        _pageIndex = index;
    }

    public IReadOnlyList<SettingsListItem> Items => ItemsFor(ActivePage);

    public bool MoveList(int delta)
    {
        var items = Items;
        if (items.Count == 0 || delta == 0)
            return false;
        if (ScrollsIntegrationTail(_integrationVisibleSlots))
        {
            var maxOffset = _integrationScrollSpan - _integrationVisibleSlots;
            var next = ListOffset + delta;
            if (next < 0)
                next = maxOffset;
            else if (next > maxOffset)
                next = 0;
            if (next == ListOffset)
                return false;
            ListOffset = next;
            PlaceIntegrationSelection(_integrationVisibleSlots);
            return true;
        }

        var nextIndex = _itemIndex + delta;
        if (nextIndex < 0)
            nextIndex = items.Count - 1;
        else if (nextIndex >= items.Count)
            nextIndex = 0;
        if (nextIndex == _itemIndex)
            return false;
        _itemIndex = nextIndex;
        if (ActivePage.Kind is SettingsPageKind.Theme)
            PreviewTheme(items[_itemIndex].Id);
        return true;
    }

    public bool SelectItem(int index)
    {
        var items = Items;
        if (index < 0 || index >= items.Count)
            return false;
        if (index == _itemIndex && ActivePage.Kind is not SettingsPageKind.Theme)
            return true;
        _itemIndex = index;
        if (ActivePage.Kind is SettingsPageKind.Theme)
            PreviewTheme(items[index].Id);
        return true;
    }

    public void BindPluginPages(IReadOnlyList<PluginSettingsPageView> pages, SettingsPageRegistry registry)
    {
        _pluginPages = pages ?? [];
        Registry = registry;
        if (IsOpen)
            _itemIndex = Math.Clamp(_itemIndex, 0, Math.Max(0, Items.Count - 1));
    }

    public bool Apply()
    {
        PendingPatch = null;
        PendingCloseAfterPatch = false;
        PendingInstallTargets = null;
        PendingPluginConfigWrite = null;
        PendingOpenReleaseNotes = false;
        var page = ActivePage;
        if (page.Kind is SettingsPageKind.Hosted)
            return ApplyHosted(page);
        if (page.Kind is SettingsPageKind.ReleaseNotes)
        {
            PendingOpenReleaseNotes = true;
            return true;
        }

        if (page.Kind is SettingsPageKind.Integrations)
        {
            var targets = new List<OfficialIntegrationTarget>();
            foreach (var status in _integrations)
            {
                if (_selected.Contains(status.Target))
                    targets.Add(status.Target);
            }

            if (targets.Count == 0)
                return false;
            PendingInstallTargets = targets;
            PendingUninstallTarget = null;
            InstallingIntegrations = true;
            IntegrationMessages = [];
            return true;
        }

        var items = Items;
        if (items.Count == 0)
            return false;
        var index = Math.Clamp(_itemIndex, 0, items.Count - 1);
        var choice = items[index];

        switch (page.Kind)
        {
            case SettingsPageKind.Theme:
                PreviewTheme(choice.Id);
                QueueTheme(choice.Id);
                return true;
            case SettingsPageKind.Indicators:
                PendingPatch =
                [
                    new AttachConfigAssignment(
                        "ui.status_indicators",
                        choice.Id == "symbols" ? "\"symbols\"" : "\"dots\""),
                ];
                return true;
            case SettingsPageKind.Sound:
                PendingPatch =
                [
                    new AttachConfigAssignment(
                        "ui.sound.enabled",
                        choice.Id == "on" ? "true" : "false"),
                ];
                return true;
            case SettingsPageKind.Toasts:
                PendingPatch =
                [
                    new AttachConfigAssignment("ui.toast.delivery", Quote(choice.Id)),
                ];
                return true;
            case SettingsPageKind.PaneLabels:
                PendingPatch =
                [
                    new AttachConfigAssignment(
                        "ui.show_agent_labels_on_pane_borders",
                        choice.Id == "on" ? "true" : "false"),
                ];
                return true;
            case SettingsPageKind.Startup:
                PendingPatch =
                [
                    new AttachConfigAssignment(
                        "ui.startup_splash",
                        choice.Id == "on" ? "true" : "false"),
                ];
                return true;
            default:
                return false;
        }
    }

    public void Cancel()
    {
        if (_snapshot is { } snap)
            _ = _theme.Restore(snap);
        Close();
    }

    public void Commit()
    {
        AcceptPreview();
        Close();
    }

    public void AcceptPreview()
    {
        _snapshot = _theme.Capture();
        PreviewDirty = false;
    }

    public void ClearPendingPatch()
    {
        PendingPatch = null;
        PendingCloseAfterPatch = false;
        PendingInstallTargets = null;
        PendingUninstallTarget = null;
        PendingPluginConfigWrite = null;
        PendingOpenReleaseNotes = false;
        InstallingIntegrations = false;
    }

    public void ClearPendingOpenReleaseNotes() => PendingOpenReleaseNotes = false;

    public void ApplyPluginWriteResult(string pluginId, string key, bool applied, string value)
    {
        if (!applied)
            return;
        var pages = _pluginPages.ToArray();
        for (var i = 0; i < pages.Length; i++)
        {
            if (!string.Equals(pages[i].PluginId, pluginId, StringComparison.Ordinal))
                continue;
            var fields = pages[i].Fields.ToArray();
            for (var j = 0; j < fields.Length; j++)
            {
                if (!string.Equals(fields[j].Key, key, StringComparison.Ordinal))
                    continue;
                fields[j] = fields[j] with { Value = value };
            }

            pages[i] = pages[i] with { Fields = fields };
        }

        _pluginPages = pages;
    }

    /// <summary>Counts committed integration lists. Refreshes use it to coalesce.</summary>
    public int IntegrationCommits { get; private set; }

    public void BindIntegrations(IReadOnlyList<OfficialIntegrationStatus> statuses, IReadOnlyList<string>? messages = null)
    {
        IntegrationCommits++;
        _integrations = statuses ?? [];
        IntegrationMessages = messages ?? [];
        IntegrationLoadError = null;
        IntegrationRetryArmed = false;
        LoadingIntegrations = false;
        InstallingIntegrations = false;
        _results.Clear();
        IntegrationSummary = null;
        SeedIntegrationSelection();
        if (ActivePage.Kind is SettingsPageKind.Integrations)
            _itemIndex = Math.Clamp(_itemIndex, 0, Math.Max(0, Items.Count - 1));
    }

    public bool IsIntegrationSelected(OfficialIntegrationTarget target) => _selected.Contains(target);

    public bool ToggleSelection(int index)
    {
        if (index < 0 || index >= _integrations.Count)
            return false;
        _itemIndex = index;
        var target = _integrations[index].Target;
        if (!_selected.Add(target))
            _selected.Remove(target);
        _selectionSeeded = true;
        return true;
    }

    public bool ToggleHighlightedSelection()
    {
        if (ActivePage.Kind is not SettingsPageKind.Integrations || _integrations.Count == 0)
            return false;
        return ToggleSelection(Math.Clamp(_itemIndex, 0, _integrations.Count - 1));
    }

    public bool RequestUninstall()
    {
        if (ActivePage.Kind is not SettingsPageKind.Integrations || _integrations.Count == 0)
            return false;
        var index = Math.Clamp(_itemIndex, 0, _integrations.Count - 1);
        PendingUninstallTarget = _integrations[index].Target;
        PendingInstallTargets = null;
        return true;
    }

    public bool TryIntegrationResult(OfficialIntegrationTarget target, out IntegrationRowResult result) =>
        _results.TryGetValue(target, out result);

    public IReadOnlyList<string> HighlightedDetail()
    {
        if (ActivePage.Kind is not SettingsPageKind.Integrations || _integrations.Count == 0)
            return [];
        var status = _integrations[Math.Clamp(_itemIndex, 0, _integrations.Count - 1)];
        if (_results.TryGetValue(status.Target, out var result)
            && result.Kind is IntegrationRowResultKind.Failed
            && result.Detail.Length > 0)
        {
            return [result.Detail];
        }

        return status.Consent;
    }

    public void NoteIntegrationResults(
        IReadOnlyList<(OfficialIntegrationTarget Target, IntegrationRowResultKind Kind, string Detail)> results,
        bool skillInstalled)
    {
        _results.Clear();
        var installed = 0;
        var unchanged = 0;
        var failed = 0;
        foreach (var result in results)
        {
            _results[result.Target] = new IntegrationRowResult(result.Kind, result.Detail);
            switch (result.Kind)
            {
                case IntegrationRowResultKind.Installed:
                    installed++;
                    break;
                case IntegrationRowResultKind.Unchanged:
                    unchanged++;
                    break;
                case IntegrationRowResultKind.Failed:
                    failed++;
                    break;
            }
        }

        var parts = new List<string>();
        if (installed > 0)
            parts.Add(installed + " installed");
        if (unchanged > 0)
            parts.Add(unchanged + " unchanged");
        if (failed > 0)
            parts.Add(failed + " failed");
        var summary = string.Join(", ", parts);
        if (skillInstalled)
        {
            summary = summary.Length == 0
                ? Hypa.AgentRuntime.Application.Integrations.IntegrationConsentText.RestartSentence
                : summary + ". " + Hypa.AgentRuntime.Application.Integrations.IntegrationConsentText.RestartSentence;
        }

        IntegrationSummary = summary.Length == 0 ? null : summary;
    }

    public static IntegrationRowResultKind ClassifyInstallResult(bool ok, IReadOnlyList<string>? messages)
    {
        if (!ok)
            return IntegrationRowResultKind.Failed;
        var installed = false;
        var unchanged = false;
        if (messages is not null)
        {
            foreach (var message in messages)
            {
                if (message.Contains("unchanged", StringComparison.Ordinal))
                    unchanged = true;
                if (message.Contains("installed ", StringComparison.Ordinal)
                    || message.StartsWith("installed", StringComparison.Ordinal))
                    installed = true;
            }
        }

        if (installed)
            return IntegrationRowResultKind.Installed;
        if (unchanged)
            return IntegrationRowResultKind.Unchanged;
        return IntegrationRowResultKind.Installed;
    }

    public static bool InstalledASkill(IReadOnlyList<string>? messages)
    {
        if (messages is null)
            return false;
        foreach (var message in messages)
        {
            if (message.Contains("installed runtime skill", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void SeedIntegrationSelection()
    {
        if (_selectionSeeded)
        {
            _selected.RemoveWhere(target => !IntegrationListHas(target));
            return;
        }

        if (_integrations.Count == 0)
            return;
        _selected.Clear();
        foreach (var status in _integrations)
        {
            if (status.Available && status.NeedsInstall())
                _selected.Add(status.Target);
        }

        _selectionSeeded = true;
    }

    private bool IntegrationListHas(OfficialIntegrationTarget target)
    {
        foreach (var status in _integrations)
        {
            if (status.Target == target)
                return true;
        }

        return false;
    }

    public void BeginLoadingIntegrations()
    {
        LoadingIntegrations = true;
        IntegrationLoadError = null;
        IntegrationRetryArmed = false;
    }

    public int BeginIntegrationLoad()
    {
        BeginLoadingIntegrations();
        return ++_integrationLoadToken;
    }

    public bool IntegrationLoadIsCurrent(int token) =>
        token == _integrationLoadToken
        && IsOpen
        && ActivePage.Kind is SettingsPageKind.Integrations;

    /// <summary>True when settings is open on the integrations page.</summary>
    public bool ShowsIntegrations =>
        IsOpen && ActivePage.Kind is SettingsPageKind.Integrations;

    public void DiscardIntegrationLoad(int token)
    {
        if (token != _integrationLoadToken)
            return;
        LoadingIntegrations = false;
        InstallingIntegrations = false;
    }

    public void NoteIntegrationLoadError(string? message)
    {
        IntegrationLoadError = string.IsNullOrWhiteSpace(message) ? "request failed" : message.Trim();
        LoadingIntegrations = false;
    }

    public void ResetIntegrationRetryBudget()
    {
        IntegrationRetryAttempts = 0;
        IntegrationRetryArmed = false;
    }

    public void ConsumeIntegrationRetry()
    {
        IntegrationRetryArmed = false;
        if (IntegrationRetryAttempts < IntegrationRetryLimit)
            IntegrationRetryAttempts++;
    }

    public bool ArmIntegrationRetry()
    {
        if (IntegrationRetryAttempts >= IntegrationRetryLimit)
        {
            IntegrationRetryArmed = false;
            return false;
        }

        IntegrationRetryArmed = true;
        return true;
    }

    /// <summary>
    /// Records the mux host whose files this page shows.
    /// A local endpoint keeps the title <c>settings</c>.
    /// </summary>
    public void NoteMuxHost(bool remote, string? displayName)
    {
        _remoteMuxHost = remote;
        _muxHostName = displayName?.Trim() ?? "";
    }

    public string PageTitle =>
        _remoteMuxHost && _muxHostName.Length > 0
            ? SettingsPainter.Title + " · " + _muxHostName
            : SettingsPainter.Title;

    public void NoteIntegrationsEndpoint(string endpointId, (ulong Source, ulong Destination) generation)
    {
        _integrationsEndpointId = endpointId ?? "";
        _integrationsGeneration = generation;
        _integrationsHaveEndpoint = true;
        IntegrationRetryAttempts = 0;
        IntegrationRetryArmed = false;
    }

    public bool IntegrationsMatchEndpoint(string endpointId, (ulong Source, ulong Destination) generation) =>
        _integrationsHaveEndpoint
        && string.Equals(_integrationsEndpointId, endpointId, StringComparison.Ordinal)
        && _integrationsGeneration == generation;

    public string TabLabel(SettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Kind is SettingsPageKind.Integrations && HasOutdatedIntegration)
            return "● " + page.Label;
        return page.Label;
    }

    public void Close()
    {
        IsOpen = false;
        PreviewDirty = false;
        PendingPatch = null;
        PendingCloseAfterPatch = false;
        PendingInstallTargets = null;
        PendingUninstallTarget = null;
        PendingPluginConfigWrite = null;
        PendingOpenReleaseNotes = false;
        InstallingIntegrations = false;
        LoadingIntegrations = false;
        _selectionSeeded = false;
        _selected.Clear();
        _results.Clear();
        IntegrationSummary = null;
        IntegrationRetryArmed = false;
        IntegrationRetryAttempts = 0;
        _integrationsHaveEndpoint = false;
        _remoteMuxHost = false;
        _muxHostName = "";
        // A load that is waiting or in flight must not commit after close.
        _integrationLoadToken++;
        ListOffset = 0;
        _integrationScrollSpan = 0;
        _integrationVisibleSlots = 0;
        _snapshot = null;
    }

    /// <summary>
    /// Records the integrations body that scrolls.
    /// <paramref name="span"/> counts integration rows plus the lines under them.
    /// </summary>
    internal void NoteIntegrationScroll(int span, int visibleSlots)
    {
        _integrationScrollSpan = Math.Max(0, span);
        _integrationVisibleSlots = Math.Max(0, visibleSlots);
    }

    public void SyncListOffset(int visibleSlots)
    {
        if (ScrollsIntegrationTail(visibleSlots))
        {
            var tailOffset = _integrationScrollSpan - visibleSlots;
            ListOffset = Math.Clamp(ListOffset, 0, Math.Max(0, tailOffset));
            PlaceIntegrationSelection(visibleSlots);
            return;
        }

        var count = Items.Count;
        if (visibleSlots <= 0 || count <= visibleSlots)
        {
            ListOffset = 0;
            return;
        }

        var maxOffset = count - visibleSlots;
        if (_itemIndex < ListOffset)
            ListOffset = _itemIndex;
        else if (_itemIndex >= ListOffset + visibleSlots)
            ListOffset = _itemIndex - visibleSlots + 1;
        ListOffset = Math.Clamp(ListOffset, 0, maxOffset);
    }

    private bool ScrollsIntegrationTail(int visibleSlots) =>
        ActivePage.Kind is SettingsPageKind.Integrations
        && visibleSlots > 0
        && _integrationScrollSpan > Items.Count
        && _integrationScrollSpan > visibleSlots;

    private void PlaceIntegrationSelection(int visibleSlots)
    {
        var count = Items.Count;
        if (count == 0)
            return;
        var last = Math.Min(count - 1, ListOffset + visibleSlots - 1);
        if (ListOffset >= count || last < ListOffset)
        {
            _itemIndex = count - 1;
            return;
        }

        if (_itemIndex < ListOffset)
            _itemIndex = ListOffset;
        else if (_itemIndex > last)
            _itemIndex = last;
    }

    private void PreviewTheme(string name)
    {
        if (!_theme.SetName(name))
            return;
        PreviewDirty = true;
        QueueTheme(name);
    }

    private void QueueTheme(string name)
    {
        PendingPatch =
        [
            new AttachConfigAssignment("theme.name", Quote(name)),
            new AttachConfigAssignment("theme.auto_switch", "false"),
        ];
        PendingCloseAfterPatch = false;
    }

    private int SeedIndex(SettingsPage page)
    {
        var items = ItemsFor(page);
        if (items.Count == 0)
            return 0;
        var current = CurrentId(page);
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, current, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    private string CurrentId(SettingsPage page) =>
        page.Kind switch
        {
            SettingsPageKind.Theme => _theme.Name,
            SettingsPageKind.Indicators =>
                _ui.StatusIndicators is StatusIndicatorStyle.Symbols ? "symbols" : "dots",
            SettingsPageKind.Sound => _ui.Sound.Enabled ? "on" : "off",
            SettingsPageKind.Toasts => ToastId(_ui.Toast.Delivery),
            SettingsPageKind.PaneLabels => _ui.ShowAgentLabelsOnPaneBorders ? "on" : "off",
            SettingsPageKind.Startup => _ui.StartupSplash ? "on" : "off",
            _ => "",
        };

    private static string ToastId(ToastDelivery delivery) =>
        delivery switch
        {
            ToastDelivery.Hypa => "hypa",
            ToastDelivery.Terminal => "terminal",
            ToastDelivery.System => "system",
            _ => "off",
        };

    private bool ApplyHosted(SettingsPage page)
    {
        var pluginPage = _pluginPages.FirstOrDefault(p => string.Equals(p.PluginId, page.PluginId, StringComparison.Ordinal));
        if (pluginPage is null || pluginPage.Fields.Count == 0)
            return false;
        var index = Math.Clamp(_itemIndex, 0, pluginPage.Fields.Count - 1);
        var field = pluginPage.Fields[index];
        PendingPluginConfigWrite = new PendingPluginConfigWrite(
            pluginPage.PluginId,
            field.Key,
            PluginSettingsFieldControl.NextValue(field));
        return true;
    }

    private PluginSettingsPageView? ActivePluginPage() =>
        ActivePage.Kind is SettingsPageKind.Hosted
            ? _pluginPages.FirstOrDefault(p => string.Equals(p.PluginId, ActivePage.PluginId, StringComparison.Ordinal))
            : null;

    private IReadOnlyList<SettingsListItem> ItemsFor(SettingsPage page) =>
        page.Kind switch
        {
            SettingsPageKind.Theme => ThemeItems(),
            SettingsPageKind.Indicators =>
            [
                new SettingsListItem("dots", "color dots"),
                new SettingsListItem("symbols", "distinct symbols"),
            ],
            SettingsPageKind.Sound =>
            [
                new SettingsListItem("on", "on"),
                new SettingsListItem("off", "off"),
            ],
            SettingsPageKind.Toasts =>
            [
                new SettingsListItem("off", "off"),
                new SettingsListItem("hypa", "inside hypa"),
                new SettingsListItem("terminal", "via terminal"),
                new SettingsListItem("system", "via system"),
            ],
            SettingsPageKind.PaneLabels =>
            [
                new SettingsListItem("on", "on"),
                new SettingsListItem("off", "off"),
            ],
            SettingsPageKind.Startup =>
            [
                new SettingsListItem("on", "on"),
                new SettingsListItem("off", "off"),
            ],
            SettingsPageKind.Integrations => IntegrationItems(),
            SettingsPageKind.ReleaseNotes => [],
            SettingsPageKind.Hosted => ActivePluginPage() is { } hosted
                ? PluginSettingsFieldControl.ItemsFor(hosted)
                : [],
            _ => [],
        };

    private IReadOnlyList<SettingsListItem> IntegrationItems()
    {
        if (_integrations.Count == 0)
            return [];
        var list = new SettingsListItem[_integrations.Count];
        for (var i = 0; i < _integrations.Count; i++)
        {
            var status = _integrations[i];
            var version = status.InstalledVersion is int v ? " v" + v : "";
            list[i] = new SettingsListItem(
                status.Target.WireName(),
                status.Target.Label() + "  " + IntegrationStateLabel(status) + version);
        }

        return list;
    }

    private static string IntegrationStateLabel(OfficialIntegrationStatus status) =>
        status.State switch
        {
            OfficialIntegrationStatusKind.Current => "installed",
            OfficialIntegrationStatusKind.Outdated => "update available",
            OfficialIntegrationStatusKind.NotInstalled when status.Available => "available",
            _ => "not found",
        };

    private static IReadOnlyList<SettingsListItem> ThemeItems()
    {
        var list = new SettingsListItem[AttachThemeNames.BuiltIn.Count];
        for (var i = 0; i < list.Length; i++)
        {
            var name = AttachThemeNames.BuiltIn[i];
            list[i] = new SettingsListItem(name, name);
        }

        return list;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
