namespace Hypa.AgentRuntime.Domain;

public enum OfficialIntegrationStatusKind
{
    NotInstalled,
    Current,
    Outdated,
}

public enum OfficialIntegrationSkillState
{
    Installed,
    Outdated,
    Missing,
    NotApplicable,
}

/// <summary>
// / Installed official-integration record.
/// <c>src/integration/types.rs</c> <c>IntegrationStatus</c>.
/// </summary>
public sealed record OfficialIntegrationStatus
{
    public required OfficialIntegrationTarget Target { get; init; }
    public required string Path { get; init; }
    public required OfficialIntegrationStatusKind State { get; init; }
    public int? InstalledVersion { get; init; }
    public required int ExpectedVersion { get; init; }
    public bool Available { get; init; }
    public OfficialIntegrationSkillState SkillState { get; init; } = OfficialIntegrationSkillState.NotApplicable;

    /// <summary>Consent lines from the install layout. Empty until a list fills them.</summary>
    public IReadOnlyList<string> Consent { get; init; } = [];
}

/// <summary>Install or uninstall messages. Paths are Hypa-owned files only.</summary>
public sealed record OfficialIntegrationActionResult
{
    public required OfficialIntegrationTarget Target { get; init; }
    public required IReadOnlyList<string> Messages { get; init; }

    /// <summary>False when this target's install or uninstall failed.</summary>
    public bool Succeeded { get; init; } = true;
}

public static class OfficialIntegrationStatusWire
{
    public static string WireName(this OfficialIntegrationStatusKind kind) =>
        kind switch
        {
            OfficialIntegrationStatusKind.NotInstalled => "not_installed",
            OfficialIntegrationStatusKind.Current => "current",
            OfficialIntegrationStatusKind.Outdated => "outdated",
            _ => "not_installed",
        };

    public static string WireName(this OfficialIntegrationSkillState state) =>
        state switch
        {
            OfficialIntegrationSkillState.Installed => "installed",
            OfficialIntegrationSkillState.Outdated => "outdated",
            OfficialIntegrationSkillState.Missing => "missing",
            OfficialIntegrationSkillState.NotApplicable => "not_applicable",
            _ => "not_applicable",
        };

    public static OfficialIntegrationSkillState ParseSkillState(string? wire) =>
        wire switch
        {
            "installed" => OfficialIntegrationSkillState.Installed,
            "outdated" => OfficialIntegrationSkillState.Outdated,
            "missing" => OfficialIntegrationSkillState.Missing,
            "not_applicable" => OfficialIntegrationSkillState.NotApplicable,
            _ => OfficialIntegrationSkillState.NotApplicable,
        };

    public static bool NeedsInstall(this OfficialIntegrationStatus status) =>
        status.State is OfficialIntegrationStatusKind.Outdated
        || (status.Available && status.State is OfficialIntegrationStatusKind.NotInstalled)
        || (status.SkillState is OfficialIntegrationSkillState.Outdated
            && (status.Available || status.State is not OfficialIntegrationStatusKind.NotInstalled));
}
