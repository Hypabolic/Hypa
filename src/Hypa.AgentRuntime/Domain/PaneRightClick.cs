namespace Hypa.AgentRuntime.Domain;

/// <summary>Pane right-click policy on the Hypa wire.</summary>
public static class PaneRightClick
{
    public const string Hypa = "hypa";
    public const string Pane = "pane";

    public static bool IsValid(string? value) =>
        value is Hypa or Pane;
}
