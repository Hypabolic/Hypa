namespace Hypa.AgentRuntime.Domain.AttachConfig;

public enum SidebarCollapsedMode
{
    Compact,
    Hidden,
}

public enum HostCursorMode
{
    Auto,
    Native,
    Drawn,
}

public enum TabBarPosition
{
    Top,
    Bottom,
}

public enum AgentPanelSort
{
    Spaces,
    Priority,
}

public enum StatusIndicatorStyle
{
    Dots,
    Symbols,
}

public enum ToastDelivery
{
    Off,
    Hypa,
    Terminal,
    System,
}

public enum ToastPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum SoundAgentMode
{
    Default,
    On,
    Off,
}

public enum TabBarRightKind
{
    Zoom,
    Hostname,
    DateTime,
    Text,
    Command,
    Resource,
}

public enum ToastSourceMode
{
    On,
    Off,
}

public sealed record AttachTabBarRightEntry
{
    public TabBarRightKind Kind { get; init; }
    public string? Format { get; init; }
    public string? Text { get; init; }
    public string? Command { get; init; }
    public int? IntervalSeconds { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string? Resource { get; init; }
    public int? MaxItems { get; init; }

    public static AttachTabBarRightEntry Zoom { get; } = new() { Kind = TabBarRightKind.Zoom };

    public static AttachTabBarRightEntry Hostname { get; } = new() { Kind = TabBarRightKind.Hostname };

    public static AttachTabBarRightEntry DateTime { get; } = new() { Kind = TabBarRightKind.DateTime };

    public static AttachTabBarRightEntry ForDateTime(string? format) =>
        string.IsNullOrEmpty(format)
            ? DateTime
            : new() { Kind = TabBarRightKind.DateTime, Format = format };

    public static AttachTabBarRightEntry ForText(string text) =>
        new() { Kind = TabBarRightKind.Text, Text = text };

    public static AttachTabBarRightEntry ForCommand(
        string command,
        int? intervalSeconds = null,
        int? timeoutSeconds = null) =>
        new()
        {
            Kind = TabBarRightKind.Command,
            Command = command,
            IntervalSeconds = intervalSeconds,
            TimeoutSeconds = timeoutSeconds,
        };

    public static AttachTabBarRightEntry ForResource(
        string resource,
        string? format = null,
        int? maxItems = null) =>
        new()
        {
            Kind = TabBarRightKind.Resource,
            Resource = resource,
            Format = format,
            MaxItems = maxItems,
        };
}

public sealed record AttachUiConfig
{
    public int SidebarWidth { get; init; } = 26;
    public int SidebarMinWidth { get; init; } = 18;
    public int SidebarMaxWidth { get; init; } = 36;
    public bool SidebarStartCollapsed { get; init; }
    public SidebarCollapsedMode SidebarCollapsedMode { get; init; } = SidebarCollapsedMode.Compact;
    public int MobileWidthThreshold { get; init; } = 64;
    public bool MouseCapture { get; init; } = true;
    public bool CopyOnSelect { get; init; } = true;
    public HostCursorMode HostCursor { get; init; } = HostCursorMode.Auto;
    public string RightClickPassthroughModifier { get; init; } = "";
    public bool RedrawOnFocusGained { get; init; } = true;
    public int MouseScrollLines { get; init; } = 3;
    public bool ConfirmClose { get; init; } = true;
    public bool PromptNewTabName { get; init; } = true;
    public bool PromptNewWorkspaceName { get; init; }
    public bool PaneBorders { get; init; } = true;
    public bool PaneOuterBorders { get; init; } = true;
    public bool PaneScrollbars { get; init; } = true;
    public bool PaneGaps { get; init; } = true;
    public ChromeGlyphSet Glyphs { get; init; } = ChromeGlyphSet.Unicode;
    public bool ShowAgentLabelsOnPaneBorders { get; init; }
    public bool HideTabBarWhenSingleTab { get; init; }
    public TabBarPosition TabBarPosition { get; init; } = TabBarPosition.Top;
    public IReadOnlyList<AttachTabBarRightEntry> TabBarRight { get; init; } = [];
    public string TabBarRightSeparator { get; init; } = " ";
    public string WindowTitle { get; init; } = "{hostname}: {workspace}";
    public AgentPanelSort AgentPanelSort { get; init; } = AgentPanelSort.Spaces;
    public StatusIndicatorStyle StatusIndicators { get; init; } = StatusIndicatorStyle.Dots;
    public AttachSidebarConfig Sidebar { get; init; } = AttachSidebarConfig.Default;
    public string Accent { get; init; } = "cyan";
    /// <summary>When true, attach shows the cube splash at start.</summary>
    public bool StartupSplash { get; init; } = true;
    public AttachToastConfig Toast { get; init; } = AttachToastConfig.Default;
    public AttachSoundConfig Sound { get; init; } = AttachSoundConfig.Default;

