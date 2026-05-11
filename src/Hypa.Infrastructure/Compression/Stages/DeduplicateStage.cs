using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Compression.Stages;

public sealed class DeduplicateStage : ICompressionStage
{
    public string Id => "deduplicate";

    public string Apply(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);
        var i = 0;
        while (i < lines.Length)
        {
            var current = lines[i];
            var runLength = 1;
            while (i + runLength < lines.Length && lines[i + runLength] == current)
                runLength++;

            result.Add(current);
            if (runLength >= 3)
                result.Add($"[... repeated {runLength - 1} times]");
            else if (runLength == 2)
                result.Add(current);

            i += runLength;
        }
        return string.Join('\n', result);
    }
}
