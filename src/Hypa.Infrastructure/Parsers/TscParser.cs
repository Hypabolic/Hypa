using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Parsers.Canonical;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Parsers;

public sealed partial class TscParser : IOutputParser<LintResult>
{
    public string Id => "tsc";

    public bool CanParse(CommandInvocation invocation) =>
        invocation.Executable == "tsc" ||
        (invocation.Executable == "npx" &&
         invocation.Arguments.Count > 0 &&
         invocation.Arguments[0] == "tsc");

    public ParseResult<LintResult> TryParse(CommandOutput output)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var lines = combined.Split('\n');

        var diagnostics = new List<LintDiagnostic>();
        bool foundSummary = false;

        foreach (var line in lines)
        {
            var diagMatch = IsDiagnosticLine().Match(line);
            if (diagMatch.Success)
            {
                diagnostics.Add(new LintDiagnostic(
                    File: diagMatch.Groups[1].Value,
                    Line: int.TryParse(diagMatch.Groups[2].Value, out var l) ? l : 0,
                    Column: int.TryParse(diagMatch.Groups[3].Value, out var c) ? c : 0,
                    Severity: diagMatch.Groups[4].Value.ToLowerInvariant(),
                    Code: diagMatch.Groups[5].Value,
                    Message: diagMatch.Groups[6].Value.Trim()));
                continue;
            }

            if (IsSummaryLine().IsMatch(line))
                foundSummary = true;
        }

        if (!foundSummary && diagnostics.Count == 0)
            return new ParseResult<LintResult>(null, ParseTier.Degraded, false);

        var result = new LintResult
        {
            ErrorCount = diagnostics.Count(d => d.Severity == "error"),
            WarningCount = diagnostics.Count(d => d.Severity == "warning"),
            Diagnostics = diagnostics,
        };
        return new ParseResult<LintResult>(result, ParseTier.Full, true);
    }

    [GeneratedRegex(@"^(.+)\((\d+),(\d+)\):\s+(error|warning)\s+(TS\d+):\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex IsDiagnosticLine();

    [GeneratedRegex(@"^Found \d+ error", RegexOptions.IgnoreCase)]
    private static partial Regex IsSummaryLine();
}
