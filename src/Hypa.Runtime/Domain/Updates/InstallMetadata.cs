namespace Hypa.Runtime.Domain.Updates;

public sealed record InstallMetadata(
    string Source,
    string RuntimeIdentifier,
    string? InstallDirectory,
    string? BinLinkPath,
    string? ExecutablePath,
    string? InstalledVersion,
    DateTimeOffset? InstalledAt
);
