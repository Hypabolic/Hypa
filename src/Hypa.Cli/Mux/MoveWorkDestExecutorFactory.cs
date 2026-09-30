using Hypa.Cli.Commands.Work;
using Hypa.Connectivity.Domain;
using Hypa.Continuity.Application;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Cli.Mux;

/// <summary>
/// Dest-specific worker for Move Work. Same join inputs as CLI
/// <c>handoff --to</c> and Cubes Connect.
/// </summary>
public interface IMoveWorkDestExecutorFactory
{
    ValueTask<IDestWorkExecutor?> CreateAsync(
        string destPlacementId,
        CancellationToken cancellationToken = default);
}

/// <summary>Worker bound to one selected Placement id.</summary>
public sealed class JoinedDestWorkExecutor : IDestWorkExecutor
{
    public JoinedDestWorkExecutor(
        string destPlacementId,
        IDestWorkExecutor inner,
        JoinBootstrap? bootstrap = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPlacementId);
        DestPlacementId = destPlacementId.Trim();
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Bootstrap = bootstrap;
    }

    public string DestPlacementId { get; }

    public IDestWorkExecutor Inner { get; }

    public JoinBootstrap? Bootstrap { get; }

    public ValueTask<DestApplyResult> ApplyAsync(
        DestApplyInvocation invocation,
        CancellationToken cancellationToken = default) =>
        Inner.ApplyAsync(invocation, cancellationToken);
}

/// <summary>
/// Builds a dest-specific worker from Cubes Connect join material.
/// The selected Placement id is the join target.
/// </summary>
public sealed class JoinMaterialMoveWorkDestExecutorFactory : IMoveWorkDestExecutorFactory
{
    private readonly IPlacementDirectory? _directory;
    private readonly Func<IPlacementDirectory> _directoryFactory;
    private readonly ICubesConnectJoinMaterialSource _joinMaterial;
    private readonly Func<CubesConnectJoinMaterial, IDestWorkExecutor> _create;

    public JoinMaterialMoveWorkDestExecutorFactory()
        : this(new EnvironmentCubesConnectJoinMaterialSource())
    {
    }

    [ActivatorUtilitiesConstructor]
    public JoinMaterialMoveWorkDestExecutorFactory(ICubesConnectJoinMaterialSource joinMaterial)
        : this(joinMaterial, DefaultCreate, directory: null)
    {
    }

    public JoinMaterialMoveWorkDestExecutorFactory(
        ICubesConnectJoinMaterialSource joinMaterial,
        IPlacementDirectory directory)
        : this(joinMaterial, DefaultCreate, directory)
    {
    }

    public JoinMaterialMoveWorkDestExecutorFactory(
        ICubesConnectJoinMaterialSource joinMaterial,
        Func<CubesConnectJoinMaterial, IDestWorkExecutor> create,
        IPlacementDirectory? directory = null,
        Func<IPlacementDirectory>? directoryFactory = null)
    {
        _joinMaterial = joinMaterial ?? throw new ArgumentNullException(nameof(joinMaterial));
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _directory = directory;
        _directoryFactory = directoryFactory ?? OpenDirectory;
    }

    public JoinMaterialMoveWorkDestExecutorFactory WithDirectory(IPlacementDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new JoinMaterialMoveWorkDestExecutorFactory(
            _joinMaterial,
            _create,
            directory,
            _directoryFactory);
    }

    public async ValueTask<IDestWorkExecutor?> CreateAsync(
        string destPlacementId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Hypa.Placement.Domain.PlacementId.TryParse(destPlacementId, out var id))
            return null;

        var placement = await ResolveLivePlacementAsync(id, cancellationToken).ConfigureAwait(false);
        if (placement is null)
            return null;

        CubesConnectJoinMaterial? material;
        try
        {
            material = await _joinMaterial.GetAsync(placement, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }

        if (material is null)
            return null;

        return new JoinedDestWorkExecutor(
            id.Value,
            _create(material),
            material.Bootstrap);
    }

    private async ValueTask<PlacementRecord?> ResolveLivePlacementAsync(
        Hypa.Placement.Domain.PlacementId id,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = _directory ?? _directoryFactory();
            var found = await directory.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (!found.Ok || found.Value is null)
                return null;
            return found.Value;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static IPlacementDirectory OpenDirectory() =>
        new PlacementDirectoryService(
            new FilePlacementDirectoryStore(PlacementStatePaths.ResolveFromEnvironment()));

    private static IDestWorkExecutor DefaultCreate(CubesConnectJoinMaterial material) =>
        new ConnectivityDestWorkExecutor(
            new ConnectivityDestWorkSessionOpener(material.Url, material.Bootstrap));
}
