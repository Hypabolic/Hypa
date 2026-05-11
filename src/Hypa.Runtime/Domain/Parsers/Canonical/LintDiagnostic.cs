namespace Hypa.Runtime.Domain.Parsers.Canonical;

public sealed record LintDiagnostic(string File, int Line, int Column, string Severity, string Code, string Message);
