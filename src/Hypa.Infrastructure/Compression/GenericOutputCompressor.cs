using Hypa.Infrastructure.Compression.Stages;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Compression;

public sealed class GenericOutputCompressor(
    IEnumerable<ICompressionStage> stages,
    TruncationStage truncationStage,
    ITokenCounter tokenCounter) : IOutputCompressor
{
    private readonly IReadOnlyList<ICompressionStage> _stages = stages.ToList();

    public string Id => "generic";

    public bool CanHandle(CommandInvocation invocation) => true;

    public CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options)
    {
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        var text = combined;
        var appliedStages = new List<string>(_stages.Count + 1);

        foreach (var stage in _stages)
        {
            var next = stage.Apply(text);
            if (next.Length <= text.Length)
            {
                text = next;
                appliedStages.Add(stage.Id);
            }
        }

        var afterTruncation = truncationStage.Apply(text);
        if (afterTruncation.Length <= text.Length)
        {
            text = afterTruncation;
            appliedStages.Add(truncationStage.Id);
        }

        var compressedTokens = tokenCounter.EstimateTokens(text);

        return CompressionResult.From(
            text,
            originalTokens,
            compressedTokens,
            Id,
            appliedStages,
            truncationStage.WasTruncated);
    }
}
