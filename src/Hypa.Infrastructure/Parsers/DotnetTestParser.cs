using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Parsers.Canonical;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Parsers;

public sealed partial class DotnetTestParser : IOutputParser<TestRunResult>
{
    public string Id => "dotnet-test";

    public bool CanParse(CommandInvocation invocation) =>
        invocation.Executable == "dotnet" &&
        invocation.Arguments.Count > 0 &&
        invocation.Arguments[0] == "test";

    public ParseResult<TestRunResult> TryParse(CommandOutput output)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var lines = combined.Split('\n');

        var failingTests = new List<FailingTest>();
        int passed = 0, failed = 0, skipped = 0;
        string duration = string.Empty;

        // Pass 1 — collect failing test blocks
        string? currentName = null;
        var messageLines = new List<string>();
        var stackLines = new List<string>();
        bool inStackTrace = false;

        foreach (var line in lines)
        {
            var failMatch = IsFailedTestLine().Match(line);
            if (failMatch.Success)
            {
                if (currentName is not null)
                    failingTests.Add(new FailingTest(currentName, string.Join('\n', messageLines), string.Join('\n', stackLines)));
                currentName = failMatch.Groups[2].Value.Trim();
                messageLines.Clear();
                stackLines.Clear();
                inStackTrace = false;
                continue;
            }

            if (currentName is not null)
            {
                if (IsStackTraceHeader().IsMatch(line)) { inStackTrace = true; continue; }
                if (inStackTrace)
                {
                    if (IsStackFrame().IsMatch(line)) stackLines.Add(line.Trim());
                    else if (line.Trim().Length == 0) { inStackTrace = false; }
                    continue;
                }
                if (IsAssertionLine().IsMatch(line)) messageLines.Add(line.Trim());
            }
        }
        if (currentName is not null)
            failingTests.Add(new FailingTest(currentName, string.Join('\n', messageLines), string.Join('\n', stackLines)));

        // Pass 2 — summary counts
        bool foundSummary = false;
        foreach (var line in lines)
        {
            var countMatch = IsCountLine().Match(line);
            if (!countMatch.Success) continue;
            foundSummary = true;
            var label = countMatch.Groups[1].Value.ToLowerInvariant();
            var value = countMatch.Groups[2].Value.Trim();
            switch (label)
            {
                case "passed": int.TryParse(value, out passed); break;
                case "failed": int.TryParse(value, out failed); break;
                case "skipped": int.TryParse(value, out skipped); break;
                case "duration": duration = value; break;
            }
        }

        if (!foundSummary)
            return new ParseResult<TestRunResult>(null, ParseTier.Degraded, false);

        var result = new TestRunResult
        {
            Passed = passed,
            Failed = failed,
            Skipped = skipped,
            Total = passed + failed + skipped,
            Duration = duration,
            FailingTests = failingTests,
        };
        return new ParseResult<TestRunResult>(result, ParseTier.Full, true);
    }

    [GeneratedRegex(@"^\s*(Failed|X)\s+(.+?)\s+\[", RegexOptions.IgnoreCase)]
    private static partial Regex IsFailedTestLine();

    [GeneratedRegex(@"^\s+Stack Trace:", RegexOptions.IgnoreCase)]
    private static partial Regex IsStackTraceHeader();

    [GeneratedRegex(@"^\s+at\s+\S+")]
    private static partial Regex IsStackFrame();

    [GeneratedRegex(@"^\s+(Assert\.|Expected|Actual|Message:|System\.\w+Exception)", RegexOptions.IgnoreCase)]
    private static partial Regex IsAssertionLine();

    [GeneratedRegex(@"^\s+(Total|Passed|Failed|Skipped|Duration):\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex IsCountLine();
}
