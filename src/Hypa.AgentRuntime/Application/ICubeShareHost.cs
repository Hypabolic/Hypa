using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Cubes;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Mux-owned share listener. Share lasts as long as the mux, not the attach
/// that turned it on, and resumes when the mux starts again.
/// </summary>
public interface ICubeShareHost
{
    CubeShareStatus Status { get; }

    /// <summary>Enable share and wait for the first listener start to settle.</summary>
    Task<CubeShareStatus> StartAsync(CubeShareSettings settings, CancellationToken ct);

    /// <summary>Disable share and stop the listener.</summary>
    Task<CubeShareStatus> StopAsync(CancellationToken ct);
}

/// <summary>Starts one share listener process.</summary>
public interface ICubeShareListenerLauncher
{
    Task<Result<ICubeShareListener, string>> LaunchAsync(
        CubeShareSettings settings,
        CancellationToken ct);
}

/// <summary>A ready share listener. Dispose stops it.</summary>
public interface ICubeShareListener : IAsyncDisposable
{
    CubeShareListen Listen { get; }

    /// <summary>Completes with an exit detail when the listener stops on its own.</summary>
    Task<string> Exited { get; }
}

/// <summary>Persists share intent beside the mux socket.</summary>
public interface ICubeShareSettingsStore
{
    CubeShareSettings? LoadEnabled();

    void SaveEnabled(CubeShareSettings settings);

    void Clear();
}
