namespace Hypa.AgentRuntime.Domain.Plugins;

/// <summary>
/// Reserved grant names. Parse at link. Advertise none until a handler ships.
/// Unknown names fail closed.
/// </summary>
public static class PluginGrantCatalog
{
    public const string ResourcePublish = "resource.publish";
    public const string ResourceRead = "resource.read";
    public const string NotificationRequest = "notification.request";
    public const string ActionInvokeSelf = "action.invoke:self";
    public const string PaneOpenSelf = "pane.open:self";
    public const string PaneSendTextSelf = "pane.send_text:self";

    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        ResourcePublish,
        ResourceRead,
        NotificationRequest,
        ActionInvokeSelf,
        PaneOpenSelf,
        PaneSendTextSelf,
    };

    /// <summary>
    /// Grants with a shipped handler. Reserved names still parse at link.
    /// </summary>
    public static readonly IReadOnlySet<string> Advertised = new HashSet<string>(StringComparer.Ordinal)
    {
        ActionInvokeSelf,
        PaneOpenSelf,
        PaneSendTextSelf,
        ResourcePublish,
        ResourceRead,
        NotificationRequest,
    };

    /// <summary>
    /// Methods a plugin token may call. Everything else stays on the human CLI.
    /// Direct <c>pane.send_text</c> stays off this list.
    /// </summary>
    public static readonly IReadOnlySet<string> DispatchAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "plugin.action.list",
        "plugin.action.invoke",
        "plugin.log.list",
        "plugin.pane.open",
        "plugin.pane.focus",
        "plugin.pane.close",
        "plugin.pane.send_text",
        "plugin.resource.list",
        "plugin.resource.get",
        "plugin.resource.publish",
        "plugin.resource.remove",
        "notification.show",
        "pane.report_agent",
        "pane.report_agent_session",
        "pane.report_metadata",
        "pane.release_agent",
        "pane.clear_agent_authority",
    };

    public static bool IsKnown(string name) => Reserved.Contains(name);

    public static bool IsDispatchAllowed(string method) => DispatchAllowlist.Contains(method);

    public static bool IsAuthorityMethod(string method) =>
        string.Equals(method, "pane.report_agent", StringComparison.Ordinal)
        || string.Equals(method, "pane.report_agent_session", StringComparison.Ordinal)
        || string.Equals(method, "pane.report_metadata", StringComparison.Ordinal)
        || string.Equals(method, "pane.release_agent", StringComparison.Ordinal)
        || string.Equals(method, "pane.clear_agent_authority", StringComparison.Ordinal);
}
