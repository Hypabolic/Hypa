namespace Hypa.AgentRuntime.Domain;

/// <summary>Lease scope tokens (design §4.5 / Appendix B).</summary>
public static class LeaseScopes
{
    public const string Observe = "observe";
    public const string Input = "input";
    public const string Resize = "resize";
    public const string Admin = "admin";

    public static bool IsKnown(string? scope) =>
        scope is Observe or Input or Resize or Admin;
}

/// <summary>Claim outcome tokens for <c>runtime.lease.claim</c>.</summary>
public static class LeaseOutcomes
{
    public const string Granted = "granted";
    public const string PendingApproval = "pending_approval";
    public const string Denied = "denied";
    public const string AlreadyHeld = "already_held";
    public const string Invalid = "invalid";
}

/// <summary>Lease lifecycle state tokens (fixture payload <c>state</c>).</summary>
public static class LeaseStates
{
    public const string Granted = "granted";
    public const string Released = "released";
    public const string Expired = "expired";
}

/// <summary>Attachment mode tokens for <c>terminal.observe</c> / <c>terminal.control</c>.</summary>
public static class AttachmentModes
{
    public const string Observe = "observe";
    public const string Control = "control";
}
