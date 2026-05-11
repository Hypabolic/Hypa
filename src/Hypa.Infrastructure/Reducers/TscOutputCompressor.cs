using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Reducers;

public sealed partial class TscOutputCompressor(ITokenCounter tokenCounter) : IOutputCompressor
{
    public string Id => "tsc";

    public bool CanHandle(CommandInvocation invocation) =>
        invocation.Executable == "tsc" ||
        (invocation.Executable == "npx" &&
         invocation.Arguments.Count > 0 &&
         invocation.Arguments[0] == "tsc");

    public CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        var lines = combined.Split('\n');
        var kept = new List<string>(lines.Length);

        string? lastFile = null;
        bool prevWasDiagnostic = false;

        foreach (var line in lines)
        {
            var diagMatch = IsDiagnosticLine().Match(line);
            if (diagMatch.Success)
            {
                var file = diagMatch.Groups[1].Value;
                if (file != lastFile)
                {
                    kept.Add($"=== {file} ===");
                    lastFile = file;
                }
                kept.Add(line);
                prevWasDiagnostic = true;
                continue;
            }

            if (IsSummaryLine().IsMatch(line))
            {
                kept.Add(line);
                prevWasDiagnostic = false;
                continue;
            }

            // indented continuation line immediately following a diagnostic
            if (prevWasDiagnostic && IsContinuationLine().IsMatch(line))
            {
                kept.Add(line);
                continue;
            }

            prevWasDiagnostic = false;
        }

        var text = string.Join('\n', kept).TrimEnd();
        if (text.Length == 0)
            text = combined.TrimEnd();

        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, Id, ["parse-diagnostics"], false);
    }

    [GeneratedRegex(@"^(.+)\(\d+,\d+\):\s+(error|warning)\s+TS\d+:", RegexOptions.IgnoreCase)]
    private static partial Regex IsDiagnosticLine();

    [GeneratedRegex(@"^Found \d+ error", RegexOptions.IgnoreCase)]
    private static partial Regex IsSummaryLine();

    [GeneratedRegex(@"^\s{2,}")]
    private static partial Regex IsContinuationLine();
}
