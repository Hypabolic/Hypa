namespace Hypa.AgentIntelligence.Detection;

/// <summary>
/// Screen plus OSC evidence for one detection pass.
/// OSC regions source from these fields, not the screen.
/// </summary>
public readonly record struct ManifestDetectionInput(
    string Screen,
    string OscTitle,
    string OscProgress)
{
    public static ManifestDetectionInput FromScreen(string screen) =>
        new(screen ?? "", "", "");
}
