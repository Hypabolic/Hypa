using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Parsers.Canonical;

namespace Hypa.Infrastructure.Parsers;

public sealed class TestRunResultFormatter : ITokenFormatter<TestRunResult>
{
    public string Format(TestRunResult result, FormatMode mode)
    {
        var sb = new StringBuilder();
        sb.Append($"Passed: {result.Passed}, Failed: {result.Failed}, Skipped: {result.Skipped}, Total: {result.Total}");
        if (result.Duration.Length > 0)
            sb.Append($", Duration: {result.Duration}");
        sb.AppendLine();

        if (result.FailingTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var test in result.FailingTests)
            {
                sb.AppendLine($"  {test.Name}");
                if (test.Message.Length > 0)
                {
                    foreach (var line in test.Message.Split('\n'))
                        sb.AppendLine($"    {line}");
                }
                if (mode == FormatMode.Verbose && test.StackTrace.Length > 0)
                {
                    foreach (var line in test.StackTrace.Split('\n').Take(3))
                        sb.AppendLine($"    {line}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }
}
