namespace Hypa.Runtime.Domain.Hooks;

public sealed record UninstallPlan(IReadOnlyList<UninstallOperation> Operations);
