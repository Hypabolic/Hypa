using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Integrations;

public interface IOfficialIntegrationService
{
    IReadOnlyList<OfficialIntegrationStatus> ListStatuses();

    /// <summary>
    /// Installs one target.
    /// <paramref name="planOnly"/> returns the consent text and writes nothing.
    /// </summary>
    IntegrationResult<OfficialIntegrationActionResult> Install(OfficialIntegrationTarget target, bool planOnly = false);

    IntegrationResult<OfficialIntegrationActionResult> Uninstall(OfficialIntegrationTarget target);

    /// <summary>
    /// Installs each target whose command is on <c>PATH</c>.
    /// Other targets are reported as not found.
    /// <paramref name="planOnly"/> returns consent text for each detected target and writes nothing.
    /// </summary>
    IReadOnlyList<OfficialIntegrationActionResult> InstallDetected(bool planOnly = false);

    /// <summary>
    /// Removes each target whose command is on <c>PATH</c>.
    /// Other targets are reported as not found.
    /// </summary>
    IReadOnlyList<OfficialIntegrationActionResult> UninstallDetected();

    /// <summary>Targets whose <c>CommandNames</c> resolve on <c>PATH</c>.</summary>
    IReadOnlyList<OfficialIntegrationTarget> DetectedTargets();

    /// <summary>Consent lines for one target. Paths come from <c>OfficialIntegrationLayout</c>.</summary>
    IReadOnlyList<string> ConsentLines(OfficialIntegrationTarget target);

    IntegrationResult<OfficialIntegrationActionResult> InstallRuntimeSkill(OfficialIntegrationTarget target);

    IntegrationResult<OfficialIntegrationActionResult> RemoveRuntimeSkill(OfficialIntegrationTarget target);
}
