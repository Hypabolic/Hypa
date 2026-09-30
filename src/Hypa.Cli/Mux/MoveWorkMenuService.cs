using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypa.Continuity.Harnesses.Fake;
using Hypa.Continuity.Harnesses.Pi;
using Hypa.Continuity.Infrastructure;
using Hypa.Placement;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;

namespace Hypa.Cli.Mux;

/// <summary>
/// Cubes row and pane menu Move Work. Same handoff as
/// <c>hypa work handoff --to &lt;placement-id&gt;</c>.
/// </summary>
public interface IMoveWorkMenu
{
    Task<MoveWorkMenuOutcome> RunAsync(
        MoveWorkMenuRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record MoveWorkMenuRequest
{
    public required string DestPlacementId { get; init; }
    public string? SourcePaneId { get; init; }
    public string? SourceWorkspaceId { get; init; }
    public required string SourceMuxSocketPath { get; init; }
    public required IReadOnlyList<OccupantPaneSnapshot> Panes { get; init; }
    public string? ContinuityStoreDir { get; init; }
    public string? PlacementStoreDir { get; init; }
    public IReadOnlyList<ActiveWorkRun>? ActiveWorks { get; init; }
    public IWorkService? Works { get; init; }
    public IHandoffService? Handoff { get; init; }
    public IPlacementDirectory? Directory { get; init; }
    public IDestWorkExecutor? DestExecutor { get; init; }
    public ISourceOccupantStopper? SourceStopper { get; init; }
}

public sealed record MoveWorkMenuOutcome
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public HandoffResult? Handoff { get; init; }
    public string? DestPlacementId { get; init; }
    public PlacementHandoffRequest? Command { get; init; }

    public static MoveWorkMenuOutcome Fail(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };
}

public sealed class MoveWorkMenuService : IMoveWorkMenu
{
    public const string DestWorkerNotJoinedDetail = "dest worker session is not joined";

    private readonly IPlacementHandoffRunner _runner;

    public MoveWorkMenuService()
        : this(new PlacementHandoffRunner())
    {
    }

    public MoveWorkMenuService(IPlacementHandoffRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<MoveWorkMenuOutcome> RunAsync(
        MoveWorkMenuRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlacementId.TryParse(request.DestPlacementId, out var destPlacementId))
        {
            return MoveWorkMenuOutcome.Fail(
                ContinuityReasons.PeerUnavailable,
                "placement is not in the directory");
        }

        IAsyncDisposable? storeLifetime = null;
        IWorkService works;
        if (request.Works is not null)
        {
            works = request.Works;
        }
        else
        {
            var store = new SqliteContinuityStore(
                request.ContinuityStoreDir ?? DefaultContinuityStoreDir());
            storeLifetime = store;
            works = new WorkService(store);
        }

        try
        {
            var active = request.ActiveWorks
                ?? await works.ListActiveWorksAsync(cancellationToken).ConfigureAwait(false);
            var owner = AdoptedWorkOwnerResolver.Resolve(
                active,
                request.Panes,
                request.SourceMuxSocketPath,
                request.SourcePaneId,
                request.SourceWorkspaceId);
            if (!owner.Ok || owner.Owner is null)
            {
                return MoveWorkMenuOutcome.Fail(
                    owner.Reason ?? ContinuityReasons.WorkNotAdopted,
                    owner.Detail ?? AdoptedWorkOwnerResolver.AdoptRequiredDetail);
            }

            if (request.DestExecutor is null)
            {
                return MoveWorkMenuOutcome.Fail(
                    ContinuityReasons.PeerUnavailable,
                    DestWorkerNotJoinedDetail);
            }

            if (string.IsNullOrWhiteSpace(owner.Owner.Run.Home)
                || string.IsNullOrWhiteSpace(owner.Owner.Run.Cwd))
            {
                return MoveWorkMenuOutcome.Fail(
                    ContinuityReasons.Internal,
                    "adopted Work home or cwd is unreadable");
            }

            var harness = CreateHarness(owner.Owner.HarnessAdapterId);
            if (harness is null)
            {
                return MoveWorkMenuOutcome.Fail(
                    ContinuityReasons.HarnessUncertified,
                    "pane is not a certified harness");
            }

            var directory = request.Directory
                ?? new PlacementDirectoryService(
                    new FilePlacementDirectoryStore(
                        request.PlacementStoreDir ?? PlacementStatePaths.ResolveFromEnvironment()));
            var handoff = request.Handoff
                ?? new HandoffService(
                    works,
                    new GitWorkspacePacker(),
                    new WorkPackCodec(),
                    new LocalWorkPackSpool());
            var command = new PlacementHandoffRequest
            {
                WorkId = owner.Owner.WorkId,
                Harness = harness,
                SourceHome = owner.Owner.Run.Home,
                SourceWorkspace = owner.Owner.Run.Cwd,
                SourceMuxEndpoint = owner.Owner.Run.MuxEndpoint,
                DestPlacementId = destPlacementId,
                DestExecutor = request.DestExecutor,
                Handoff = handoff,
                Directory = directory,
                SourcePaneId = owner.Owner.PaneId,
                SourceStopper = request.SourceStopper ?? new ProtocolSourceOccupantStopper(),
                OccupantStreaming = owner.Owner.OccupantStreaming,
                RequireDestStart = true,
            };
            var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return new MoveWorkMenuOutcome
                {
                    Ok = false,
                    Reason = result.Reason,
                    Detail = result.Detail,
                    Handoff = result,
                    Command = command,
                };
            }

            return new MoveWorkMenuOutcome
            {
                Ok = true,
                Handoff = result,
                DestPlacementId = destPlacementId.Value,
                Command = command,
            };
        }
        finally
        {
            if (storeLifetime is not null)
                await storeLifetime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IHarnessAdapter? CreateHarness(string id)
    {
        if (string.Equals(id, CertifiedHarnessIds.Pi, StringComparison.OrdinalIgnoreCase))
            return new PiHarnessAdapter();
        if (string.Equals(id, CertifiedHarnessIds.Fake, StringComparison.OrdinalIgnoreCase))
            return new FakeHarnessAdapter();
        return null;
    }

    internal static string DefaultContinuityStoreDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg, "hypa", "continuity");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "hypa", "continuity");
    }
}
