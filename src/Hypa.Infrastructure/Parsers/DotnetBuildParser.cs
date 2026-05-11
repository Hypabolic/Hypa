using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Parsers.Canonical;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Parsers;

public sealed partial class DotnetBuildParser : IOutputParser<BuildResult>
{
    public string Id => "dotnet-build";

    public bool CanParse(CommandInvocation invocation) =>
        invocation.Executable == "dotnet" &&
        invocation.Arguments.Count > 0 &&
        invocation.Arguments[0] == "build";

    public ParseResult<BuildResult> TryParse(CommandOutput output)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var lines = combined.Split('\n');

        var errors = new List<BuildDiagnostic>();
        var warnings = new List<BuildDiagnostic>();
        bool? succeeded = null;
        string elapsed = string.Empty;

        foreach (var line in lines)
        {
            var diagMatch = IsDiagnosticLine().Match(line);
            if (diagMatch.Success)
            {
                var diag = new BuildDiagnostic(
                    File: diagMatch.Groups[1].Value,
                    Line: int.TryParse(diagMatch.Groups[2].Value, out var l) ? l : 0,
                    Column: int.TryParse(diagMatch.Groups[3].Value, out var c) ? c : 0,
                    Severity: diagMatch.Groups[4].Value.ToLowerInvariant(),
                    Code: diagMatch.Groups[5].Value,
                    Message: diagMatch.Groups[6].Value.Trim());
                if (diag.Severity == "error") errors.Add(diag);
                else warnings.Add(diag);
                continue;
            }

            var buildResultMatch = IsBuildResultLine().Match(line);
            if (buildResultMatch.Success)
            {
                succeeded = buildResultMatch.Groups[1].Value.Equals("succeeded", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var elapsedMatch = IsTimeElapsedLine().Match(line);
            if (elapsedMatch.Success)
                elapsed = line.Trim();
        }

        if (succeeded is null)
            return new ParseResult<BuildResult>(null, ParseTier.Degraded, false);

        var result = new BuildResult
        {
            Succeeded = succeeded.Value,
            Errors = errors,
            Warnings = warnings,
            ElapsedTime = elapsed,
        };
        return new ParseResult<BuildResult>(result, ParseTier.Full, true);
    }

    [GeneratedRegex(@"^\s*(.+)\((\d+),(\d+)\)\s*:\s*(error|warning)\s+([A-Z]{1,4}\d{3,5})\s*:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex IsDiagnosticLine();

    [GeneratedRegex(@"^Build (succeeded|FAILED)\.", RegexOptions.IgnoreCase)]
    private static partial Regex IsBuildResultLine();

    [GeneratedRegex(@"^Time Elapsed", RegexOptions.IgnoreCase)]
    private static partial Regex IsTimeElapsedLine();
}
