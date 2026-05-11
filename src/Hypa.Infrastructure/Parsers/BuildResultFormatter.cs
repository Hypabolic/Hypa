using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Parsers.Canonical;

namespace Hypa.Infrastructure.Parsers;

public sealed class BuildResultFormatter : ITokenFormatter<BuildResult>
{
    public string Format(BuildResult result, FormatMode mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Succeeded ? "Build succeeded." : "Build FAILED.");

        foreach (var d in result.Errors)
            sb.AppendLine($"{d.File}({d.Line},{d.Column}): error {d.Code}: {d.Message}");

        if (mode == FormatMode.Verbose)
        {
            foreach (var d in result.Warnings)
                sb.AppendLine($"{d.File}({d.Line},{d.Column}): warning {d.Code}: {d.Message}");
        }

        if (result.Errors.Count > 0 || result.Warnings.Count > 0)
            sb.AppendLine($"{result.Errors.Count} Error(s), {result.Warnings.Count} Warning(s)");

        if (result.ElapsedTime.Length > 0)
            sb.AppendLine(result.ElapsedTime);

        return sb.ToString().TrimEnd();
    }
}
