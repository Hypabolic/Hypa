using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Validates a peer profile, assigns a connection generation, then opens
/// the provider byte path. Does not reference Continuity or Work.
/// </summary>
public sealed class PeerReachService : IPeerReach
{
    private readonly IBytePath _bytePath;

    public PeerReachService(IBytePath bytePath)
    {
        _bytePath = bytePath ?? throw new ArgumentNullException(nameof(bytePath));
    }

    public async Task<ConnectivityOutcome<BytePathHandle>> ReachAsync(
        PeerReachRequest request,
        PeerConnectionGenerationFence fence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fence);

        var profile = request.Profile;
        var invalid = PeerProfileRules.Validate(profile);
        if (invalid is not null)
            return ConnectivityOutcome<BytePathHandle>.Failure(invalid, "Peer profile is invalid.");

        if (!profile.Enabled)
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                PeerReachReasons.ProfileDisabled,
                "Peer profile is disabled.");
        }

        if (request.RequestLiveHandoff && !RemoteRestartPolicy.LiveHandoffSupportedOnHost())
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                PeerReachReasons.LiveHandoffUnsupported,
                "Live handoff is experimental and Unix-only.");
        }

        if (!string.Equals(profile.Provider, _bytePath.Provider, StringComparison.Ordinal))
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                PeerReachReasons.ProviderUnsupported,
                $"Provider '{profile.Provider}' is not supported by this reach adapter.");
        }

        var generation = fence.BeginAttempt();
        var opened = await _bytePath.OpenAsync(
            new BytePathRequest
            {
                Provider = profile.Provider,
                Target = profile.Target,
                Session = profile.Session,
                ManageSshConfig = request.ManageSshConfig,
            },
            cancellationToken).ConfigureAwait(false);

        if (!opened.Ok)
            return opened;

        if (!fence.TryAccept(generation))
        {
            await _bytePath.CloseAsync(opened.Value!, cancellationToken).ConfigureAwait(false);
            return ConnectivityOutcome<BytePathHandle>.Failure(
                PeerReachReasons.StaleGeneration,
                "Stale connection generation was rejected.");
        }

        return ConnectivityOutcome<BytePathHandle>.Success(opened.Value! with { Generation = generation });
    }
}
