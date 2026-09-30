using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Domain.Plugins;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
// / Plugin host.
/// <c>manifest.rs</c>. Hypa names. Unknown hooks fail closed at link.
/// </summary>
public sealed class PluginHostService : IPluginHost
{
    public const string ManifestFileName = "hypa-plugin.toml";
    public const string ProductVersion = "0.1.0";
    public const string ManifestUnavailablePrefix = "manifest unavailable: ";

    private readonly IPluginFiles _files;
    private readonly IPluginClock _clock;
    private readonly IPluginPathRoots _paths;
    private readonly IPluginManifestParser _parser;
    private readonly IPluginRegistry _registry;
    private readonly IPluginProcessLauncher _launcher;
    private readonly IPluginProcessRegistry _processRegistry;
    private readonly IPluginRefreshRunner _refreshRunner;
    private readonly PluginSettingsStore _settings;
    private readonly IPluginContextSource _context;
    private readonly string? _binPath;
    private readonly string? _socketPath;
    private readonly object _gate = new();
    private readonly PluginGrantTokens _tokens = new();
    private readonly List<PluginCommandLog> _logs = [];
    private readonly Dictionary<string, int> _inFlightByPlugin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginPaneRecord> _ownedPanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _deliveryTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ownedPopups = new(StringComparer.Ordinal);
    private int _inFlight;
    private long _nextLogId = 1;
    private IReadOnlyList<InstalledPlugin> _cache = [];

    public PluginHostService(
        IPluginFiles files,
        IPluginClock clock,
        IPluginPathRoots paths,
        IPluginManifestParser parser,
        IPluginRegistry registry,
        IPluginProcessLauncher launcher,
        IPluginContextSource? context = null,
        string? binPath = null,
        string? socketPath = null,
        IPluginProcessRegistry? processRegistry = null,
        IPluginRefreshRunner? refreshRunner = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _processRegistry = processRegistry ?? new PluginProcessRegistry();
        _refreshRunner = refreshRunner ?? UnconfiguredPluginRefreshRunner.Instance;
        _settings = new PluginSettingsStore(_files, _paths);
        _context = context ?? new EmptyPluginContextSource();
        _binPath = binPath;
        _socketPath = socketPath;
        try
        {
            var loaded = _registry.Load();
            if (loaded.IsOk)
                _cache = ReloadManifests(loaded.Value);
        }
        catch
        {
            _cache = [];
        }
    }

    public string CurrentVersion => ProductVersion;

    public PluginResult<PluginLinkResult> Link(string path, bool enabled)
    {
        var loaded = LoadFromPath(path, enabled);
        if (!loaded.IsOk)
            return PluginResult<PluginLinkResult>.Fail(loaded.Error);
        var plugin = loaded.Value with { EnableGeneration = enabled ? 1 : 0 };
        var dirs = EnsureUserDirs(plugin.PluginId);
        if (!dirs.IsOk)
            return PluginResult<PluginLinkResult>.Fail(dirs.Error);

        var saved = Mutate(list =>
        {
            var next = list.Where(p => p.PluginId != plugin.PluginId).ToList();
            next.Add(plugin);
            return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(next);
        });
        if (!saved.IsOk)
            return PluginResult<PluginLinkResult>.Fail(saved.Error);

        if (plugin.Enabled)
            IssueToken(plugin);
        return PluginResult<PluginLinkResult>.Ok(new PluginLinkResult
        {
            Plugin = plugin,
            TrustPreview = TrustPreview(plugin),
        });
    }

    public PluginResult<IReadOnlyList<InstalledPlugin>> List(string? pluginId)
    {
        var refresh = Refresh();
        if (!refresh.IsOk)
            return PluginResult<IReadOnlyList<InstalledPlugin>>.Fail(refresh.Error);
        IEnumerable<InstalledPlugin> items = _cache;
        if (pluginId is not null)
        {
            var id = PluginIdentifiers.NormalizePluginId(pluginId);
            if (id is null)
                return PluginResult<IReadOnlyList<InstalledPlugin>>.Fail(PluginError.InvalidId, "invalid plugin id");
            items = items.Where(p => p.PluginId == id);
        }

        return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(
            items.OrderBy(p => p.PluginId, StringComparer.Ordinal).ToArray());
    }

    public PluginResult<PluginUnlinkResult> Unlink(string pluginId)
    {
        var id = PluginIdentifiers.NormalizePluginId(pluginId);
        if (id is null)
            return PluginResult<PluginUnlinkResult>.Fail(PluginError.InvalidId, "invalid plugin id");
        var panes = OwnedPaneIds(id);
        var hadPopup = OwnsPopup(id);
        var existed = false;
        lock (_gate)
            existed = _cache.Any(p => p.PluginId == id);
        var saved = Mutate(list =>
        {
            var next = list.Where(p => p.PluginId != id).ToList();
            var removed = next.Count != list.Count;
            return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(removed ? next : list);
        });
        if (!saved.IsOk)
            return PluginResult<PluginUnlinkResult>.Fail(saved.Error);
        var removed = existed;
        if (removed)
        {
            _tokens.Revoke(id);
            ClearDeliveryTargets(id);
        }

        return PluginResult<PluginUnlinkResult>.Ok(new PluginUnlinkResult
        {
            PluginId = id,
            Removed = removed,
            ClosedPaneIds = panes,
            ClosedPopup = hadPopup,
        });
    }

