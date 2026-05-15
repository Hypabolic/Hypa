namespace Hypa.Runtime.Domain.Hooks;

public sealed record UninstallReport(string HarnessKey, IReadOnlyList<UninstallEntry> Entries);

public sealed record UninstallEntry(string Description, UninstallStatus Status, string? Detail = null);

public enum UninstallStatus { Removed, NotPresent, Skipped, Error }
