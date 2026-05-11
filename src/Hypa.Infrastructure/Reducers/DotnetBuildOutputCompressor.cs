using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Reducers;

public sealed partial class DotnetBuildOutputCompressor(ITokenCounter tokenCounter) : IOutputCompressor
{
    public string Id => "dotnet-build";

    public bool CanHandle(CommandInvocation invocation) =>
        invocation.Executable == "dotnet" &&
        invocation.Arguments.Count > 0 &&
        invocation.Arguments[0] == "build";

    public CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        var lines = combined.Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (IsDiagnosticLine().IsMatch(line) ||
                IsBuildResultLine().IsMatch(line) ||
                IsSummaryCountLine().IsMatch(line) ||
                IsTimeElapsedLine().IsMatch(line) ||
                IsProjectTargetLine().IsMatch(line))
            {
                kept.Add(line);
            }
        }

        var text = string.Join('\n', kept).TrimEnd();
        if (text.Length == 0)
            text = combined.TrimEnd();

        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, Id, ["parse-diagnostics"], false);
    }

    [GeneratedRegex(@"^\s*.+\(\d+,\d+\)\s*:\s*(error|warning)\s+[A-Z]{1,4}\d{3,5}\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex IsDiagnosticLine();

    [GeneratedRegex(@"^Build (succeeded|FAILED)\.", RegexOptions.IgnoreCase)]
    private static partial Regex IsBuildResultLine();

    [GeneratedRegex(@"^\s*\d+\s+(Error|Warning)", RegexOptions.IgnoreCase)]
    private static partial Regex IsSummaryCountLine();

    [GeneratedRegex(@"^Time Elapsed", RegexOptions.IgnoreCase)]
    private static partial Regex IsTimeElapsedLine();

    [GeneratedRegex(@"^\s+\S+\s+->")]
    private static partial Regex IsProjectTargetLine();
}
