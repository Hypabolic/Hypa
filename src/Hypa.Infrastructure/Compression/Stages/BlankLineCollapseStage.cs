using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Compression.Stages;

public sealed partial class BlankLineCollapseStage : ICompressionStage
{
    public string Id => "collapse-blank-lines";

    [GeneratedRegex(@"(\r?\n){3,}")]
    private static partial Regex MultipleBlankLines();

    public string Apply(string text) => MultipleBlankLines().Replace(text, "\n\n");
}
