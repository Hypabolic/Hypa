namespace Hypa.ControlPlane;

/// <summary>
/// Why one attach socket closed. Stall keeps the mux. Detach is user leave.
/// </summary>
public enum AttachClientLoss
{
    Stall = 0,
    Detach = 1,
    Disconnect = 2,
}
