using System.Text.RegularExpressions;

namespace Hypa.Infrastructure.Compression;

public sealed partial class ImportantLineClassifier
{
    [GeneratedRegex(@"\b(error|failed|exception|warning|fatal|panic)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorKeywords();

    [GeneratedRegex(@"\w+\.\w+:\d+:")]
    private static partial Regex FileDiagnostic();

    [GeneratedRegex(@"\b[45]\d{2}\b")]
    private static partial Regex HttpErrorCode();

    [GeneratedRegex(@"\bWarning\b")]
    private static partial Regex K8sWarning();

    [GeneratedRegex(@"\b[A-Z]{2,4}\d{3,5}\b")]
    private static partial Regex CompilerDiagnosticId();

    public bool IsImportant(string line) =>
        ErrorKeywords().IsMatch(line)
        || FileDiagnostic().IsMatch(line)
        || HttpErrorCode().IsMatch(line)
        || K8sWarning().IsMatch(line)
        || CompilerDiagnosticId().IsMatch(line);
}
