using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace Hypa.Runtime.Application.Services;

internal static class CapabilityToolEvidence
{
    internal static async Task RecordAsync(
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        ILogger logger,
        string toolName,
        string argsJson,
        string resultText,
        long durationMs,
        CancellationToken ct)
    {
        var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), ct);
        if (!sessionResult.IsOk)
            logger.LogWarning("session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);

        await evidenceLedger.RecordToolCallAsync(new ToolCallRecord
        {
            SessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty,
            ToolName = toolName,
            Args = argsJson,
            ArgsHash = HashString(argsJson),
            Result = resultText[..Math.Min(200, resultText.Length)],
            OutputHash = HashString(resultText),
            DurationMs = durationMs,
        }, ct);
    }

    internal static string BuildArgsJson(params (string Key, string? Value)[] pairs)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var (key, value) in pairs)
        {
            if (value is not null)
                writer.WriteString(key, value);
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string HashString(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
