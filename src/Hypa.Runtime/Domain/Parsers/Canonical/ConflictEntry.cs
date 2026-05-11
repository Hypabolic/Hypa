namespace Hypa.Runtime.Domain.Parsers.Canonical;

public sealed record ConflictEntry(string Package, string Required, string Installed, string Message);
