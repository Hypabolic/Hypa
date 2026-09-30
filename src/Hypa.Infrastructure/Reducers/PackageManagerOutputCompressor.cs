using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Reducers;

public sealed partial class PackageManagerOutputCompressor(ITokenCounter tokenCounter) : IOutputCompressor
{

    public string Id => "pkg-manager";

    public bool CanHandle(CommandInvocation invocation)
    {
        var executable = invocation.Executable.AsSpan();
        var separatorIndex = Math.Max(executable.LastIndexOf('/'), executable.LastIndexOf('\\'));
        executable = executable[(separatorIndex + 1)..];

        var suffixIndex = executable.LastIndexOf('.');
        if (suffixIndex > 0 && IsWindowsShimSuffix(executable[suffixIndex..]))
            executable = executable[..suffixIndex];

        return executable.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("yarn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsShimSuffix(ReadOnlySpan<char> suffix) =>
        suffix.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
        suffix.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
        suffix.Equals(".bat", StringComparison.OrdinalIgnoreCase);

    public CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options)
    {
        var combined = (output.Stdout.Length, output.Stderr.Length) switch
        {
            (0, 0) => string.Empty,
            (0, _) => output.Stderr,
            (_, 0) => output.Stdout,
            _ => output.Stdout + "\n" + output.Stderr,
        };
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
