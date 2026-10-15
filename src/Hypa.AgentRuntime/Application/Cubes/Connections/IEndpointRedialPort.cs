namespace Hypa.AgentRuntime.Application.Cubes.Connections;

/// <summary>
/// Starts one redial of a lost endpoint. The supervisor calls it. It does
/// not pass through the cube click path. The outcome arrives later as a
/// commit or a failed connect.
/// </summary>
public interface IEndpointRedialPort
{
    Task RedialAsync(string endpointId, CancellationToken ct);
}