    public PluginResult<PluginEnableResult> SetEnabled(string pluginId, bool enabled)
    {
        var id = PluginIdentifiers.NormalizePluginId(pluginId);
        if (id is null)
            return PluginResult<PluginEnableResult>.Fail(PluginError.InvalidId, "invalid plugin id");
        InstalledPlugin? found = null;
        var saved = Mutate(list =>
        {
            var next = new List<InstalledPlugin>(list.Count);
            var hit = false;
            foreach (var plugin in list)
            {
                if (plugin.PluginId != id)
                {
                    next.Add(plugin);
                    continue;
                }

                hit = true;
                found = plugin with { Enabled = enabled, EnableGeneration = plugin.EnableGeneration + 1 };
                next.Add(found);
            }

            return hit
                ? PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(next)
                : PluginResult<IReadOnlyList<InstalledPlugin>>.Fail(PluginError.NotFound, "plugin not found");
        });
        if (!saved.IsOk)
            return PluginResult<PluginEnableResult>.Fail(saved.Error);
        if (found is null)
            return PluginResult<PluginEnableResult>.Fail(PluginError.NotFound, "plugin not found");
        IReadOnlyList<string> panes = Array.Empty<string>();
        var hadPopup = false;
        _tokens.Revoke(id);
        if (!enabled)
            ClearDeliveryTargets(id);
        if (enabled)
            IssueToken(found);
        if (!enabled)
        {
            panes = OwnedPaneIds(id);
            hadPopup = OwnsPopup(id);
        }

        return PluginResult<PluginEnableResult>.Ok(new PluginEnableResult
        {
            Plugin = found,
            ClosedPaneIds = panes,
            ClosedPopup = hadPopup,
        });
    }

    public PluginResult<IReadOnlyList<PluginActionInfo>> ListActions(string? pluginId)
    {
        var listed = List(pluginId);
        if (!listed.IsOk)
            return PluginResult<IReadOnlyList<PluginActionInfo>>.Fail(listed.Error);
        var actions = listed.Value
            .Where(ManifestAvailable)
            .SelectMany(plugin => plugin.Actions.Select(a => ToActionInfo(plugin, a)))
            .OrderBy(a => a.QualifiedId, StringComparer.Ordinal)
            .ToArray();
        return PluginResult<IReadOnlyList<PluginActionInfo>>.Ok(actions);
    }

    public PluginResult<PluginActionInvokeResult> InvokeAction(
        string actionId,
        string? pluginId,
        PluginInvocationContext? context)
    {
        var refresh = Refresh();
        if (!refresh.IsOk)
            return PluginResult<PluginActionInvokeResult>.Fail(refresh.Error);
        var found = FindAction(pluginId, actionId);
        if (!found.IsOk)
            return PluginResult<PluginActionInvokeResult>.Fail(found.Error);
        var (plugin, action) = found.Value;
        if (!plugin.Enabled)
        {
            return PluginResult<PluginActionInvokeResult>.Fail(
                PluginError.Disabled,
                "plugin " + plugin.PluginId + " is disabled");
        }

        var platform = PluginIdentifiers.EnsurePlatformSupported(
            PluginIdentifiers.EffectivePlatforms(action.Platforms, plugin.Platforms),
            "action '" + action.QualifiedId + "'");
        if (!platform.IsOk)
            return PluginResult<PluginActionInvokeResult>.Fail(platform.Error);

        var merged = MergeContext(context, "plugin.action.invoke") with
        {
            InvocationSource = context?.InvocationSource ?? "api",
        };
        if (!string.IsNullOrWhiteSpace(merged.FocusedPaneId))
            NoteDeliveryTarget(plugin.PluginId, merged.FocusedPaneId);
        var log = StartCommand(plugin, action.ActionId, eventName: null, action.Command, merged, eventJson: null);
        if (!log.IsOk)
            return PluginResult<PluginActionInvokeResult>.Fail(log.Error);
        return PluginResult<PluginActionInvokeResult>.Ok(new PluginActionInvokeResult
        {
            Action = action,
            Context = merged,
            Log = log.Value,
        });
    }

    public PluginResult<bool> ActivateLink(string url, PluginInvocationContext? context)
    {
        ArgumentNullException.ThrowIfNull(url);
        var refresh = Refresh();
        if (!refresh.IsOk)
            return PluginResult<bool>.Fail(refresh.Error);
        var found = FindLinkHandler(url);
        if (found is null)
            return PluginResult<bool>.Ok(false);

        var (plugin, handler, action) = found.Value;
        var merged = MergeContext(context, "pane.link.activate") with
        {
            InvocationSource = "link_click",
            ClickedUrl = url,
            LinkHandlerId = handler.Id,
        };
        if (!string.IsNullOrWhiteSpace(merged.FocusedPaneId))
            NoteDeliveryTarget(plugin.PluginId, merged.FocusedPaneId);
        var log = StartCommand(plugin, action.Id, eventName: null, action.Command, merged, eventJson: null);
        if (!log.IsOk)
            return PluginResult<bool>.Fail(log.Error);
        return PluginResult<bool>.Ok(true);
    }

    public PluginResult<IReadOnlyList<PluginCommandLog>> ListLogs(string? pluginId, int? limit)
    {
        string? id = null;
        if (pluginId is not null)
        {
            id = PluginIdentifiers.NormalizePluginId(pluginId);
            if (id is null)
                return PluginResult<IReadOnlyList<PluginCommandLog>>.Fail(PluginError.InvalidId, "invalid plugin id");
        }

        var take = Math.Clamp(limit ?? 50, 1, PluginCommandLimits.LogLimit);
        lock (_gate)
        {
            IEnumerable<PluginCommandLog> logs = _logs;
            if (id is not null)
                logs = logs.Where(l => l.PluginId == id);
            var arr = logs.TakeLast(take).ToArray();
            return PluginResult<IReadOnlyList<PluginCommandLog>>.Ok(arr);
        }
    }

