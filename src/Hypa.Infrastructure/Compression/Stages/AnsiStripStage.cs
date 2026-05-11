using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Compression.Stages;

public sealed partial class AnsiStripStage : ICompressionStage
{
    public string Id => "strip-ansi";

    [GeneratedRegex(@"\x1B\[[0-9;]*[mGKHFJABCDEFfnsuhl]|\x1B\][^\x07]*\x07|\x1B[()][\w]")]
    private static partial Regex AnsiPattern();

    public string Apply(string text) => AnsiPattern().Replace(text, string.Empty);
}
