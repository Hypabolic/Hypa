using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Plugins;
using Hypa.Cli.Attach.Sidebar;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

public sealed partial class AttachSession
{
    internal static async Task RefreshLinkedPluginsAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        try
        {
            var listed = await control.CallAsync(ProtocolMethods.PluginList, null, ct)
                .ConfigureAwait(false);
            live.LinkedPluginIds = SidebarLiveModel.ReadLinkedPluginIds(listed)
                .ToHashSet(StringComparer.Ordinal);
            live.LinkedPlugins = InstalledPluginWireMapper.FromListResult(listed);
        }
        catch (ControlPlaneException)
        {
            live.LinkedPluginIds ??= new HashSet<string>(StringComparer.Ordinal);
        }
    }

    internal static async Task InvokeCollectionActionAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        SidebarCollectionActivation activation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(activation);
        if (string.IsNullOrWhiteSpace(activation.ActionId))
            return;

        var body = new JsonObject
        {
            ["plugin_id"] = activation.PluginId,
            ["action_id"] = activation.ActionId,
            ["resource_id"] = activation.ResourceId,
            ["revision"] = activation.Revision,
            ["context"] = new JsonObject
            {
                ["collection_item_id"] = activation.ItemId,
                ["collection_section_id"] = activation.SectionId,
            },
        };
        await control.CallAsync(ProtocolMethods.PluginActionInvoke, body, ct)
            .ConfigureAwait(false);
    }
}
