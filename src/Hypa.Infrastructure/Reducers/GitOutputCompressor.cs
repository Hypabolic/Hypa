using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Reducers;

public sealed partial class GitOutputCompressor(ITokenCounter tokenCounter) : IOutputCompressor
{
    private static readonly HashSet<string> SupportedSubs = ["status", "diff", "log"];

    public string Id => "git";

    public bool CanHandle(CommandInvocation invocation) =>
        invocation.Executable == "git" &&
        invocation.Arguments.Count > 0 &&
        SupportedSubs.Contains(invocation.Arguments[0]);

    public CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options)
    {
        var sub = invocation.Arguments[0];
        return sub switch
        {
            "status" => CompressStatus(output),
            "diff" => CompressDiff(invocation, output),
            "log" => CompressLog(output),
            _ => throw new InvalidOperationException($"Unexpected git sub-command: {sub}"),
        };
    }

    private CompressionResult CompressStatus(CommandOutput output)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        var kept = new List<string>();
        foreach (var line in combined.Split('\n'))
        {
            if (IsOnBranchLine().IsMatch(line) ||
                IsAheadBehindLine().IsMatch(line) ||
                IsConflictLine().IsMatch(line) ||
                IsChangedFileLine().IsMatch(line) ||
                IsSectionHeader().IsMatch(line))
            {
                kept.Add(line);
            }
        }

        var text = string.Join('\n', kept).TrimEnd();
        if (text.Length == 0)
            text = combined.TrimEnd();

        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, "git-status", ["parse-status"], false);
    }

    private CompressionResult CompressDiff(CommandInvocation invocation, CommandOutput output)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        // --stat is already compact — pass through as-is
        if (invocation.Arguments.Contains("--stat"))
        {
            var statTokens = tokenCounter.EstimateTokens(combined);
            return CompressionResult.From(combined.TrimEnd(), originalTokens, statTokens, "git-diff-stat", [], false);
        }

        var lines = combined.Split('\n');
        var kept = new List<string>(lines.Length);
        bool dropContext = lines.Length > 150;

        foreach (var line in lines)
        {
            if (IsDiffHeader().IsMatch(line) ||
                IsFileLine().IsMatch(line) ||
                IsHunkHeader().IsMatch(line) ||
                IsAddedLine().IsMatch(line) ||
                IsRemovedLine().IsMatch(line))
            {
                kept.Add(line);
            }
            else if (!dropContext)
            {
                kept.Add(line);
            }
        }

        var text = string.Join('\n', kept).TrimEnd();
        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, "git-diff", ["parse-diff"], dropContext);
    }

    private CompressionResult CompressLog(CommandOutput output)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        var lines = combined.Split('\n');
        var kept = new List<string>(lines.Length);

        bool inHeader = false;
        bool seenBlankAfterDate = false;
        bool subjectCaptured = false;

        foreach (var line in lines)
        {
            if (IsCommitHash().IsMatch(line))
            {
                kept.Add(line);
                inHeader = true;
                seenBlankAfterDate = false;
                subjectCaptured = false;
                continue;
            }

            if (!inHeader) continue;

            if (IsAuthorLine().IsMatch(line) || IsDateLine().IsMatch(line))
            {
                kept.Add(line);
                continue;
            }

            // blank line between header and commit body
            if (line.Trim().Length == 0 && !seenBlankAfterDate)
            {
                seenBlankAfterDate = true;
                continue;
            }

            // first non-blank line after header blank = subject
            if (seenBlankAfterDate && !subjectCaptured && line.Trim().Length > 0)
            {
                kept.Add(line);
                subjectCaptured = true;
                continue;
            }
        }

        var text = string.Join('\n', kept).TrimEnd();
        if (text.Length == 0)
            text = combined.TrimEnd();

        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, "git-log", ["parse-log"], false);
    }

    [GeneratedRegex(@"^On branch |^HEAD detached")]
    private static partial Regex IsOnBranchLine();

    [GeneratedRegex(@"^Your branch (is ahead|is behind|and .* have diverged)")]
    private static partial Regex IsAheadBehindLine();

    [GeneratedRegex(@"^You have unmerged paths|^both modified:")]
    private static partial Regex IsConflictLine();

    [GeneratedRegex(@"^\t\S")]
    private static partial Regex IsChangedFileLine();

    [GeneratedRegex(@"^(Changes to be committed:|Changes not staged for commit:|Untracked files:)")]
    private static partial Regex IsSectionHeader();

    [GeneratedRegex(@"^diff --git ")]
    private static partial Regex IsDiffHeader();

    [GeneratedRegex(@"^(--- a/|\+\+\+ b/)")]
    private static partial Regex IsFileLine();

    [GeneratedRegex(@"^@@")]
    private static partial Regex IsHunkHeader();

    [GeneratedRegex(@"^\+")]
    private static partial Regex IsAddedLine();

    [GeneratedRegex(@"^-")]
    private static partial Regex IsRemovedLine();

    [GeneratedRegex(@"^commit [0-9a-f]{7,40}")]
    private static partial Regex IsCommitHash();

    [GeneratedRegex(@"^Author:")]
    private static partial Regex IsAuthorLine();

    [GeneratedRegex(@"^Date:")]
    private static partial Regex IsDateLine();
}