    public PluginResult<PluginPaneOpenPlan> PlanPaneOpen(
        string pluginId,
        string entrypoint,
        string? placement,
        PluginInvocationContext context,
        IReadOnlyDictionary<string, string>? extraEnv)
    {
        var refresh = Refresh();
        if (!refresh.IsOk)
            return PluginResult<PluginPaneOpenPlan>.Fail(refresh.Error);
        var id = PluginIdentifiers.NormalizePluginId(pluginId);
        if (id is null)
            return PluginResult<PluginPaneOpenPlan>.Fail(PluginError.InvalidId, "invalid plugin id");
        var plugin = _cache.FirstOrDefault(p => p.PluginId == id);
        if (plugin is null)
            return PluginResult<PluginPaneOpenPlan>.Fail(PluginError.NotFound, "plugin not found");
        if (!ManifestAvailable(plugin))
        {
            return PluginResult<PluginPaneOpenPlan>.Fail(
                PluginError.ManifestUnavailable,
                "plugin " + id + " manifest is unavailable");
        }

        if (!plugin.Enabled)
        {
            return PluginResult<PluginPaneOpenPlan>.Fail(
                PluginError.Disabled,
                "plugin " + id + " is disabled");
        }

        var entry = PluginIdentifiers.NormalizeLocalId(entrypoint);
        if (entry is null)
            return PluginResult<PluginPaneOpenPlan>.Fail(PluginError.InvalidEntrypoint, "invalid entrypoint id");
        var pane = plugin.Panes.FirstOrDefault(p => p.Id == entry);
        if (pane is null)
        {
            return PluginResult<PluginPaneOpenPlan>.Fail(
                PluginError.PaneNotFound,
                "plugin pane entrypoint '" + entry + "' not found");
        }

        var platform = PluginIdentifiers.EnsurePlatformSupported(
            PluginIdentifiers.EffectivePlatforms(pane.Platforms, plugin.Platforms),
            "plugin pane");
        if (!platform.IsOk)
            return PluginResult<PluginPaneOpenPlan>.Fail(platform.Error);

        var resolvedPlacement = string.IsNullOrWhiteSpace(placement) ? pane.Placement : placement.Trim();
        if (resolvedPlacement is not ("overlay" or "popup" or "split" or "tab" or "zoomed"))
        {
            return PluginResult<PluginPaneOpenPlan>.Fail(
                PluginError.InvalidParams,
                "placement must be overlay, popup, split, tab, or zoomed");
        }

        if (resolvedPlacement != "popup" && (pane.Width is not null || pane.Height is not null))
        {
            return PluginResult<PluginPaneOpenPlan>.Fail(
                PluginError.InvalidPaneSize,
                "pane width and height are only supported when placement is popup");
        }

        var command = ResolveCommand(pane.Command, plugin.PluginRoot);
        var env = BuildEnv(plugin, context, actionId: null, eventName: null, eventJson: null, entry);
        env = PluginEnv.MergeProtected(extraEnv, env);
        return PluginResult<PluginPaneOpenPlan>.Ok(new PluginPaneOpenPlan
        {
            PluginId = plugin.PluginId,
            Entrypoint = entry,
            Title = pane.Title,
            Placement = resolvedPlacement,
            Command = command,
            Cwd = plugin.PluginRoot,
            Environment = env,
            Width = pane.Width,
            Height = pane.Height,
        });
    }

    public void NoteOwnedPane(string pluginId, string entrypoint, string paneId)
    {
        lock (_gate)
        {
            _ownedPanes[paneId] = new PluginPaneRecord(pluginId, entrypoint);
        }
    }

    public void ForgetOwnedPane(string paneId)
    {
        lock (_gate)
            _ownedPanes.Remove(paneId);
    }

    public IReadOnlyList<string> OwnedPaneIds(string pluginId)
    {
        lock (_gate)
        {
            return _ownedPanes
                .Where(kv => kv.Value.PluginId == pluginId)
                .Select(kv => kv.Key)
                .ToArray();
        }
    }

    public void NoteDeliveryTarget(string pluginId, string paneId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (!_deliveryTargets.TryGetValue(pluginId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _deliveryTargets[pluginId] = set;
            }

            set.Add(paneId);
        }
    }

    public string? ResolveSendTextPluginId(string paneId)
    {
        lock (_gate)
        {
            if (_ownedPanes.TryGetValue(paneId, out var owned))
                return owned.PluginId;
            foreach (var pair in _deliveryTargets)
            {
                if (pair.Value.Contains(paneId))
                    return pair.Key;
            }
        }

        return null;
    }

