namespace Hypa.Runtime.Domain.Hooks;

public sealed record InstallPlan(IReadOnlyList<InstallOperation> Operations);
