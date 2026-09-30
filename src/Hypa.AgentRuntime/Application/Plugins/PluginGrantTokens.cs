using System.Security.Cryptography;
using System.Text;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Application.Plugins;

internal sealed class PluginGrantTokens
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GrantRecord> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _plaintextByPlugin = new(StringComparer.Ordinal);

    public string Issue(
        string pluginId,
        long enableGeneration,
        IReadOnlyList<string> grants,
        DateTimeOffset expiresAt)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = "h32." + Convert.ToHexString(bytes).ToLowerInvariant();
        var hash = Hash(token);
        lock (_gate)
        {
            RevokeUnlocked(pluginId);
            _byHash[hash] = new GrantRecord(pluginId, enableGeneration, [.. grants], expiresAt);
            _plaintextByPlugin[pluginId] = token;
        }

        return token;
    }

    public void Revoke(string pluginId)
    {
        lock (_gate)
            RevokeUnlocked(pluginId);
    }

    public string? Peek(string pluginId)
    {
        lock (_gate)
            return _plaintextByPlugin.TryGetValue(pluginId, out var token) ? token : null;
    }

    public PluginGrantDecision Check(
        string? token,
        string method,
        string? source,
        string? targetPluginId,
        bool pluginConnection,
        DateTimeOffset now,
        Func<string, long> enableGenerationOf)
    {
        // plugin:<id> on the authority source field is identity.
        // It does not mean this RPC arrived on a plugin socket.
        var pluginAttributed = pluginConnection;

        if (string.IsNullOrWhiteSpace(token))
        {
            if (pluginAttributed)
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            if (string.Equals(method, ProtocolMethods.PluginPaneSendText, StringComparison.Ordinal))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            return PluginGrantDecision.Allow();
        }

        GrantRecord? record;
        lock (_gate)
            _byHash.TryGetValue(Hash(token), out record);

        if (record is null)
            return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
        if (record.ExpiresAt <= now)
            return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
        if (record.EnableGeneration != enableGenerationOf(record.PluginId))
            return PluginGrantDecision.Deny(PluginError.CapabilityMissing);

        if (!PluginGrantCatalog.IsDispatchAllowed(method))
            return PluginGrantDecision.Deny(PluginError.CapabilityMissing);

        if (!string.IsNullOrWhiteSpace(source))
        {
            if (PluginIdentifiers.IsOfficialSource(source))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            if (!PluginIdentifiers.TryPluginSource(source, out var sourcePlugin)
                || !string.Equals(sourcePlugin, record.PluginId, StringComparison.Ordinal))
            {
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            }
        }
        else if (PluginGrantCatalog.IsAuthorityMethod(method))
        {
            return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
        }

        if (string.Equals(method, "plugin.action.invoke", StringComparison.Ordinal))
        {
            if (!record.Grants.Contains(PluginGrantCatalog.ActionInvokeSelf))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            if (!OwnsTarget(record.PluginId, targetPluginId))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
        }

        if (string.Equals(method, "plugin.pane.open", StringComparison.Ordinal)
            || string.Equals(method, "plugin.pane.focus", StringComparison.Ordinal)
            || string.Equals(method, "plugin.pane.close", StringComparison.Ordinal))
        {
            if (!record.Grants.Contains(PluginGrantCatalog.PaneOpenSelf))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            if (!OwnsTarget(record.PluginId, targetPluginId))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
        }

        if (string.Equals(method, ProtocolMethods.PluginPaneSendText, StringComparison.Ordinal))
        {
            if (!record.Grants.Contains(PluginGrantCatalog.PaneSendTextSelf))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            if (!OwnsTarget(record.PluginId, targetPluginId))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
        }

        if (string.Equals(method, ProtocolMethods.PluginResourceList, StringComparison.Ordinal)
            || string.Equals(method, ProtocolMethods.PluginResourceGet, StringComparison.Ordinal))
        {
            if (!record.Grants.Contains(PluginGrantCatalog.ResourceRead))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
        }

        if (string.Equals(method, ProtocolMethods.PluginResourcePublish, StringComparison.Ordinal)
            || string.Equals(method, ProtocolMethods.PluginResourceRemove, StringComparison.Ordinal))
        {
            if (!record.Grants.Contains(PluginGrantCatalog.ResourcePublish))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            if (targetPluginId is not null && !OwnsTarget(record.PluginId, targetPluginId))
                return PluginGrantDecision.Deny(PluginError.SourceDenied);
        }

        if (string.Equals(method, ProtocolMethods.NotificationShow, StringComparison.Ordinal))
        {
            if (!record.Grants.Contains(PluginGrantCatalog.NotificationRequest))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            if (string.IsNullOrWhiteSpace(source)
                || !PluginIdentifiers.TryPluginSource(source, out var toastSourcePlugin)
                || !string.Equals(toastSourcePlugin, record.PluginId, StringComparison.Ordinal))
            {
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
            }
        }

        if (string.Equals(method, "plugin.action.list", StringComparison.Ordinal)
            || string.Equals(method, "plugin.log.list", StringComparison.Ordinal))
        {
            if (!OwnsTarget(record.PluginId, targetPluginId))
                return PluginGrantDecision.Deny(PluginError.CapabilityMissing);
        }

        return PluginGrantDecision.Allow(record.PluginId);
    }

    private void RevokeUnlocked(string pluginId)
    {
        if (_plaintextByPlugin.Remove(pluginId, out var old))
            _byHash.Remove(Hash(old));
        foreach (var pair in _byHash.ToArray())
        {
            if (string.Equals(pair.Value.PluginId, pluginId, StringComparison.Ordinal))
                _byHash.Remove(pair.Key);
        }
    }

    private static bool OwnsTarget(string pluginId, string? targetPluginId) =>
        !string.IsNullOrWhiteSpace(targetPluginId)
        && string.Equals(targetPluginId, pluginId, StringComparison.Ordinal);

    private static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private sealed record GrantRecord(
        string PluginId,
        long EnableGeneration,
        HashSet<string> Grants,
        DateTimeOffset ExpiresAt);
}
