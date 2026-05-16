namespace Hypa.Runtime.Domain.Updates;

public sealed record UpdatePlan(
    string Strategy,
    bool CanAutoUpdate,
    string Summary,
    string? Command,
    string? Detail
);
