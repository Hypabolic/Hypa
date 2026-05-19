using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Metrics;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace Hypa.Runtime.Application.Services;

public sealed class CommandRunnerService(
    ICommandRunner runner,
    IEnumerable<IOutputCompressor> compressors,
    ITokenCounter tokenCounter,
    IArtifactRepository artifacts,
    IEvidenceLedger evidence,
    ISessionResolver sessionResolver,
    FilterService filterService,
    IFilterEngine filterEngine,
    IParseMetricsRepository parseMetrics,
    ILogger<CommandRunnerService> logger) : ICommandRunnerService
{
    private readonly IReadOnlyList<IOutputCompressor> _compressors = compressors.ToList();

    public async Task<Result<BufferedRunOutput, Error>> RunBufferedAsync(
        CommandInvocation invocation,
        CompressionOptions options,
        CancellationToken ct)
    {
        var runResult = await runner.RunAsync(invocation, ct);
        if (!runResult.IsOk)
            return Result<BufferedRunOutput, Error>.Fail(runResult.Error);

        var output = runResult.Value;
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var originalTokens = tokenCounter.EstimateTokens(combined);

        string finalText;
        string reducerId = "passthrough";
        int compressedTokens = originalTokens;
        bool wasTruncated = false;

        if (originalTokens <= options.SmallOutputThreshold)
        {
            finalText = combined;
        }
        else
        {
            var compressor = _compressors.FirstOrDefault(c => c.CanHandle(invocation));
            if (compressor is null)
                return Result<BufferedRunOutput, Error>.Fail(new Error("NO_COMPRESSOR", "No compressor registered."));

            var result = compressor.Compress(invocation, output, options);
            reducerId = result.ReducerId;
            wasTruncated = result.WasTruncated;

            if (result.CompressedTokens >= originalTokens)
            {
                finalText = combined;
                compressedTokens = originalTokens;
                reducerId = "passthrough";
            }
            else
            {
                finalText = result.Text;
                compressedTokens = result.CompressedTokens;
            }
        }

        Guid? teeArtifactId = null;
        var sessionId = await ResolveSessionIdBestEffortAsync(ct);

        if ((output.ExitCode != 0 || output.WasTimedOut) && options.TeeOnFailure)
        {
            teeArtifactId = await TeeAsync(combined, sessionId, ct);
        }
        else if (wasTruncated && options.TeeOnTruncation)
        {
            teeArtifactId = await TeeAsync(combined, sessionId, ct);
        }

        // Apply DSL filters (built-in → user-global → trusted project-local)
        string? appliedFilterId = null;
        foreach (var filter in filterService.GetApplicableFilters(invocation.Executable, invocation.OriginalCommand))
        {
            var textForFilter = filter.MergeStderr && output.Stderr.Length > 0
                ? finalText.TrimEnd() + "\n" + output.Stderr
                : finalText;
            var fr = filterEngine.Apply(filter, textForFilter);
            var voided = string.IsNullOrWhiteSpace(fr.Text) && !string.IsNullOrWhiteSpace(finalText);
            if (fr.StagesApplied > 0 && !voided)
            {
                finalText = fr.Text;
                appliedFilterId = filter.Id;
                break;
            }
        }

        compressedTokens = tokenCounter.EstimateTokens(finalText);

        var commandMetrics = new CommandMetricsRecord
        {
            SessionId = sessionId,
            Command = invocation.OriginalCommand,
            ExitCode = output.ExitCode,
            DurationMs = (long)output.Duration.TotalMilliseconds,
            OriginalTokens = originalTokens,
            CompressedTokens = compressedTokens,
            ReducerId = reducerId,
            TeeArtifactId = teeArtifactId,
        };
        await RecordCommandMetricsBestEffortAsync(commandMetrics, ct);

        // Record parse metrics
        await RecordParseMetricsBestEffortAsync(new ParseMetricsRecord
        {
            RunId = commandMetrics.Id.ToString(),
            Executable = invocation.Executable,
            Arguments = string.Join(' ', invocation.Arguments),
            ParseTier = ParseTier.Passthrough,
            FilterId = appliedFilterId,
            RecordedAt = DateTimeOffset.UtcNow,
        }, ct);

        if (originalTokens > options.SmallOutputThreshold && compressedTokens < originalTokens)
        {
            var saving = (int)Math.Round((1.0 - (double)compressedTokens / originalTokens) * 100);
            var metaLine = $"[hypa: {originalTokens}→{compressedTokens} tok, -{saving}%, reducer={reducerId}]";
            if (teeArtifactId.HasValue)
                metaLine += $"\n[hypa: full output -> artifact:{teeArtifactId.Value:N}, expires in 24h]";
            finalText = finalText.TrimEnd() + "\n" + metaLine;
        }
        else if (teeArtifactId.HasValue)
        {
            finalText = finalText.TrimEnd() + $"\n[hypa: full output -> artifact:{teeArtifactId.Value:N}, expires in 24h]";
        }

        return Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput(finalText, output.ExitCode));
    }

    public async Task<Result<int, Error>> RunPassthroughAsync(
        CommandInvocation invocation,
        CancellationToken ct)
    {
        var passthroughInvocation = invocation with { Mode = ToolRunMode.Passthrough };
        var runResult = await runner.RunAsync(passthroughInvocation, ct);
        if (!runResult.IsOk)
            return Result<int, Error>.Fail(runResult.Error);

        var output = runResult.Value;
        var sessionId = await ResolveSessionIdBestEffortAsync(ct);

        await RecordCommandMetricsBestEffortAsync(new CommandMetricsRecord
        {
            SessionId = sessionId,
            Command = invocation.OriginalCommand,
            ExitCode = output.ExitCode,
            DurationMs = (long)output.Duration.TotalMilliseconds,
            OriginalTokens = 0,
            CompressedTokens = 0,
            ReducerId = "passthrough",
        }, ct);

        return Result<int, Error>.Ok(output.ExitCode);
    }

    private async Task<Guid?> TeeAsync(string content, Guid sessionId, CancellationToken ct)
    {
        try
        {
            var storeResult = await artifacts.StoreAsync(content, "text/plain", sessionId, ct);
            if (!storeResult.IsOk)
                logger.LogDebug("Failed to tee command output: {Error}", storeResult.Error.Message);
            return storeResult.IsOk ? storeResult.Value.Id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to tee command output");
            return null;
        }
    }

    private async Task RecordCommandMetricsBestEffortAsync(CommandMetricsRecord record, CancellationToken ct)
    {
        try
        {
            await evidence.RecordCommandMetricsAsync(record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to record command metrics");
        }
    }

    private async Task RecordParseMetricsBestEffortAsync(ParseMetricsRecord record, CancellationToken ct)
    {
        try
        {
            await parseMetrics.RecordAsync(record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to record parse metrics");
        }
    }

    private async Task<Guid> ResolveSessionIdBestEffortAsync(CancellationToken ct)
    {
        try
        {
            var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), ct);
            if (sessionResult.IsOk)
                return sessionResult.Value.Id;

            logger.LogDebug("Session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
            return Guid.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to resolve session, recording with empty ID");
            return Guid.Empty;
        }
    }
}
