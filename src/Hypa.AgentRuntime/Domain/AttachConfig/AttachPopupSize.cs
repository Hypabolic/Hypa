namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>
/// Cells or a 1–100 percent of the attach content area.
/// Domain VO. Cli maps this to protocol <c>PopupSize</c> at <c>popup.open</c>.
/// </summary>
public sealed record AttachPopupSize
{
    public required bool IsPercent { get; init; }

    public required int Value { get; init; }

    public static AttachPopupSize Cells(int value) => new() { IsPercent = false, Value = value };

    public static AttachPopupSize Percent(int value) => new() { IsPercent = true, Value = value };
}
