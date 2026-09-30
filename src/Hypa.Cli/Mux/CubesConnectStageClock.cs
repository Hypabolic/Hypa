using System.Diagnostics;
using Hypa.AgentRuntime.Application;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Cli.Mux;

/// <summary>
/// Stopwatch for one cubes connect. The same instance travels on the
/// connect request and the endpoint activation. It is not process-static.
/// </summary>
internal sealed class CubesConnectStageClock
{
    private readonly object _gate = new();
    private readonly Stopwatch _watch;
    private readonly IProcessLogSink _sink;
    private readonly string? _sessionId;
    private readonly string? _attachClientId;
    private readonly HashSet<string> _stamped = new(StringComparer.Ordinal);
    private long _previousMs;
    private string? _transport;
    private bool _dialSucceeded;
    private PlacementRecord? _placement;
    private PlacementDirectoryChangeStamp _stamp;
    private bool _hasStamp;
    private IPlacementDirectory? _directory;
    private IPlacementDirectoryChangeStamp? _probe;

    public CubesConnectStageClock(
        Stopwatch watch,
        IProcessLogSink sink,
        string endpointId,
        string? sessionId,
        string? attachClientId)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        _watch = watch;
        _sink = sink;
        EndpointId = endpointId;
        _sessionId = sessionId;
        _attachClientId = attachClientId;
    }

    public string EndpointId { get; }

    public bool HasStamped(string stage)
    {
        lock (_gate)
            return _stamped.Contains(stage);
    }

    public bool DialSucceeded
    {
        get
        {
            lock (_gate)
                return _dialSucceeded;
        }
    }

    public void NoteTransport(string transport)
    {
        if (string.IsNullOrWhiteSpace(transport))
            return;
        lock (_gate)
            _transport = transport;
    }

    public void NoteDialSucceeded()
    {
        lock (_gate)
            _dialSucceeded = true;
    }

    public void RememberPlacement(
        PlacementRecord placement,
        PlacementDirectoryChangeStamp? stamp,
        IPlacementDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(directory);
        lock (_gate)
        {
            _placement = placement;
            _hasStamp = stamp is not null;
            if (stamp is { } captured)
                _stamp = captured;
            _directory = directory;
            _probe = directory as IPlacementDirectoryChangeStamp;
        }
    }

    public bool TryRememberedPlacement(string placementId, out PlacementRecord? placement)
    {
        lock (_gate)
        {
            if (_placement is not null
                && string.Equals(_placement.Id.Value, placementId, StringComparison.Ordinal))
            {
                placement = _placement;
                return true;
            }
        }

        placement = null;
        return false;
    }

    public bool TryRememberedDirectory(
        string placementId,
        out IPlacementDirectory? directory,
        out PlacementRecord? placement)
    {
        lock (_gate)
        {
            if (_directory is not null
                && _placement is not null
                && string.Equals(_placement.Id.Value, placementId, StringComparison.Ordinal))
            {
                directory = _directory;
                placement = _placement;
                return true;
            }
        }

        directory = null;
        placement = null;
        return false;
    }

    /// <summary>
    /// True when one stat of the directory file matches the stamp taken
    /// before the connect read. A changed or missing stamp is false.
    /// </summary>
    public bool DirectoryUnchanged(string placementId)
    {
        IPlacementDirectoryChangeStamp? probe;
        PlacementDirectoryChangeStamp stamp;
        lock (_gate)
        {
            if (!_hasStamp
                || _probe is null
                || _placement is null
                || !string.Equals(_placement.Id.Value, placementId, StringComparison.Ordinal))
            {
                return false;
            }

            probe = _probe;
            stamp = _stamp;
        }

        try
        {
            return probe.ReadChangeStamp() == stamp;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Stamp(string stage)
    {
        long elapsedMs;
        long stageMs;
        string? transport;
        lock (_gate)
        {
            if (!_stamped.Add(stage))
                return;
            elapsedMs = _watch.ElapsedMilliseconds;
            stageMs = elapsedMs - _previousMs;
            if (stageMs < 0)
                stageMs = 0;
            _previousMs = elapsedMs;
            transport = _transport;
        }

        AttachProcessLog.CubesStage(
            _sink,
            stage,
            elapsedMs,
            stageMs,
            EndpointId,
            transport,
            _sessionId,
            _attachClientId);
    }
}

internal static class CubesConnectStages
{
    public const string Resolve = "resolve";
    public const string Material = "material";
    public const string Dial = "dial";
    public const string Join = "join";
    public const string Hello = "hello";
    public const string Snapshot = "snapshot";
    public const string Subscribe = "subscribe";
    public const string Observe = "observe";
    public const string Queued = "queued";
    public const string Begin = "begin";
    public const string SourceReleased = "source_released";
    public const string TargetStarted = "target_started";
    public const string Ready = "ready";
}
