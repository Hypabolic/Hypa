using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Reducers;

public sealed partial class PackageManagerOutputCompressor(ITokenCounter tokenCounter) : IOutputCompressor
{
    private static readonly HashSet<string> Executables = ["npm", "pnpm", "yarn"];

    public string Id => "pkg-manager";

    public bool CanHandle(CommandInvocation invocation) =>
        Executables.Contains(invocation.Executable);

    public CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        // successful small outputs need no reduction
        if (output.ExitCode == 0 && originalTokens <= options.SmallOutputThreshold)
            return CompressionResult.Passthrough(combined.TrimEnd(), originalTokens);

        var lines = combined.Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (IsErrorLine().IsMatch(line) ||
                IsPeerConflictLine().IsMatch(line) ||
                IsInstallSummaryLine().IsMatch(line))
            {
                kept.Add(line);
            }
        }

        var text = string.Join('\n', kept).TrimEnd();
        if (text.Length == 0)
            text = combined.TrimEnd();

        var compressedTokens = tokenCounter.EstimateTokens(text);
        return CompressionResult.From(text, originalTokens, compressedTokens, Id, ["parse-errors"], false);
    }

    [GeneratedRegex(@"(^|\s)(npm (ERR!|error|warn)|ERR_PNPM|pnpm ERR!|Error:|error |warning |hint:)", RegexOptions.IgnoreCase)]
    private static partial Regex IsErrorLine();

    [GeneratedRegex(@"\bpeer\b.*(conflict|unmet|incompatible)", RegexOptions.IgnoreCase)]
    private static partial Regex IsPeerConflictLine();

    [GeneratedRegex(@"(packages|dependencies).*(added|removed|updated|installed)", RegexOptions.IgnoreCase)]
    private static partial Regex IsInstallSummaryLine();
}