    public static AttachUiConfig Default { get; } = new();
}

public sealed record AttachSidebarConfig
{
    public AttachSidebarSectionConfig Agents { get; init; } = AttachSidebarSectionConfig.AgentsDefault;
    public AttachSidebarSectionConfig Spaces { get; init; } = AttachSidebarSectionConfig.SpacesDefault;
    public AttachSidebarSectionConfig Cubes { get; init; } = AttachSidebarSectionConfig.CubesDefault;
    public IReadOnlyList<AttachSidebarSectionOverride> Sections { get; init; } = [];

    public static AttachSidebarConfig Default { get; } = new();
}

public enum AttachSidebarSectionSort
{
    Order,
    Attention,
}

public sealed record AttachSidebarSectionOverride
{
    public string Id { get; init; } = "";
    public int Order { get; init; }
    public bool Collapsed { get; init; }
    public IReadOnlyList<IReadOnlyList<SidebarTokenSpec>> Rows { get; init; } = [];
    public bool RowsSpecified { get; init; }
    public string? Resource { get; init; }
    public string? Title { get; init; }
    public AttachSidebarSectionSort Sort { get; init; } = AttachSidebarSectionSort.Order;
    public int RowGap { get; init; }
}

public sealed record AttachSidebarSectionConfig
{
    public int RowGap { get; init; }
    public int Order { get; init; }
    public bool Collapsed { get; init; }
    public IReadOnlyList<IReadOnlyList<SidebarTokenSpec>> Rows { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<SidebarTokenSpec>>> RowsByAgent { get; init; } =
        new Dictionary<string, IReadOnlyList<IReadOnlyList<SidebarTokenSpec>>>(StringComparer.Ordinal);

    public static AttachSidebarSectionConfig AgentsDefault { get; } = new()
    {
        Order = 0,
        Rows =
        [
            ["state_icon", "workspace", "tab"],
            ["agent"],
        ],
    };

    public static AttachSidebarSectionConfig SpacesDefault { get; } = new()
    {
        Order = 1,
        Rows =
        [
            ["state_icon", "workspace"],
            ["branch", "git_status"],
        ],
    };

    public static AttachSidebarSectionConfig CubesDefault { get; } = new()
    {
        Order = 2,
        Rows =
        [
            ["name", "kind"],
            ["reachability", "work_title"],
        ],
    };
}

public sealed record AttachToastConfig
{
    /// <summary>
    /// Server kill-switch for <c>notification.show</c>. False returns reason
    /// <c>disabled</c>. Distinct from client <see cref="Delivery"/> = off.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public ToastDelivery Delivery { get; init; } = ToastDelivery.Off;
    public int DelaySeconds { get; init; } = 1;
    public ToastPosition HypaPosition { get; init; } = ToastPosition.BottomRight;
    public bool ClipboardEnabled { get; init; } = true;
    public ToastPosition ClipboardPosition { get; init; } = ToastPosition.BottomCenter;

    /// <summary>Per-source mute. Key is a toast source id. Off suppresses that source.</summary>
    public IReadOnlyDictionary<string, ToastSourceMode> Sources { get; init; } =
        new Dictionary<string, ToastSourceMode>(StringComparer.Ordinal);

    public static AttachToastConfig Default { get; } = new();
}

public sealed record AttachSoundConfig
{
    public bool Enabled { get; init; } = true;
    public string? Path { get; init; }
    public string? DonePath { get; init; }
    public string? RequestPath { get; init; }
    public IReadOnlyDictionary<string, SoundAgentMode> Agents { get; init; } =
        new Dictionary<string, SoundAgentMode>(StringComparer.Ordinal)
        {
            ["pi"] = SoundAgentMode.Default,
            ["claude"] = SoundAgentMode.Default,
            ["codex"] = SoundAgentMode.Default,
            ["gemini"] = SoundAgentMode.Default,
            ["cursor"] = SoundAgentMode.Default,
            ["devin"] = SoundAgentMode.Default,
            ["agy"] = SoundAgentMode.Default,
            ["cline"] = SoundAgentMode.Default,
            ["open_code"] = SoundAgentMode.Default,
            ["github_copilot"] = SoundAgentMode.Default,
            ["kimi"] = SoundAgentMode.Default,
            ["kiro"] = SoundAgentMode.Default,
            ["droid"] = SoundAgentMode.Off,
            ["amp"] = SoundAgentMode.Default,
            ["grok"] = SoundAgentMode.Default,
            ["hermes"] = SoundAgentMode.Default,
            ["kilo"] = SoundAgentMode.Default,
            ["qodercli"] = SoundAgentMode.Default,
            ["maki"] = SoundAgentMode.Default,
        };

    public static AttachSoundConfig Default { get; } = new();
}
