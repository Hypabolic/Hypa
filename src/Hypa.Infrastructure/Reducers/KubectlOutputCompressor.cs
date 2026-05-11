using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Reducers;

public sealed partial class KubectlOutputCompressor(ITokenCounter tokenCounter) : IOutputCompressor
{
    private static readonly HashSet<string> SupportedSubs = ["get", "describe"];
    private const int GetMaxLines = 300;
    private const int DescribeEventLines = 20;

    public string Id => "kubectl";

    public bool CanHandle(CommandInvocation invocation) =>
        invocation.Executable == "kubectl" &&
        invocation.Arguments.Count > 0 &&
        SupportedSubs.Contains(invocation.Arguments[0]);

    public CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options)
    {
        return invocation.Arguments[0] == "get"
            ? CompressGet(output)
            : CompressDescribe(output);
    }

    private CompressionResult CompressGet(CommandOutput output)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        var lines = combined.Split('\n');

        if (lines.Length <= GetMaxLines)
        {
            return CompressionResult.From(combined.TrimEnd(), originalTokens, originalTokens, "kubectl-get", [], false);
        }

        // preserve header row (line 0) + head/tail within limit
        var kept = new List<string>(GetMaxLines + 2);
        var header = lines[0];
        var dataLines = lines[1..];
        int half = (GetMaxLines - 1) / 2;

        kept.Add(header);
        kept.AddRange(dataLines.Take(half));
        kept.Add($"[... {dataLines.Length - GetMaxLines + 1} lines omitted ...]");
        kept.AddRange(dataLines.TakeLast(half));

        var text = string.Join('\n', kept).TrimEnd();
        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, "kubectl-get", ["truncate"], true);
    }

    private CompressionResult CompressDescribe(CommandOutput output)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        var lines = combined.Split('\n');
        var kept = new List<string>(lines.Length);

        bool inEvents = false;
        bool inConditions = false;
        var eventLines = new List<string>();
        bool inAnnotations = false;

        foreach (var line in lines)
        {
            // always keep warning/error events anywhere
            if (IsWarningEvent().IsMatch(line))
            {
                kept.Add(line);
                continue;
            }

            if (IsEventsHeader().IsMatch(line))
            {
                inEvents = true;
                inAnnotations = false;
                inConditions = false;
                eventLines.Clear();
                eventLines.Add(line);
                continue;
            }

            if (inEvents)
            {
                if (line.Trim().Length == 0 || IsNewSection().IsMatch(line))
                {
                    // flush last N event lines
                    kept.AddRange(eventLines.TakeLast(DescribeEventLines));
                    inEvents = false;
                    if (line.Trim().Length > 0)
                        goto processLine;
                }
                else
                {
                    eventLines.Add(line);
                }
                continue;
            }

        processLine:
            if (IsConditionsHeader().IsMatch(line))
            {
                inConditions = true;
                inAnnotations = false;
                kept.Add(line);
                continue;
            }

            if (IsAnnotationsHeader().IsMatch(line))
            {
                inAnnotations = true;
                inConditions = false;
                continue;
            }

            if (IsNewSection().IsMatch(line))
            {
                inAnnotations = false;
                inConditions = false;
            }

            if (inAnnotations)
                continue;

            if (IsResourceHeader().IsMatch(line) || inConditions)
            {
                kept.Add(line);
            }
        }

        // flush events if output ended while still in events section
        if (inEvents)
            kept.AddRange(eventLines.TakeLast(DescribeEventLines));

        var text = string.Join('\n', kept).TrimEnd();
        if (text.Length == 0)
            text = combined.TrimEnd();

        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, "kubectl-describe", ["parse-describe"], false);
    }

    [GeneratedRegex(@"^(Name|Namespace|Status|Labels|Selector|IP|IPs|Node|Start Time|QoS Class|Priority):\s")]
    private static partial Regex IsResourceHeader();

    [GeneratedRegex(@"^Conditions:")]
    private static partial Regex IsConditionsHeader();

    [GeneratedRegex(@"^(Annotations|Tolerations|Volumes|Init Containers|Containers|Node-Selectors|Controlled By|Limits|Requests|Liveness|Readiness|Startup|Environment|Mounts):")]
    private static partial Regex IsAnnotationsHeader();

    [GeneratedRegex(@"^Events:")]
    private static partial Regex IsEventsHeader();

    [GeneratedRegex(@"\b(Warning|Failed|BackOff|CrashLoop|OOMKilled|Error)\b")]
    private static partial Regex IsWarningEvent();

    [GeneratedRegex(@"^\S")]
    private static partial Regex IsNewSection();
}
