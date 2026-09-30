using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// surface-interest lease ownership per attach connection.
/// </summary>
public interface IAttachSurfaceInterestPublication
{
    string BootId { get; }

    Result<AttachEndpointHelloApplyResult, AttachSurfaceInterestError> ApplyHello(
        AttachEndpointHelloApplyRequest request);

    Result<AttachSurfaceInterestApplyResult, AttachSurfaceInterestError> ApplySurfaceInterest(
        AttachSurfaceInterestApplyRequest request);

    Result<(ulong ProjectionRevision, ulong GeometryRevision), AttachSurfaceInterestError> ApplyResize(
        string connectionId,
        string clientId,
        AttachGeometry geometry);

    Result<(ulong ProjectionRevision, ulong GeometryRevision), AttachSurfaceInterestError> ApplyFocus(
        string connectionId,
        string clientId);

    Result<(ulong ProjectionRevision, ulong GeometryRevision), AttachSurfaceInterestError> ConsumeProjectionFloor(
        string connectionId,
        string clientId);

    Result<bool, AttachSurfaceInterestError> AdmitSurface(
        AttachActivationAdmissionRequest request);

    Result<bool, AttachSurfaceInterestError> AdmitPaneMutation(string connectionId);

    AttachSurfaceInterestSnapshot? GetSnapshot(string connectionId);

    IReadOnlyList<string> ListConnectionIds();

    void Remove(string connectionId);

    bool IsSurfaceActive(string connectionId);

    Result<bool, AttachSurfaceInterestError> ValidateIngress(
        string connectionId,
        string clientId,
        ulong connectionGeneration);

    /// <summary>Max projection revision across active attach surfaces; 0 when none.</summary>
    ulong SessionProjectionRevision();
}
