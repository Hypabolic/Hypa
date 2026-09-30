namespace Hypa.Continuity.Domain;

/// <summary>
/// Resume proof class. File bytes that Continuity wrote are not harness-native proof.
/// </summary>
public enum ResumeEvidence
{
    None = 0,
    StorePresence = 1,
    IndexListing = 2,
    HarnessReported = 3,
}
