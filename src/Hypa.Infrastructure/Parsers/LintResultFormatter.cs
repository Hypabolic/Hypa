using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Parsers.Canonical;

namespace Hypa.Infrastructure.Parsers;

public sealed class LintResultFormatter : ITokenFormatter<LintResult>
{
    public string Format(LintResult result, FormatMode mode)
    {
        var sb = new StringBuilder();
        string? lastFile = null;

        var filtered = mode == FormatMode.Compact
            ? result.Diagnostics.Where(d => d.Severity == "error")
            : result.Diagnostics;

        foreach (var d in filtered)
        {
            if (d.File != lastFile)
            {
                sb.AppendLine($"=== {d.File} ===");
                lastFile = d.File;
            }
            sb.AppendLine($"  ({d.Line},{d.Column}): {d.Severity} {d.Code}: {d.Message}");
        }

        sb.AppendLine($"Found {result.ErrorCount} error(s), {result.WarningCount} warning(s)");
        return sb.ToString().TrimEnd();
    }
}