    public bool CanDeliverText(string pluginId, string paneId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_gate)
        {
            if (_ownedPanes.TryGetValue(paneId, out var owned)
                && string.Equals(owned.PluginId, pluginId, StringComparison.Ordinal))
            {
                return true;
            }

            return _deliveryTargets.TryGetValue(pluginId, out var set) && set.Contains(paneId);
        }
    }

    public void RecordPaneTextDelivery(string pluginId, string paneId)
    {
        var now = UnixMs();
        string logId;
        lock (_gate)
        {
            logId = "plugin-log-" + _nextLogId;
            _nextLogId++;
        }

        PushLog(new PluginCommandLog
        {
            LogId = logId,
            PluginId = pluginId,
            Event = "plugin.pane.send_text",
            Command = ["plugin.pane.send_text", paneId],
            Status = "succeeded",
            StartedUnixMs = now,
            FinishedUnixMs = now,
        });
    }

    private void ClearDeliveryTargets(string pluginId)
    {
        lock (_gate)
            _deliveryTargets.Remove(pluginId);
    }

    public void NoteOwnedPopup(string pluginId)
    {
        lock (_gate)
            _ownedPopups.Add(pluginId);
    }

    public void ForgetOwnedPopup(string pluginId)
    {
        lock (_gate)
            _ownedPopups.Remove(pluginId);
    }

    public bool OwnsPopup(string pluginId)
    {
        lock (_gate)
            return _ownedPopups.Contains(pluginId);
    }

    public void RunStartupHooks()
    {
        try
        {
            var refresh = Refresh();
            if (!refresh.IsOk)
                return;
            var context = _context.Current("plugin.startup") with { InvocationSource = "startup" };
            InstalledPlugin[] plugins;
            lock (_gate)
            {
                plugins = _cache
                    .Where(p => p is { Enabled: true } && ManifestAvailable(p) && p.Startup is { Count: > 0 })
                    .OrderBy(p => p.PluginId, StringComparer.Ordinal)
                    .ToArray();
            }

            foreach (var plugin in plugins)
            {
                foreach (var startup in plugin.Startup ?? [])
                {
                    if (startup?.Command is not { Count: > 0 })
                        continue;
                    var platform = PluginIdentifiers.EnsurePlatformSupported(
                        PluginIdentifiers.EffectivePlatforms(startup.Platforms, plugin.Platforms),
                        "startup");
                    if (!platform.IsOk)
                        continue;
                    _ = StartCommand(
                        plugin,
                        actionId: null,
                        eventName: PluginHookCatalog.Startup,
                        startup.Command,
                        context,
                        eventJson: null);
                }
            }
        }
        catch
        {
        }
    }

    public void HandleRuntimeEvent(string eventType, string payloadJson)
    {
        var hook = PluginEventFilterMatcher.MapHookName(eventType, payloadJson);
        if (hook is null)
            return;
        var refresh = Refresh();
        if (!refresh.IsOk)
            return;
        InstalledPlugin[] plugins;
        lock (_gate)
        {
            plugins = _cache
                .Where(p => p.Enabled && ManifestAvailable(p) && p.Events.Any(e => e.On == hook))
                .ToArray();
        }

        if (plugins.Length == 0)
            return;

        var context = _context.ForEvent(hook, payloadJson, hook);
        foreach (var plugin in plugins)
        {
            foreach (var ev in plugin.Events)
            {
                if (ev.On != hook)
                    continue;
                var platform = PluginIdentifiers.EnsurePlatformSupported(
                    PluginIdentifiers.EffectivePlatforms(ev.Platforms, plugin.Platforms),
                    hook);
                if (!platform.IsOk)
                    continue;
                if (!PluginEventFilterMatcher.Matches(ev.Filter, context, payloadJson))
                    continue;
                _ = StartCommand(plugin, actionId: null, hook, ev.Command, context, payloadJson);
            }
        }
    }

    public PluginGrantDecision CheckGrant(
        string? token,
        string method,
        string? source,
        string? targetPluginId,
        bool pluginConnection = false) =>
        _tokens.Check(
            token,
            method,
            source,
            targetPluginId,
            pluginConnection,
            _clock.UtcNow,
            EnableGenerationOf);

    public string? ResolveActionOwnerPluginId(string actionId, string? pluginId)
    {
        var refresh = Refresh();
        if (!refresh.IsOk)
            return null;
        var found = FindAction(pluginId, actionId);
        return found.IsOk ? found.Value.Plugin.PluginId : null;
    }

    public bool IsLivePluginProcess(int pid) => _processRegistry.IsLive(pid);

    public string? PeekGrantToken(string pluginId) => _tokens.Peek(pluginId);

    public IReadOnlyList<PluginManifestResource> DeclaredResources(string pluginId)
    {
        var id = PluginIdentifiers.NormalizePluginId(pluginId);
        if (id is null)
            return [];
        var refresh = Refresh();
        if (!refresh.IsOk)
            return [];
        foreach (var plugin in _cache)
        {
            if (plugin.PluginId == id)
                return plugin.Resources ?? [];
        }

        return [];
    }

    public bool HasAction(string pluginId, string actionId)
    {
        var found = FindAction(pluginId, actionId);
        return found.IsOk;
    }

    public PluginResult<PluginCommandLog> RunDeclaredRefresh(string pluginId, string localId)
    {
        var refresh = Refresh();
        if (!refresh.IsOk)
            return PluginResult<PluginCommandLog>.Fail(refresh.Error);
        var id = PluginIdentifiers.NormalizePluginId(pluginId);
        var resourceId = PluginIdentifiers.NormalizeLocalId(localId);
        if (id is null)
            return PluginResult<PluginCommandLog>.Fail(PluginError.InvalidId, "invalid plugin id");
        if (resourceId is null)
        {
            return PluginResult<PluginCommandLog>.Fail(
                PluginError.InvalidResourceId,
                "invalid plugin resource id");
        }

        InstalledPlugin? plugin = null;
        foreach (var item in _cache)
        {
            if (item.PluginId == id)
            {
                plugin = item;
                break;
            }
        }

        if (plugin is null)
            return PluginResult<PluginCommandLog>.Fail(PluginError.NotFound, "plugin not found");
        if (!plugin.Enabled)
        {
            return PluginResult<PluginCommandLog>.Fail(
                PluginError.Disabled,
                "plugin " + plugin.PluginId + " is disabled");
        }

        PluginManifestResource? declared = null;
        foreach (var resource in plugin.Resources)
        {
            if (resource.Id == resourceId)
            {
                declared = resource;
                break;
            }
        }

        if (declared is null)
        {
            return PluginResult<PluginCommandLog>.Fail(
                PluginError.ResourceNotFound,
                "plugin resource not found");
        }

        if (declared.Command is not { Count: > 0 })
        {
            return PluginResult<PluginCommandLog>.Fail(
                PluginError.InvalidCommand,
                "resource has no refresh command");
        }

        var platform = PluginIdentifiers.EnsurePlatformSupported(
            PluginIdentifiers.EffectivePlatforms(declared.Platforms, plugin.Platforms),
            "resource '" + plugin.PluginId + "/" + resourceId + "'");
        if (!platform.IsOk)
            return PluginResult<PluginCommandLog>.Fail(platform.Error);

        var context = MergeContext(null, "plugin.resource.refresh");
        return RunRefreshCommand(plugin, declared.Command, context);
    }

    public PluginTrustPreview TrustPreview(InstalledPlugin plugin)
    {
        var commands = new List<IReadOnlyList<string>>();
        foreach (var item in plugin.Build)
            commands.Add(item.Command);
        foreach (var item in plugin.Startup)
            commands.Add(item.Command);
        foreach (var item in plugin.Actions)
            commands.Add(item.Command);
        foreach (var item in plugin.Events)
            commands.Add(item.Command);
        foreach (var item in plugin.Panes)
            commands.Add(item.Command);
        foreach (var item in plugin.Resources)
        {
            if (item.Command is { Count: > 0 })
                commands.Add(item.Command);
        }
        return new PluginTrustPreview
        {
            Commands = commands,
            Grants = plugin.RequestedGrants,
        };
    }

    private long EnableGenerationOf(string pluginId)
    {
        lock (_gate)
        {
            foreach (var plugin in _cache)
            {
                if (plugin.PluginId == pluginId)
                    return plugin.EnableGeneration;
            }
        }

        return -1;
    }

    private void IssueToken(InstalledPlugin plugin)
    {
        var dirs = EnsureUserDirs(plugin.PluginId);
        if (!dirs.IsOk)
            return;
        _tokens.Issue(
            plugin.PluginId,
            plugin.EnableGeneration,
            plugin.RequestedGrants,
            _clock.UtcNow + PluginCommandLimits.GrantTokenTtl);
    }

    private PluginResult<IReadOnlyList<InstalledPlugin>> Refresh()
    {
        try
        {
            var loaded = _registry.Load();
            if (!loaded.IsOk)
            {
                lock (_gate)
                    _cache = [];
                return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(_cache);
            }

            lock (_gate)
                _cache = ReloadManifests(loaded.Value);
            return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(_cache);
        }
        catch
        {
            lock (_gate)
                _cache = [];
            return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(_cache);
        }
    }

    private PluginResult<IReadOnlyList<InstalledPlugin>> Mutate(
        Func<List<InstalledPlugin>, PluginResult<IReadOnlyList<InstalledPlugin>>> mutation)
    {
        var saved = _registry.Update(list =>
        {
            var reloaded = ReloadManifests(list).ToList();
            return mutation(reloaded);
        });
        if (saved.IsOk)
        {
            lock (_gate)
                _cache = saved.Value;
        }

        return saved;
    }

    private IReadOnlyList<InstalledPlugin> ReloadManifests(IReadOnlyList<InstalledPlugin>? entries)
    {
        if (entries is null || entries.Count == 0)
            return [];
        var next = new List<InstalledPlugin>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is null)
                continue;
            try
            {
                var loaded = LoadFromPath(entry.ManifestPath ?? "", entry.Enabled);
                if (loaded.IsOk)
                {
                    next.Add(loaded.Value with
                    {
                        Enabled = entry.Enabled,
                        EnableGeneration = entry.EnableGeneration,
                        SourceKind = entry.SourceKind,
                    });
                    continue;
                }

                next.Add(entry.WithNonNullCollections() with
                {
                    Warnings = [ManifestUnavailablePrefix + loaded.Error.Message],
                });
            }
            catch (Exception ex)
            {
                next.Add(entry.WithNonNullCollections() with
                {
                    Warnings = [ManifestUnavailablePrefix + ex.Message],
                });
            }
        }

        return next;
    }

    private PluginResult<InstalledPlugin> LoadFromPath(string path, bool enabled)
    {
        string manifestPath;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return PluginResult<InstalledPlugin>.Fail(
                    PluginError.ManifestNotFound,
                    "manifest path is empty");
            }

            var full = _files.GetFullPath(path);
            // Registry entries store the manifest file path; link callers may pass a plugin root dir.
            if (_files.FileExists(full))
                manifestPath = full;
            else if (_files.DirectoryExists(full))
                manifestPath = Path.Combine(full, ManifestFileName);
            else
                manifestPath = full;
            if (!_files.FileExists(manifestPath))
            {
                return PluginResult<InstalledPlugin>.Fail(
                    PluginError.ManifestNotFound,
                    "plugin manifest not found");
            }

            if (!string.Equals(Path.GetFileName(manifestPath), ManifestFileName, StringComparison.Ordinal))
            {
                return PluginResult<InstalledPlugin>.Fail(
                    PluginError.InvalidParams,
                    "plugin manifest must be named " + ManifestFileName);
            }

            manifestPath = _files.GetFullPath(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PluginResult<InstalledPlugin>.Fail(PluginError.ManifestNotFound, ex.Message);
        }

        string content;
        try
        {
            content = _files.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginResult<InstalledPlugin>.Fail(PluginError.ManifestReadFailed, ex.Message);
        }

        var parsed = _parser.Parse(content);
        if (!parsed.IsOk)
            return PluginResult<InstalledPlugin>.Fail(parsed.Error);
        var min = parsed.Value.MinHypaVersion;
        if (!PluginIdentifiers.TryParseVersion(min, out var required))
        {
            return PluginResult<InstalledPlugin>.Fail(
                PluginError.InvalidMinVersion,
                "plugin min_hypa_version must be a semantic version like " + ProductVersion);
        }

        if (!PluginIdentifiers.TryParseVersion(ProductVersion, out var current))
            current = new Version(0, 1, 0);
        if (required > current)
        {
            return PluginResult<InstalledPlugin>.Fail(
                PluginError.RequiresNewer,
                $"plugin requires Hypa {required} or newer; current Hypa is {current}");
        }

        var parent = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrEmpty(parent))
        {
            return PluginResult<InstalledPlugin>.Fail(
                PluginError.ManifestNotFound,
                "manifest path has no parent directory");
        }

        var m = parsed.Value;
        return PluginResult<InstalledPlugin>.Ok(new InstalledPlugin
        {
            PluginId = m.Id,
            Name = m.Name,
            Version = m.Version,
            MinHypaVersion = m.MinHypaVersion,
            Description = m.Description,
            ManifestPath = manifestPath,
            PluginRoot = parent,
            Enabled = enabled,
            Platforms = m.Platforms,
            Build = m.Build,
            Startup = m.Startup,
            Actions = m.Actions,
            Events = m.Events,
            Panes = m.Panes,
            LinkHandlers = m.LinkHandlers,
            Resources = m.Resources,
            MenuItems = m.MenuItems,
            SettingsFields = m.SettingsFields,
            Doctor = m.Doctor,
            RequestedGrants = m.RequestedGrants,
            Warnings = m.Warnings,
            EnableGeneration = 0,
            SourceKind = "local",
        });
    }

    public PluginResult<PluginSettingsSnapshot> GetSettings(string pluginId)
    {
        var plugin = FindEnabledPlugin(pluginId);
        if (!plugin.IsOk)
            return PluginResult<PluginSettingsSnapshot>.Fail(plugin.Error);
        if (plugin.Value.SettingsFields.Count == 0)
        {
            return PluginResult<PluginSettingsSnapshot>.Fail(
                PluginError.InvalidParams,
                "plugin declares no settings fields");
        }

        return _settings.Read(plugin.Value, plugin.Value.SettingsFields);
    }

    public PluginResult<PluginSettingsWriteOutcome> SetSettings(string pluginId, string key, string value)
    {
        var plugin = FindEnabledPlugin(pluginId);
        if (!plugin.IsOk)
            return PluginResult<PluginSettingsWriteOutcome>.Fail(plugin.Error);
        if (plugin.Value.SettingsFields.Count == 0)
        {
            return PluginResult<PluginSettingsWriteOutcome>.Fail(
                PluginError.InvalidParams,
                "plugin declares no settings fields");
        }

        return _settings.Write(plugin.Value, plugin.Value.SettingsFields, key, value);
    }

    private PluginResult<InstalledPlugin> FindEnabledPlugin(string pluginId)
    {
        var id = PluginIdentifiers.NormalizePluginId(pluginId);
        if (id is null)
            return PluginResult<InstalledPlugin>.Fail(PluginError.InvalidId, "invalid plugin id");
        var plugin = _cache.FirstOrDefault(p => p.PluginId == id);
        if (plugin is null)
            return PluginResult<InstalledPlugin>.Fail(PluginError.NotFound, "plugin not found");
        if (!plugin.Enabled)
            return PluginResult<InstalledPlugin>.Fail(PluginError.Disabled, "plugin disabled");
        if (!ManifestAvailable(plugin))
        {
            return PluginResult<InstalledPlugin>.Fail(
                PluginError.ManifestUnavailable,
                "plugin " + id + " manifest is unavailable");
        }

        return PluginResult<InstalledPlugin>.Ok(plugin);
    }

    private PluginResult<bool> EnsureUserDirs(string pluginId)
    {
        try
        {
            _files.CreateDirectory(_paths.PluginConfigDir(pluginId));
            _files.CreateDirectory(_paths.PluginStateDir(pluginId));
            return PluginResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginResult<bool>.Fail(PluginError.UserDirFailed, ex.Message);
        }
    }

    private PluginResult<(InstalledPlugin Plugin, PluginActionInfo Action)> FindAction(
        string? pluginId,
        string actionId)
    {
        if (pluginId is not null)
        {
            var id = PluginIdentifiers.NormalizePluginId(pluginId);
            if (id is null)
                return FailAction(PluginError.InvalidId, "invalid plugin id");
            var local = PluginIdentifiers.NormalizeLocalId(actionId);
            if (local is null)
                return FailAction(PluginError.InvalidActionId, "invalid action id");
            var plugin = _cache.FirstOrDefault(p => p.PluginId == id);
            if (plugin is null)
                return FailAction(PluginError.NotFound, "plugin not found");
            if (!ManifestAvailable(plugin))
            {
                return FailAction(
                    PluginError.ManifestUnavailable,
                    "plugin " + id + " manifest is unavailable");
            }

            var action = plugin.Actions.FirstOrDefault(a => a.Id == local);
            if (action is null)
                return FailAction(PluginError.ActionNotFound, "plugin action not found");
            return PluginResult<(InstalledPlugin, PluginActionInfo)>.Ok((plugin, ToActionInfo(plugin, action)));
        }

        var trimmed = actionId.Trim();
        var matches = _cache
            .Where(ManifestAvailable)
            .SelectMany(plugin => plugin.Actions.Select(a => (plugin, info: ToActionInfo(plugin, a))))
            .Where(pair => pair.info.ActionId == trimmed || pair.info.QualifiedId == trimmed)
            .ToArray();
        return matches.Length switch
        {
            1 => PluginResult<(InstalledPlugin, PluginActionInfo)>.Ok((matches[0].plugin, matches[0].info)),
            0 => FailAction(PluginError.ActionNotFound, "plugin action not found"),
            _ => FailAction(
                PluginError.AmbiguousAction,
                "plugin action id matches more than one action; include plugin_id"),
        };

        static PluginResult<(InstalledPlugin, PluginActionInfo)> FailAction(string code, string message) =>
            PluginResult<(InstalledPlugin, PluginActionInfo)>.Fail(code, message);
    }

    /// <summary>
    // Sort enabled plugins
    /// by plugin id. Keep manifest order inside one plugin. First regex match wins.
    /// </summary>
    private (InstalledPlugin Plugin, PluginManifestLinkHandler Handler, PluginManifestAction Action)? FindLinkHandler(
        string url)
    {
        var plugins = _cache
            .Where(plugin => plugin.Enabled && ManifestAvailable(plugin))
            .OrderBy(plugin => plugin.PluginId, StringComparer.Ordinal)
            .ToArray();
        foreach (var plugin in plugins)
        {
            foreach (var handler in plugin.LinkHandlers)
            {
                var handlerPlatform = PluginIdentifiers.EnsurePlatformSupported(
                    PluginIdentifiers.EffectivePlatforms(handler.Platforms, plugin.Platforms),
                    handler.Id);
                if (!handlerPlatform.IsOk)
                    continue;
                var action = plugin.Actions.FirstOrDefault(item => item.Id == handler.Action);
                if (action is null)
                    continue;
                var actionPlatform = PluginIdentifiers.EnsurePlatformSupported(
                    PluginIdentifiers.EffectivePlatforms(action.Platforms, plugin.Platforms),
                    action.Id);
                if (!actionPlatform.IsOk)
                    continue;
                if (!PatternMatches(handler.Pattern, url))
                    continue;
                return (plugin, handler, action);
            }
        }

        return null;
    }

    private static bool PatternMatches(string pattern, string url)
    {
        try
        {
            return Regex.IsMatch(url, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static PluginActionInfo ToActionInfo(InstalledPlugin plugin, PluginManifestAction action) =>
        new()
        {
            PluginId = plugin.PluginId,
            ActionId = action.Id,
            Title = action.Title,
            Description = action.Description,
            Contexts = action.Contexts,
            Command = action.Command,
            Platforms = PluginIdentifiers.EffectivePlatforms(action.Platforms, plugin.Platforms),
            Palette = action.Palette,
            SuggestedKey = action.SuggestedKey,
        };

    private static bool ManifestAvailable(InstalledPlugin plugin)
    {
        foreach (var warning in plugin.Warnings)
        {
            if (warning.StartsWith(ManifestUnavailablePrefix, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private PluginInvocationContext MergeContext(PluginInvocationContext? provided, string correlationId)
    {
        var current = _context.Current(correlationId);
        if (provided is null)
            return current;
        return current with
        {
            WorkspaceId = provided.WorkspaceId ?? current.WorkspaceId,
            WorkspaceLabel = provided.WorkspaceLabel ?? current.WorkspaceLabel,
            WorkspaceCwd = provided.WorkspaceCwd ?? current.WorkspaceCwd,
            Worktree = provided.Worktree ?? current.Worktree,
            TabId = provided.TabId ?? current.TabId,
            TabLabel = provided.TabLabel ?? current.TabLabel,
            FocusedPaneId = provided.FocusedPaneId ?? current.FocusedPaneId,
            FocusedPaneCwd = provided.FocusedPaneCwd ?? current.FocusedPaneCwd,
            FocusedPaneAgent = provided.FocusedPaneAgent ?? current.FocusedPaneAgent,
            FocusedPaneStatus = provided.FocusedPaneStatus ?? current.FocusedPaneStatus,
            AgentSession = IsLinkActivation(provided, correlationId)
                ? provided.AgentSession
                : provided.AgentSession ?? current.AgentSession,
            ProgramName = provided.ProgramName ?? current.ProgramName,
            InvocationSource = provided.InvocationSource ?? current.InvocationSource,
            CorrelationId = provided.CorrelationId ?? current.CorrelationId,
            SelectedText = null,
            ClickedUrl = provided.ClickedUrl ?? current.ClickedUrl,
            LinkHandlerId = provided.LinkHandlerId ?? current.LinkHandlerId,
        };
    }

    /// <summary>
    /// Link context is server-built for the clicked pane. Keep a null session.
    /// Do not fill it from the focused pane.
    /// </summary>
    private static bool IsLinkActivation(PluginInvocationContext provided, string correlationId) =>
        string.Equals(correlationId, "pane.link.activate", StringComparison.Ordinal)
        || string.Equals(provided.InvocationSource, "link_click", StringComparison.Ordinal);

    private PluginResult<PluginCommandLog> StartCommand(
        InstalledPlugin plugin,
        string? actionId,
        string? eventName,
        IReadOnlyList<string> command,
        PluginInvocationContext context,
        string? eventJson)
    {
        if (command.Count == 0)
        {
            return PluginResult<PluginCommandLog>.Fail(
                PluginError.InvalidCommand,
                "command must not be empty");
        }

        if (!PluginIdentifiers.IsAllowedUnixCommand(command[0]))
        {
            return PluginResult<PluginCommandLog>.Fail(
                PluginError.UnsupportedCommand,
                ".ps1 commands are refused until Windows plugin commands ship");
        }

        var dirs = EnsureUserDirs(plugin.PluginId);
        if (!dirs.IsOk)
            return PluginResult<PluginCommandLog>.Fail(dirs.Error);
        var json = PluginContextJson.Serialize(context);
        if (!json.IsOk)
            return PluginResult<PluginCommandLog>.Fail(json.Error);

        var now = UnixMs();
        string logId;
        lock (_gate)
        {
            logId = "plugin-log-" + _nextLogId;
            _nextLogId++;
        }

        var argv = ResolveCommand(command, plugin.PluginRoot);
        if (_inFlight >= PluginCommandLimits.MaxServerInFlight
            || InFlightOf(plugin.PluginId) >= PluginCommandLimits.MaxPerPluginInFlight)
        {
            var message = _inFlight >= PluginCommandLimits.MaxServerInFlight
                ? $"maximum concurrent plugin commands reached ({PluginCommandLimits.MaxServerInFlight})"
                : $"maximum concurrent plugin commands for {plugin.PluginId} reached ({PluginCommandLimits.MaxPerPluginInFlight})";
            var failed = new PluginCommandLog
            {
                LogId = logId,
                PluginId = plugin.PluginId,
                ActionId = actionId,
                Event = eventName,
                Command = argv,
                Status = "failed",
                StartedUnixMs = now,
                FinishedUnixMs = now,
                Error = message,
                Stdout = "",
                Stderr = "",
            };
            PushLog(failed);
            return PluginResult<PluginCommandLog>.Fail(PluginError.CommandLimit, message);
        }

        var env = BuildEnv(plugin, context, actionId, eventName, eventJson, entrypointId: null);
        var running = new PluginCommandLog
        {
            LogId = logId,
            PluginId = plugin.PluginId,
            ActionId = actionId,
            Event = eventName,
            Command = argv,
            Status = "running",
            StartedUnixMs = now,
        };
        PushLog(running);
        IncrementInFlight(plugin.PluginId);
        var program = argv[0];
        var args = argv.Skip(1).ToArray();
        if (!_launcher.TryStart(
                program,
                args,
                plugin.PluginRoot,
                env,
                PluginCommandLimits.OutputMaxBytes,
                exit => FinishCommand(logId, plugin.PluginId, actionId, exit),
                out var error))
        {
            FinishCommand(logId, plugin.PluginId, actionId, new PluginProcessExit(null, "", "", error));
            return PluginResult<PluginCommandLog>.Fail(
                PluginError.InvalidCommand,
                error ?? "plugin command failed to start");
        }

        return PluginResult<PluginCommandLog>.Ok(running);
    }

    private PluginResult<PluginCommandLog> RunRefreshCommand(
        InstalledPlugin plugin,
        IReadOnlyList<string> command,
        PluginInvocationContext context)
    {
        var dirs = EnsureUserDirs(plugin.PluginId);
        if (!dirs.IsOk)
            return PluginResult<PluginCommandLog>.Fail(dirs.Error);
        var json = PluginContextJson.Serialize(context);
        if (!json.IsOk)
            return PluginResult<PluginCommandLog>.Fail(json.Error);

        var now = UnixMs();
        string logId;
        lock (_gate)
        {
            logId = "plugin-log-" + _nextLogId;
            _nextLogId++;
        }

        var argv = ResolveCommand(command, plugin.PluginRoot);
        if (_inFlight >= PluginCommandLimits.MaxServerInFlight
            || InFlightOf(plugin.PluginId) >= PluginCommandLimits.MaxPerPluginInFlight)
        {
            var message = _inFlight >= PluginCommandLimits.MaxServerInFlight
                ? $"maximum concurrent plugin commands reached ({PluginCommandLimits.MaxServerInFlight})"
                : $"maximum concurrent plugin commands for {plugin.PluginId} reached ({PluginCommandLimits.MaxPerPluginInFlight})";
            var failed = new PluginCommandLog
            {
                LogId = logId,
                PluginId = plugin.PluginId,
                Event = "resource.refresh",
                Command = argv,
                Status = "failed",
                StartedUnixMs = now,
                FinishedUnixMs = now,
                Error = message,
                Stdout = "",
                Stderr = "",
            };
            PushLog(failed);
            return PluginResult<PluginCommandLog>.Fail(PluginError.CommandLimit, message);
        }

        var env = BuildEnv(plugin, context, actionId: null, "resource.refresh", eventJson: null, entrypointId: null);
        var running = new PluginCommandLog
        {
            LogId = logId,
            PluginId = plugin.PluginId,
            Event = "resource.refresh",
            Command = argv,
            Status = "running",
            StartedUnixMs = now,
        };
        PushLog(running);
        IncrementInFlight(plugin.PluginId);
        var program = argv[0];
        var args = argv.Skip(1).ToArray();
        PluginProcessExit exit;
        try
        {
            exit = _refreshRunner.Run(
                program,
                args,
                plugin.PluginRoot,
                env,
                PluginCommandLimits.OutputMaxBytes,
                PluginResourceLimits.RefreshTimeout);
        }
        catch (Exception ex)
        {
            exit = new PluginProcessExit(null, "", "", ex.Message);
        }

        FinishCommand(logId, plugin.PluginId, actionId: null, exit);
        lock (_gate)
        {
            foreach (var log in _logs)
            {
                if (log.LogId == logId)
                    return PluginResult<PluginCommandLog>.Ok(log);
            }
        }

        return PluginResult<PluginCommandLog>.Ok(running);
    }

    private void FinishCommand(string logId, string pluginId, string? actionId, PluginProcessExit exit)
    {
        DecrementInFlight(pluginId);
        var finished = UnixMs();
        PluginStructuredActionResult? structured = null;
        if (actionId is not null)
            structured = PluginActionResultParser.TryParse(exit.Stdout);
        var status = exit.Error is not null || exit.ExitCode is not 0 and not null
            ? "failed"
            : "succeeded";
        if (exit.ExitCode is null && exit.Error is null)
            status = "failed";
        lock (_gate)
        {
            for (var i = 0; i < _logs.Count; i++)
            {
                if (_logs[i].LogId != logId)
                    continue;
                _logs[i] = _logs[i] with
                {
                    Status = status,
                    FinishedUnixMs = finished,
                    ExitCode = exit.ExitCode,
                    Pid = exit.Pid,
                    Stdout = exit.Stdout,
                    Stderr = exit.Stderr,
                    Error = exit.Error,
                    Result = structured,
                };
                return;
            }
        }
    }

    private Dictionary<string, string> BuildEnv(
        InstalledPlugin plugin,
        PluginInvocationContext context,
        string? actionId,
        string? eventName,
        string? eventJson,
        string? entrypointId)
    {
        var json = PluginContextJson.Serialize(context);
        var contextJson = json.IsOk ? json.Value : "{}";
        return PluginEnv.Build(
            plugin,
            _paths.PluginConfigDir(plugin.PluginId),
            _paths.PluginStateDir(plugin.PluginId),
            contextJson,
            _tokens.Peek(plugin.PluginId),
            _binPath,
            _socketPath,
            context,
            actionId,
            eventName,
            eventJson,
            entrypointId);
    }

    private static IReadOnlyList<string> ResolveCommand(IReadOnlyList<string> command, string pluginRoot)
    {
        if (command.Count == 0)
            return command;
        var program = command[0];
        if (program.Contains('/') && !Path.IsPathRooted(program))
        {
            var relative = program.StartsWith("./", StringComparison.Ordinal) ? program[2..] : program;
            var resolved = Path.Combine(pluginRoot, relative);
            var copy = command.ToArray();
            copy[0] = resolved;
            return copy;
        }

        return command;
    }

    private void PushLog(PluginCommandLog log)
    {
        lock (_gate)
        {
            _logs.Add(log);
            if (_logs.Count > PluginCommandLimits.LogLimit)
                _logs.RemoveRange(0, _logs.Count - PluginCommandLimits.LogLimit);
        }
    }

    private int InFlightOf(string pluginId)
    {
        lock (_gate)
            return _inFlightByPlugin.TryGetValue(pluginId, out var n) ? n : 0;
    }

    private void IncrementInFlight(string pluginId)
    {
        lock (_gate)
        {
            _inFlight++;
            _inFlightByPlugin[pluginId] = InFlightOfUnlocked(pluginId) + 1;
        }
    }

    private int InFlightOfUnlocked(string pluginId) =>
        _inFlightByPlugin.TryGetValue(pluginId, out var n) ? n : 0;

    private void DecrementInFlight(string pluginId)
    {
        lock (_gate)
        {
            if (_inFlight > 0)
                _inFlight--;
            if (_inFlightByPlugin.TryGetValue(pluginId, out var n))
            {
                if (n <= 1)
                    _inFlightByPlugin.Remove(pluginId);
                else
                    _inFlightByPlugin[pluginId] = n - 1;
            }
        }
    }

    private long UnixMs() =>
        Math.Min(_clock.UtcNow.ToUnixTimeMilliseconds(), long.MaxValue);

    private sealed record PluginPaneRecord(string PluginId, string Entrypoint);

    private sealed class EmptyPluginContextSource : IPluginContextSource
    {
        public PluginInvocationContext Current(string correlationId) =>
            new() { CorrelationId = correlationId, InvocationSource = "api", SelectedText = null };

        public PluginInvocationContext ForEvent(string hookName, string eventJson, string correlationId) =>
            Current(correlationId) with { InvocationSource = hookName };
    }

    private sealed class UnconfiguredPluginRefreshRunner : IPluginRefreshRunner
    {
        public static readonly UnconfiguredPluginRefreshRunner Instance = new();

        public PluginProcessExit Run(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            int outputCapBytes,
            TimeSpan timeout) =>
            new(null, "", "", "refresh runner not configured");
    }
}
