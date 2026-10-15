using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.UnitTests.Mux;

internal static class ConnectResultTestExtensions
{
    /// <summary>The endpoint of a successful resolve. Null on failure.</summary>
    public static IAttachEndpoint? EndpointOrNull(
        this Hypa.Runtime.Domain.Common.Result<IAttachEndpoint, ConnectError> result) =>
        result.IsOk ? result.Value : null;
}
