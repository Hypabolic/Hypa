namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Wire error codes from design Appendix C.
/// Numbers match live UnixSocketServer / ControlPlaneException usage.
/// </summary>
public static class ProtocolErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerShuttingDown = -32000;
    public const int NotFound = -32004;
    public const int InvalidState = -32005;
    public const int LeaseRequired = -32006;
    public const int LeaseExpired = -32007;
    public const int OccupantReplaced = -32008;
    public const int CapabilityInvalid = -32009;
    public const int PaneStartFailed = -32010;
    public const int PersistenceUnavailable = -32011;
    public const int ReplayIncomplete = -32012;
    public const int CheckpointConflict = -32013;
    public const int TransferIncomplete = -32014;
    public const int RemoteUnavailable = -32015;
    public const int PolicyUnavailable = -32016;
    public const int Fenced = -32017;
}
