using System.Diagnostics;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Runner;
using Microsoft.Extensions.Logging;

namespace Hypa.Runtime.Application.Services;

public sealed record CompressOutput
{
    public string Text { get; init; } = string.Empty;
    public string ReducerId { get; init; } = string.Empty;
    public int OriginalTokens { get; init; }
    public int CompressedTokens { get; init; }
    public int SavingPercent { get; init; }
    public long DurationMs { get; init; }
}

public sealed class CompressService(
    IEnumerable<IOutputCompressor> compressors,
    ITokenCounter tokenCounter,
    IEvidenceLedger evidenceLedger,
    ISessionResolver sessionResolver,
    ILogger<CompressService> logger)
{
    private readonly IReadOnlyList<IOutputCompressor> _compressors = compressors.ToList();

    public async Task<Result<CompressOutput, Error>> CompressAsync(
        string input,
        string? kind,
        string? command,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(input))
            return Result<CompressOutput, Error>.Fail(new Error("INPUT_REQUIRED", "input is required."));

        var originalTokens = tokenCounter.EstimateTokens(input);
        var commandString = command ?? "compress";
        var invocation = CommandInvocation.Buffered("compress", [], commandString);
        var output = CommandOutput.Captured(input, string.Empty, 0, TimeSpan.Zero);
        var options = new CompressionOptions
        {
            MaxTotalLines = maxTokens.HasValue ? Math.Max(1, maxTokens.Value / 5) : 500,
        };

        var compressorIdResult = NormalizeCompressorId(kind);
        if (!compressorIdResult.IsOk)
            return Result<CompressOutput, Error>.Fail(compressorIdResult.Error);

        var compressor = compressorIdResult.Value is { } compressorId
                ? _compressors.FirstOrDefault(c => c.Id == compressorId)
                : null;
        compressor ??= _compressors.FirstOrDefault(c => c.CanHandle(invocation))
            ?? _compressors.FirstOrDefault(c => c.Id == "generic");

        string finalText;
        string reducerId;
        int compressedTokens;

        if (compressor is not null)
        {
            var result = compressor.Compress(invocation, output, options);
            finalText = result.Text;
            reducerId = result.ReducerId;
            compressedTokens = result.CompressedTokens;
        }
        else
        {
            finalText = input;
            reducerId = "passthrough";
            compressedTokens = originalTokens;
        }

        var saving = originalTokens > 0
            ? (int)Math.Round((1.0 - (double)compressedTokens / originalTokens) * 100)
            : 0;

        var text =
            $"SUMMARY\nCompressed {originalTokens} → {compressedTokens} tokens (-{saving}%).\n\n" +
            $"DETAILS\n{finalText.TrimEnd()}\n\n" +
            $"STATS\noriginal={originalTokens} compressed={compressedTokens} saving={saving}% reducer={reducerId} duration={sw.ElapsedMilliseconds}ms";

        var args = CapabilityToolEvidence.BuildArgsJson(
            ("kind", kind ?? "generic"),
            ("compressorId", compressorIdResult.Value),
            ("command", command),
            ("maxTokens", maxTokens?.ToString()));
        await CapabilityToolEvidence.RecordAsync(
            evidenceLedger,
            sessionResolver,
            logger,
            "hypa_compress",
            args,
            text,
            sw.ElapsedMilliseconds,
            cancellationToken);

        return Result<CompressOutput, Error>.Ok(new CompressOutput
        {
            Text = text,
            ReducerId = reducerId,
            OriginalTokens = originalTokens,
            CompressedTokens = compressedTokens,
            SavingPercent = saving,
            DurationMs = sw.ElapsedMilliseconds,
        });
    }

    private static Result<string?, Error> NormalizeCompressorId(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return Result<string?, Error>.Ok(null);

        return kind.ToLowerInvariant() switch
        {
            "generic" or "shell-output" or "log" or "code" => Result<string?, Error>.Ok("generic"),
            _ => Result<string?, Error>.Fail(new Error("INVALID_KIND", $"unsupported compression kind: {kind}")),
        };
    }
}
