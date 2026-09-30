using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.ControlPlane;

namespace Hypa.Cli.Mux;

/// <summary>Connect-time occupant command snapshot. Connect must not add attach.</summary>
public sealed class PaneProcessList
{
    private readonly List<string> _commands;

    public PaneProcessList(IEnumerable<string>? commands = null)
    {
        _commands = commands is null ? [] : [.. commands];
    }

    public IReadOnlyList<string> Commands => _commands;

    public int SpawnCount { get; private set; }

    public void Spawn(string command)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        SpawnCount++;
        _commands.Add(command);
    }
}

public sealed record AttachConnectOutcome
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public required string TransportKind { get; init; }
    public required bool SpawnsHypaAttach { get; init; }
    public required bool NestedAttachBlocked { get; init; }
    public required int ProcessStartCount { get; init; }
    public required IReadOnlyList<string> ProcessStartCommands { get; init; }
    public required IReadOnlyList<string> PaneProcessCommands { get; init; }
    public JsonElement? Ping { get; init; }

    public static AttachConnectOutcome NestedBlocked(
        string transportKind,
        IReadOnlyList<string> paneProcessCommands,
        int processStartCount,
        IReadOnlyList<string> processStartCommands,
        bool spawnsHypaAttach) =>
        new()
        {
            Ok = false,
            Reason = "nested_attach_blocked",
            Detail = NestedAttachGuard.Message,
            TransportKind = transportKind,
            SpawnsHypaAttach = spawnsHypaAttach,
            NestedAttachBlocked = true,
            ProcessStartCount = processStartCount,
            ProcessStartCommands = processStartCommands,
            PaneProcessCommands = paneProcessCommands,
        };

    public static AttachConnectOutcome Connected(
        string transportKind,
        IReadOnlyList<string> paneProcessCommands,
        int processStartCount,
        IReadOnlyList<string> processStartCommands,
        bool spawnsHypaAttach,
        JsonElement ping) =>
        new()
        {
            Ok = true,
            TransportKind = transportKind,
            SpawnsHypaAttach = spawnsHypaAttach,
            NestedAttachBlocked = false,
            ProcessStartCount = processStartCount,
            ProcessStartCommands = processStartCommands,
            PaneProcessCommands = paneProcessCommands,
            Ping = ping,
        };
}

/// <summary>
/// Connect uses an attach endpoint. It does not spawn <c>hypa attach</c>.
/// Nested attach stays blocked by default. This is not reconnect or retarget.
/// </summary>
public sealed class AttachConnectService
{
    private readonly IAttachProcessStarter _processStarter;
    private readonly IPaneRuntimeFactory? _paneFactory;

    public AttachConnectService(
        IAttachProcessStarter? processStarter = null,
        IPaneRuntimeFactory? paneFactory = null)
    {
        _processStarter = processStarter ?? new ObservingProcessStarter();
        _paneFactory = paneFactory;
    }

    public IAttachProcessStarter ProcessStarter => _processStarter;

    public IPaneRuntimeFactory? PaneFactory => _paneFactory;

    public async Task<AttachConnectOutcome> ConnectAsync(
        IAttachEndpoint endpoint,
        PaneProcessList paneProcesses,
        AttachClientConfig? config = null,
        string? hypaEnv = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(paneProcesses);
        config ??= AttachClientConfig.Default;

        var startsBefore = _processStarter.StartCount;
        var spawnBefore = paneProcesses.SpawnCount;
        var commandCountBefore = paneProcesses.Commands.Count;

        if (NestedAttachGuard.IsBlocked(config, hypaEnv))
        {
            return AttachConnectOutcome.NestedBlocked(
                endpoint.Kind,
                Snapshot(paneProcesses),
                _processStarter.StartCount,
                SnapshotStarts(),
                ObservedHypaAttachSpawn(startsBefore, spawnBefore, commandCountBefore, paneProcesses));
        }

        await using var client = endpoint.CreateClient();
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var ping = await client.CallAsync("ping", ct: cancellationToken).ConfigureAwait(false);
        return AttachConnectOutcome.Connected(
            endpoint.Kind,
            Snapshot(paneProcesses),
            _processStarter.StartCount,
            SnapshotStarts(),
            ObservedHypaAttachSpawn(startsBefore, spawnBefore, commandCountBefore, paneProcesses),
            ping);
    }

    private bool ObservedHypaAttachSpawn(
        int startsBefore,
        int spawnBefore,
        int commandCountBefore,
        PaneProcessList panes)
    {
        if (_processStarter.StartCount > startsBefore)
            return true;
        if (panes.SpawnCount > spawnBefore)
            return true;
        for (var i = commandCountBefore; i < panes.Commands.Count; i++)
        {
            if (LooksLikeHypaAttach(panes.Commands[i]))
                return true;
        }

        foreach (var command in _processStarter.StartedCommands)
        {
            if (LooksLikeHypaAttach(command))
                return true;
        }

        return false;
    }

    private static bool LooksLikeHypaAttach(string command) =>
        command.Contains("hypa attach", StringComparison.Ordinal)
        || (command.Contains("hypa", StringComparison.Ordinal)
            && command.Contains("attach", StringComparison.Ordinal));

    private static IReadOnlyList<string> Snapshot(PaneProcessList panes) =>
        [.. panes.Commands];

    private IReadOnlyList<string> SnapshotStarts() =>
        [.. _processStarter.StartedCommands];
}
