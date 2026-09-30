using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Settings;

public sealed record SettingsTabHit(string Id, CellRect Rect);

public sealed record SettingsItemHit(int Index, CellRect Rect, CellRect? Box = null);

public sealed record SettingsLayout(
    CellRect Panel,
    IReadOnlyList<SettingsTabHit> Tabs,
    IReadOnlyList<SettingsItemHit> Items,
    CellRect Close,
    CellRect Apply,
    int ListOffset = 0);
