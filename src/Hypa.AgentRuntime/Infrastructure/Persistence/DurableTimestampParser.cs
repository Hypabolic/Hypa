using System.Globalization;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Shared restore parser for persisted timestamps. Missing or corrupt values
/// keep <see cref="DateTimeOffset.UnixEpoch"/>. Never substitutes
/// <see cref="DateTimeOffset.UtcNow"/> — restore age must stay honest.
/// </summary>
internal static class DurableTimestampParser
{
    /// <summary>Sentinel used when a persisted timestamp is missing or corrupt.</summary>
    public static DateTimeOffset Sentinel { get; } = DateTimeOffset.UnixEpoch;

    public static DateTimeOffset ParseOrSentinel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Sentinel;

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dto))
        {
            return dto.ToUniversalTime();
        }

        return Sentinel;
    }
}
