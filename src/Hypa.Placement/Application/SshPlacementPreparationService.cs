using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
/// Prepare a remote mux through before catalog persistence.
/// </summary>
public sealed class SshPlacementPreparationService : ISshPlacementPreparation
{
    private static readonly TimeSpan TotalDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IRemoteMuxPath _remoteMux;
    private readonly IRemoteMuxIdentityReader _identityReader;
    private readonly TimeProvider _clock;

    public SshPlacementPreparationService(
        IRemoteMuxPath remoteMux,
        IRemoteMuxIdentityReader identityReader,
        TimeProvider? clock = null)
    {
        _remoteMux = remoteMux ?? throw new ArgumentNullException(nameof(remoteMux));
        _identityReader = identityReader ?? throw new ArgumentNullException(nameof(identityReader));
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<PlacementOutcome<MuxIdentity>> PrepareAsync(
        PeerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var deadline = _clock.GetUtcNow() + TotalDeadline;
        var delay = InitialRetryDelay;
        RemoteMuxOutcome? lastFailure = null;

        while (_clock.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var open = await _remoteMux.OpenAsync(
                    new RemoteMuxOpenRequest
                    {
                        Profile = profile,
                        ManageSshConfig = true,
                        Interactive = false,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (open.Ok && open.Path is not null)
            {
                try
                {
                    var identity = await _identityReader.ReadAsync(
                            open.Path.LocalSocketPath,
                            profile.Session,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return identity;
                }
                finally
                {
                    await open.Path.DisposeAsync().ConfigureAwait(false);
                }
            }

            lastFailure = open;
            if (!IsTransient(open))
                return MapFailure(open);

            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                break;

            var wait = delay < remaining ? delay : remaining;
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            delay = delay * 2;
            if (delay > MaxRetryDelay)
                delay = MaxRetryDelay;
        }

        return lastFailure is null
            ? PlacementOutcome<MuxIdentity>.Failure(
                PlacementReasons.PreparationFailed,
                "Remote preparation timed out.")
            : MapFailure(lastFailure);
    }

    private static bool IsTransient(RemoteMuxOutcome outcome)
    {
        if (outcome.Ok)
            return false;
        return outcome.Reason is RemoteMuxReasons.Internal
            or RemoteMuxReasons.GenerationStale;
    }

    private static PlacementOutcome<MuxIdentity> MapFailure(RemoteMuxOutcome outcome)
    {
        var reason = outcome.Reason switch
        {
            RemoteMuxReasons.ApprovalRequired => PlacementReasons.PreparationFailed,
            RemoteMuxReasons.AuthenticationFailed => PlacementReasons.PreparationFailed,
            RemoteMuxReasons.RestartRequired => PlacementReasons.PreparationFailed,
            RemoteMuxReasons.ProfileInvalid => PlacementReasons.ProfileInvalid,
            RemoteMuxReasons.Incompatible => PlacementReasons.PreparationFailed,
            _ => PlacementReasons.PreparationFailed,
        };

        var detail = outcome.Reason switch
        {
            RemoteMuxReasons.ApprovalRequired => outcome.Detail ?? "",
            RemoteMuxReasons.AuthenticationFailed => outcome.Detail ?? "",
            RemoteMuxReasons.RestartRequired => RemoteMuxReasons.RestartRequired,
            _ => outcome.Detail ?? "Remote preparation failed.",
        };

        if (outcome.Reason == RemoteMuxReasons.RestartRequired)
        {
            return PlacementOutcome<MuxIdentity>.Failure(
                RemoteMuxReasons.RestartRequired,
                "Remote restart requires approval. Active remote processes can stop.");
        }

        if (outcome.Reason == RemoteMuxReasons.ApprovalRequired && outcome.Detail is not null)
            return PlacementOutcome<MuxIdentity>.Failure(RemoteMuxReasons.ApprovalRequired, outcome.Detail);

        if (outcome.Reason == RemoteMuxReasons.AuthenticationFailed && outcome.Detail is not null)
            return PlacementOutcome<MuxIdentity>.Failure(RemoteMuxReasons.AuthenticationFailed, outcome.Detail);

        return PlacementOutcome<MuxIdentity>.Failure(reason, detail);
    }
}
