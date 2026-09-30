namespace Hypa.Continuity.Domain;

/// <summary>
/// Continuity-certified harness ids. Pi is the product harness. Fake is the CI harness.
/// Claude Code and Codex are not certified.
/// </summary>
public static class CertifiedHarnessIds
{
    public const string Pi = "pi";
    public const string Fake = "fake";

    public static bool Contains(string? harnessId) =>
        string.Equals(harnessId, Pi, StringComparison.Ordinal)
        || string.Equals(harnessId, Fake, StringComparison.Ordinal);
}
