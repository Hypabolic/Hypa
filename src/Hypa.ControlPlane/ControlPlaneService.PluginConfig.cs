using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    /// <summary>
    /// Serializes <c>config.changed</c> sequence allocation and live fanout.
    /// A later caller waits until the earlier sequence is published.
    /// </summary>
    private readonly SemaphoreSlim _configChangedEmitGate = new(1, 1);

    /// <summary>
    /// Serializes allocation and fanout for the other type-keyed live events
    /// (<c>pane.input_rejected</c>, <c>resource.changed</c>) and the
    /// synchronous posts (<c>config.reloaded</c>, input-undeliverable notices).
    /// </summary>
    private readonly SemaphoreSlim _paneInputRejectedEmitGate = new(1, 1);

    private readonly SemaphoreSlim _resourceChangedEmitGate = new(1, 1);

    private readonly object _configReloadedEmitGate = new();

    private readonly object _inputUndeliverableEmitGate = new();

    internal Task<JsonElement> HandlePluginConfigGetAsync(PluginConfigGetParams p, CancellationToken ct)
    {
        _ = ct;
        var pluginId = RequireField(p.PluginId, "plugin_id");
        var got = _plugins.GetSettings(pluginId);
        if (!got.IsOk)
            throw PluginFault(got.Error);
        return Task.FromResult(OkTyped(
            new PluginConfigGetResult
            {
                PluginId = got.Value.PluginId,
                Values = new Dictionary<string, string>(got.Value.Values, StringComparer.Ordinal),
            },
            ProtocolJsonContext.Default.PluginConfigGetResult));
    }

    internal Task<JsonElement> HandlePluginConfigSetAsync(PluginConfigSetParams p, CancellationToken ct)
    {
        _ = ct;
        EnsureNotFrozenForMutation(ProtocolMethods.PluginConfigSet);
        var pluginId = RequireField(p.PluginId, "plugin_id");
        var key = RequireField(p.Key, "key");
        var value = p.Value ?? "";
        var wrote = _plugins.SetSettings(pluginId, key, value);
        if (!wrote.IsOk)
            throw PluginFault(wrote.Error);
        if (wrote.Value.Applied)
            _ = EmitConfigChangedAsync(pluginId, key, wrote.Value.Values[key], ct);
        return Task.FromResult(OkTyped(
            new PluginConfigSetResult
            {
                PluginId = pluginId,
                Key = key,
                Applied = wrote.Value.Applied,
                Values = new Dictionary<string, string>(wrote.Value.Values, StringComparer.Ordinal),
            },
            ProtocolJsonContext.Default.PluginConfigSetResult));
    }

    private async Task EmitConfigChangedAsync(string pluginId, string key, string value, CancellationToken ct)
    {
        if (_subscriptions is null)
            return;

        try
        {
            var payload = RuntimeEventPayloadJson.WriteConfigChanged(pluginId, key, value);
            var occurredAt = _time.GetUtcNow();
            await EmitInAllocationOrderAsync(
                _configChangedEmitGate,
                () => _paneRenderSeq.AddOrUpdate(
                    ProtocolEventTypes.ConfigChanged, 1L, static (_, prev) => prev + 1),
                async (seq, token) =>
                {
                    var rec = new RuntimeEventRecord
                    {
                        Seq = seq,
                        Class = EventClass.Lifecycle,
                        Reliability = EventReliability.Reliable,
                        Type = ProtocolEventTypes.ConfigChanged,
                        OccurredAt = occurredAt,
                        PayloadJson = payload,
                    };
                    await _subscriptions.FanoutLiveAsync(rec, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "config.changed emit failed");
        }
    }

    /// <summary>
    /// Allocates the next sequence and publishes it before another caller on
    /// <paramref name="gate"/> can allocate. Stops a higher sequence from
    /// reaching the live high-water mark first.
    /// </summary>
    internal static async Task EmitInAllocationOrderAsync(
        SemaphoreSlim gate,
        Func<long> nextSeq,
        Func<long, CancellationToken, Task> publish,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(nextSeq);
        ArgumentNullException.ThrowIfNull(publish);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seq = nextSeq();
            await publish(seq, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Synchronous form of <see cref="EmitInAllocationOrderAsync"/> for producers
    /// that post with <c>PostLive</c>. The post enqueues in call order, so holding
    /// <paramref name="gate"/> across allocation and post keeps the live key in order.
    /// </summary>
    internal static void EmitInAllocationOrder(
        object gate,
        Func<long> nextSeq,
        Action<long> publish)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(nextSeq);
        ArgumentNullException.ThrowIfNull(publish);
        lock (gate)
        {
            var seq = nextSeq();
            publish(seq);
        }
    }
}
