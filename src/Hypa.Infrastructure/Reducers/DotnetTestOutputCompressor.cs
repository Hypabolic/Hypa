using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Reducers;

public sealed partial class DotnetTestOutputCompressor(ITokenCounter tokenCounter) : IOutputCompressor
{
    public string Id => "dotnet-test";

    public bool CanHandle(CommandInvocation invocation) =>
        invocation.Executable == "dotnet" &&
        invocation.Arguments.Count > 0 &&
        invocation.Arguments[0] == "test";

    public CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        var lines = combined.Split('\n');
        var kept = new List<string>(lines.Length);

        // Pass 1 — failing test blocks
        bool inFailBlock = false;
        bool inStackTrace = false;
        bool seenFirstStackFrame = false;
        int assertionLineCount = 0;
        const int maxAssertionLines = 10;

        foreach (var line in lines)
        {
            if (IsFailedTestLine().IsMatch(line))
            {
                inFailBlock = true;
                inStackTrace = false;
                seenFirstStackFrame = false;
                assertionLineCount = 0;
                kept.Add(line);
                continue;
            }

            if (inFailBlock)
            {
                if (IsStackTraceHeader().IsMatch(line))
                {
                    inStackTrace = true;
                    seenFirstStackFrame = false;
                    kept.Add(line);
                    continue;
                }

                if (inStackTrace)
                {
                    if (!seenFirstStackFrame && IsStackFrame().IsMatch(line))
                    {
                        kept.Add(line);
                        seenFirstStackFrame = true;
                    }
                    // stop the fail block at the blank line after the stack trace
                    if (line.Trim().Length == 0)
                    {
                        inFailBlock = false;
                        inStackTrace = false;
                    }
                    continue;
                }

                if (IsAssertionLine().IsMatch(line))
                {
                    if (assertionLineCount < maxAssertionLines)
                    {
                        kept.Add(line);
                        assertionLineCount++;
                    }
                    continue;
                }

                // blank line or next test result ends the fail block
                if (line.Trim().Length == 0 || IsFailedTestLine().IsMatch(line) || IsSummaryLine().IsMatch(line))
                {
                    inFailBlock = false;
                }
            }
        }

        // Pass 2 — summary section
        foreach (var line in lines)
        {
            if (IsTestRunLine().IsMatch(line) ||
                IsSummaryLine().IsMatch(line) ||
                IsCountLine().IsMatch(line))
            {
                kept.Add(line);
            }
        }

        var text = string.Join('\n', kept).TrimEnd();
        if (text.Length == 0)
            text = combined.TrimEnd();

        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, Id, ["parse-failures", "parse-summary"], false);
    }

    [GeneratedRegex(@"^\s*(Failed|X)\s+.+?\s+\[", RegexOptions.IgnoreCase)]
    private static partial Regex IsFailedTestLine();

    [GeneratedRegex(@"^\s+Stack Trace:", RegexOptions.IgnoreCase)]
    private static partial Regex IsStackTraceHeader();

    [GeneratedRegex(@"^\s+at\s+\S+")]
    private static partial Regex IsStackFrame();

    [GeneratedRegex(@"^\s+(Assert\.|Expected|Actual|Message:|System\.\w+Exception)", RegexOptions.IgnoreCase)]
    private static partial Regex IsAssertionLine();

    [GeneratedRegex(@"^(Passed!|Failed!)\s+-\s+Failed:", RegexOptions.IgnoreCase)]
    private static partial Regex IsSummaryLine();

    [GeneratedRegex(@"^\s+(Total|Passed|Failed|Skipped|Duration):", RegexOptions.IgnoreCase)]
    private static partial Regex IsCountLine();

    [GeneratedRegex(@"^Test run for", RegexOptions.IgnoreCase)]
    private static partial Regex IsTestRunLine();
}
