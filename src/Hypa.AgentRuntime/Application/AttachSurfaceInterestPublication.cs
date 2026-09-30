using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// In-memory attach surface-interest registry. Every <c>active: true</c> request
// / advances the projection floor.
/// <c>src/server/headless/surface_interest.rs:3-84</c>.
/// </summary>
public sealed class AttachSurfaceInterestPublication : IAttachSurfaceInterestPublication
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ClientState> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _clientGenerations = new(StringComparer.Ordinal);
    private readonly List<byte[]> _heldInputs = [];
    private ulong _nextLeaseSerial = 1;

    public AttachSurfaceInterestPublication(string? bootId = null, string? muxIdentity = null)
    {
        BootId = string.IsNullOrWhiteSpace(bootId) ? Guid.NewGuid().ToString("N") : bootId.Trim();
        MuxIdentity = string.IsNullOrWhiteSpace(muxIdentity) ? "mux_local" : muxIdentity.Trim();
    }

    public string BootId { get; }

    public string MuxIdentity { get; }

    public Result<AttachEndpointHelloApplyResult, AttachSurfaceInterestError> ApplyHello(
        AttachEndpointHelloApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Hello);
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
            return Result<AttachEndpointHelloApplyResult, AttachSurfaceInterestError>.Fail(
                AttachSurfaceInterestError.InvalidConnection);

        var hello = request.Hello;
        if (hello.EndpointGeneration != AttachEndpointProtocol.EndpointGeneration)
        {
            return Result<AttachEndpointHelloApplyResult, AttachSurfaceInterestError>.Fail(
                new AttachSurfaceInterestError(
                    AttachEndpointErrorCodes.Incompatible,
                    "endpoint generation is incompatible"));
        }

        if (!SupportsRequiredCodecs(hello))
        {
            return Result<AttachEndpointHelloApplyResult, AttachSurfaceInterestError>.Fail(
                new AttachSurfaceInterestError(
                    AttachEndpointErrorCodes.Incompatible,
                    "required codecs are missing"));
        }

        if (!SupportsRequiredCapabilities(hello))
        {
            return Result<AttachEndpointHelloApplyResult, AttachSurfaceInterestError>.Fail(
                new AttachSurfaceInterestError(
                    AttachEndpointErrorCodes.Incompatible,
                    "required capabilities are missing"));
        }

        lock (_gate)
        {
            var generation = StampConnectionGenerationUnlocked(hello.ClientId, request.ConnectionId);
            _clients[request.ConnectionId] = new ClientState
            {
                ClientId = hello.ClientId.Trim(),
                EndpointId = request.EndpointId,
                ConnectionGeneration = generation,
                Geometry = hello.Geometry,
                SurfaceActive = false,
                ProjectionRevision = 0,
                GeometryRevision = hello.Geometry.GeometryRevision,
                LeaseId = null,
                CachedSnapshotRevision = null,
                HeldInputCount = 0,
            };

            var welcome = CompatibleWelcome(generation);
            return Result<AttachEndpointHelloApplyResult, AttachSurfaceInterestError>.Ok(
                new AttachEndpointHelloApplyResult
                {
                    Welcome = welcome,
                    ConnectionGeneration = generation,
                });
        }
    }

    public Result<AttachSurfaceInterestApplyResult, AttachSurfaceInterestError> ApplySurfaceInterest(
        AttachSurfaceInterestApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
            return Result<AttachSurfaceInterestApplyResult, AttachSurfaceInterestError>.Fail(
                AttachSurfaceInterestError.InvalidConnection);

        lock (_gate)
        {
            if (!_clients.TryGetValue(request.ConnectionId, out var client))
                return Result<AttachSurfaceInterestApplyResult, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.InvalidConnection);

            if (!string.Equals(client.ClientId, request.ClientId, StringComparison.Ordinal))
                return Result<AttachSurfaceInterestApplyResult, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.ClientMismatch);

            if (request.GeometryRevision < client.GeometryRevision)
                return Result<AttachSurfaceInterestApplyResult, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleGeometry);

            var changed = client.SurfaceActive != request.Active;
            if (request.Active)
            {
                client.ProjectionRevision = checked(client.ProjectionRevision + 1);
                client.CachedSnapshotRevision = null;
                client.LeaseId = AllocateLeaseIdUnlocked();
            }

            if (!changed && !request.Active)
            {
                return Result<AttachSurfaceInterestApplyResult, AttachSurfaceInterestError>.Ok(
                    new AttachSurfaceInterestApplyResult
                    {
                        Changed = false,
                        ProjectionRevision = client.ProjectionRevision,
                        GeometryRevision = client.GeometryRevision,
                        LeaseId = client.LeaseId ?? string.Empty,
                        BootId = BootId,
                    });
            }

            client.SurfaceActive = request.Active;
            client.GeometryRevision = Math.Max(client.GeometryRevision, request.GeometryRevision);
            if (request.Geometry is not null)
                client.Geometry = request.Geometry;

            if (!request.Active && changed)
            {
                client.HeldInputCount = 0;
                _heldInputs.Clear();
                client.LeaseId = null;
            }

            return Result<AttachSurfaceInterestApplyResult, AttachSurfaceInterestError>.Ok(
                new AttachSurfaceInterestApplyResult
                {
                    Changed = changed || request.Active,
                    ProjectionRevision = client.ProjectionRevision,
                    GeometryRevision = client.GeometryRevision,
                    LeaseId = client.LeaseId ?? string.Empty,
                    BootId = BootId,
                });
        }
    }

    public Result<(ulong ProjectionRevision, ulong GeometryRevision), AttachSurfaceInterestError> ApplyResize(
        string connectionId,
        string clientId,
        AttachGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                AttachSurfaceInterestError.InvalidConnection);

        lock (_gate)
        {
            if (!_clients.TryGetValue(connectionId, out var client))
                return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.InvalidConnection);

            if (!string.Equals(client.ClientId, clientId, StringComparison.Ordinal))
                return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.ClientMismatch);

            if (geometry.GeometryRevision < client.GeometryRevision)
                return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleGeometry);

            client.Geometry = geometry;
            client.GeometryRevision = geometry.GeometryRevision;
            return Result<(ulong, ulong), AttachSurfaceInterestError>.Ok(
                (client.ProjectionRevision, client.GeometryRevision));
        }
    }

    public Result<(ulong ProjectionRevision, ulong GeometryRevision), AttachSurfaceInterestError> ApplyFocus(
        string connectionId,
        string clientId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                AttachSurfaceInterestError.InvalidConnection);

        lock (_gate)
        {
            if (!_clients.TryGetValue(connectionId, out var client))
                return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.InvalidConnection);

            if (!string.Equals(client.ClientId, clientId, StringComparison.Ordinal))
                return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.ClientMismatch);

            return Result<(ulong, ulong), AttachSurfaceInterestError>.Ok(
                (client.ProjectionRevision, client.GeometryRevision));
        }
    }

    public Result<(ulong ProjectionRevision, ulong GeometryRevision), AttachSurfaceInterestError> ConsumeProjectionFloor(
        string connectionId,
        string clientId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                AttachSurfaceInterestError.InvalidConnection);

        lock (_gate)
        {
            if (!_clients.TryGetValue(connectionId, out var client))
                return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.InvalidConnection);

            if (!string.Equals(client.ClientId, clientId, StringComparison.Ordinal))
                return Result<(ulong, ulong), AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.ClientMismatch);

            client.ProjectionRevision = checked(client.ProjectionRevision + 1);
            client.CachedSnapshotRevision = null;
            return Result<(ulong, ulong), AttachSurfaceInterestError>.Ok(
                (client.ProjectionRevision, client.GeometryRevision));
        }
    }

    public Result<bool, AttachSurfaceInterestError> AdmitSurface(
        AttachActivationAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
            return Result<bool, AttachSurfaceInterestError>.Fail(
                AttachSurfaceInterestError.InvalidConnection);

        lock (_gate)
        {
            if (!_clients.TryGetValue(request.ConnectionId, out var client))
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.SurfaceInactive);

            if (!client.SurfaceActive)
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.SurfaceInactive);

            if (!string.Equals(request.BootId, BootId, StringComparison.Ordinal))
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);

            if (client.LeaseId is null
                || !string.Equals(client.LeaseId, request.LeaseId, StringComparison.Ordinal))
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);

            if (request.ProjectionRevision < client.ProjectionRevision)
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);

            if (request.SurfaceRevision < client.ProjectionRevision)
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);

            if (request.GeometryRevision < client.GeometryRevision)
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);

            if (request.SnapshotRevision is { } snapshot
                && snapshot != request.ProjectionRevision)
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);

            client.CachedSnapshotRevision = request.ProjectionRevision;
            return Result<bool, AttachSurfaceInterestError>.Ok(true);
        }
    }

    public Result<bool, AttachSurfaceInterestError> AdmitPaneMutation(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<bool, AttachSurfaceInterestError>.Fail(
                AttachSurfaceInterestError.SurfaceInactive);

        lock (_gate)
        {
            if (!_clients.TryGetValue(connectionId, out var client) || !client.SurfaceActive)
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.SurfaceInactive);

            if (!_clientGenerations.TryGetValue(client.ClientId, out var current)
                || current != client.ConnectionGeneration)
            {
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);
            }

            return Result<bool, AttachSurfaceInterestError>.Ok(true);
        }
    }

    public AttachSurfaceInterestSnapshot? GetSnapshot(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return null;

        lock (_gate)
        {
            if (!_clients.TryGetValue(connectionId, out var client))
                return null;

            return new AttachSurfaceInterestSnapshot
            {
                ConnectionId = connectionId,
                ClientId = client.ClientId,
                ConnectionGeneration = client.ConnectionGeneration,
                BootId = BootId,
                SurfaceActive = client.SurfaceActive,
                ProjectionRevision = client.ProjectionRevision,
                GeometryRevision = client.GeometryRevision,
                Columns = client.Geometry.Columns,
                Rows = client.Geometry.Rows,
                LeaseId = client.LeaseId,
                CachedSnapshotRevision = client.CachedSnapshotRevision,
            };
        }
    }

    public IReadOnlyList<string> ListConnectionIds()
    {
        lock (_gate)
        {
            var ids = _clients.Keys.ToArray();
            Array.Sort(ids, StringComparer.Ordinal);
            return ids;
        }
    }

    public void Remove(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        lock (_gate)
        {
            if (_clients.Remove(connectionId, out var client) && client.SurfaceActive)
            {
                _heldInputs.Clear();
            }
        }
    }

    public bool IsSurfaceActive(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return false;

        lock (_gate)
            return _clients.TryGetValue(connectionId, out var client) && client.SurfaceActive;
    }

    public ulong SessionProjectionRevision()
    {
        lock (_gate)
        {
            ulong max = 0;
            foreach (var client in _clients.Values)
            {
                if (client.SurfaceActive)
                    max = Math.Max(max, client.ProjectionRevision);
            }

            return max;
        }
    }

    public Result<bool, AttachSurfaceInterestError> ValidateIngress(
        string connectionId,
        string clientId,
        ulong connectionGeneration)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<bool, AttachSurfaceInterestError>.Fail(
                AttachSurfaceInterestError.InvalidConnection);

        lock (_gate)
        {
            if (!_clients.TryGetValue(connectionId, out var client))
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.InvalidConnection);

            if (!string.Equals(client.ClientId, clientId, StringComparison.Ordinal))
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.ClientMismatch);

            if (client.ConnectionGeneration != connectionGeneration)
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);

            if (!_clientGenerations.TryGetValue(clientId, out var current)
                || current != connectionGeneration)
            {
                return Result<bool, AttachSurfaceInterestError>.Fail(
                    AttachSurfaceInterestError.StaleSurface);
            }

            return Result<bool, AttachSurfaceInterestError>.Ok(true);
        }
    }

    private AttachEndpointWelcome CompatibleWelcome(ulong connectionGeneration) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            BootId = BootId,
            MuxIdentity = MuxIdentity,
            SnapshotCodec = AttachEndpointProtocol.SnapshotCodec,
            SurfaceCodec = AttachEndpointProtocol.SurfaceCodec,
            InputCodec = AttachEndpointProtocol.InputCodec,
            Methods = AttachEndpointProtocol.Methods.ToArray(),
            Capabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
            ConnectionGeneration = connectionGeneration,
        };

    private static bool SupportsRequiredCodecs(AttachEndpointHello hello) =>
        hello.SnapshotCodecs.Contains(AttachEndpointProtocol.SnapshotCodec, StringComparer.Ordinal)
        && hello.SurfaceCodecs.Contains(AttachEndpointProtocol.SurfaceCodec, StringComparer.Ordinal)
        && hello.InputCodecs.Contains(AttachEndpointProtocol.InputCodec, StringComparer.Ordinal);

    private static bool SupportsRequiredCapabilities(AttachEndpointHello hello)
    {
        foreach (var required in hello.RequiredCapabilities)
        {
            if (!AttachEndpointProtocol.RequiredCapabilities.Contains(required, StringComparer.Ordinal))
                return false;
        }

        return hello.RequiredCapabilities.Length > 0;
    }

    private ulong StampConnectionGenerationUnlocked(string clientId, string connectionId)
    {
        if (_clientGenerations.TryGetValue(clientId, out var prior))
        {
            var next = checked(prior + 1);
            _clientGenerations[clientId] = next;
            return next;
        }

        _clientGenerations[clientId] = 1;
        return 1;
    }

    private string AllocateLeaseIdUnlocked() =>
        $"lease_ae_{Interlocked.Increment(ref _nextLeaseSerial)}";

    private sealed class ClientState
    {
        public required string ClientId { get; set; }
        public required string EndpointId { get; set; }
        public required ulong ConnectionGeneration { get; set; }
        public required AttachGeometry Geometry { get; set; }
        public required bool SurfaceActive { get; set; }
        public required ulong ProjectionRevision { get; set; }
        public required ulong GeometryRevision { get; set; }
        public string? LeaseId { get; set; }
        public ulong? CachedSnapshotRevision { get; set; }
        public int HeldInputCount { get; set; }
    }
}
