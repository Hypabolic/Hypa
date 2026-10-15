namespace Hypa.AgentRuntime.Domain.Cubes.Connections;

/// <summary>
/// Identity of one endpoint connection: the endpoint id and the server
/// generation. Generation 0 is unspecified.
/// </summary>
public readonly record struct ConnectionId(string EndpointId, ulong Generation)
{
    /// <summary>True when the generation is known.</summary>
    public bool IsKnown => Generation > 0;

    public override string ToString() => $"{EndpointId}#{Generation}";
}
