using Hypa.AgentRuntime.Application.Sidebar;

namespace Hypa.Cli.Attach;

/// <summary>
/// Move Work confirm copy. Close-pane confirm stays
/// <c>CONFIRM close? [y/n]</c>.
/// </summary>
public static class MoveWorkConfirmPrompt
{
    public const string ClosePaneText = "CONFIRM close? [y/n]";

    public static string Format(string workTitle, string sourceName, string destName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destName);
        return $"CONFIRM Move Work {workTitle} from {sourceName} to {destName}? [y/n]";
    }

    public static string PlacementLabel(string displayName, SidebarCubeKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return kind is SidebarCubeKind.Cube
            ? "Cube " + displayName
            : displayName;
    }
}
