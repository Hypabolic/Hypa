namespace Hypa.Runtime.Domain.Hooks;

public sealed record InstallReport(string HarnessKey, IReadOnlyList<InstallEntry> Entries);

public sealed record InstallEntry(string Description, InstallStatus Status, string? Detail = null);

public enum InstallStatus { Installed, AlreadyPresent, Skipped, Warning, Error }
