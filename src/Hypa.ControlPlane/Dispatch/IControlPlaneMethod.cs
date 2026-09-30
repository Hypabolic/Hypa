using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.ControlPlane.Dispatch;

/// <summary>One live control-plane method: capability gate + typed invoke.</summary>
public interface IControlPlaneMethod
{
    string Name { get; }

    string RequiredCapability { get; }

    Task<JsonElement> InvokeAsync(
        JsonElement? parameters,
        IClientConnection? connection,
        CancellationToken ct);
}

/// <summary>AOT-safe typed method: params deserialize through <c>ProtocolJsonContext</c>.</summary>
internal sealed class TypedControlPlaneMethod<TParams> : IControlPlaneMethod
    where TParams : class
{
    private readonly JsonTypeInfo<TParams> _typeInfo;
    private readonly Func<TParams, IClientConnection?, CancellationToken, Task<JsonElement>> _handler;

    public TypedControlPlaneMethod(
        string name,
        string requiredCapability,
        JsonTypeInfo<TParams> typeInfo,
        Func<TParams, IClientConnection?, CancellationToken, Task<JsonElement>> handler)
    {
        Name = name;
        RequiredCapability = requiredCapability;
        _typeInfo = typeInfo;
        _handler = handler;
    }

    public string Name { get; }

    public string RequiredCapability { get; }

    public async Task<JsonElement> InvokeAsync(
        JsonElement? parameters,
        IClientConnection? connection,
        CancellationToken ct)
    {
        TParams typed;
        try
        {
            if (parameters is null
                || parameters.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                typed = JsonSerializer.Deserialize("{}", _typeInfo)
                    ?? throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidParams,
                        ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));
            }
            else
            {
                typed = JsonSerializer.Deserialize(parameters.Value, _typeInfo)
                    ?? throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidParams,
                        ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));
            }
        }
        catch (JsonException)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));
        }

        return await _handler(typed, connection, ct).ConfigureAwait(false);
    }
}
