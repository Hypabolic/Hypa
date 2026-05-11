using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Compression.Stages;

internal interface IStatefulCompressionStage : ICompressionStage
{
    bool WasTruncated { get; }
}

public sealed class TruncationStage(
    CompressionOptions options,
    ImportantLineClassifier classifier) : IStatefulCompressionStage
{
    public string Id => "truncate";
    public bool WasTruncated { get; private set; }

    public string Apply(string text)
    {
        WasTruncated = false;
        var lines = text.Split('\n');
        if (lines.Length <= options.MaxTotalLines)
            return text;

        WasTruncated = true;
        var head = lines[..options.MaxHeadLines];
        var tail = lines[^options.MaxTailLines..];
        var middleStart = options.MaxHeadLines;
        var middleEnd = lines.Length - options.MaxTailLines;
        var important = new List<string>();
        for (var i = middleStart; i < middleEnd; i++)
        {
            if (classifier.IsImportant(lines[i]))
                important.Add(lines[i]);
        }

        var omittedCount = middleEnd - middleStart - important.Count;
        var marker = $"[{omittedCount} lines omitted, {important.Count} safety-relevant lines preserved]";

        var result = new List<string>(head.Length + important.Count + 1 + tail.Length + 2);
        result.AddRange(head);
        result.Add(marker);
        result.AddRange(important);
        result.AddRange(tail);
        return string.Join('\n', result);
    }
}
