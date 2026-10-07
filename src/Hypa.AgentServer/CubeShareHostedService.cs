using Hypa.AgentRuntime.Application.Cubes;
using Microsoft.Extensions.Hosting;

namespace Hypa.AgentServer;

/// <summary>
/// Resumes share when the last mux left it enabled, and stops the listener
/// with the mux. Share stays enabled for the next mux.
/// </summary>
public sealed class CubeShareHostedService(CubeShareSupervisor share) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        share.ResumeAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await share.DisposeAsync().ConfigureAwait(false);
}
