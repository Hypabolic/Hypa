namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Mux-local Work generation fence. Continuity remains the authority that
/// chooses the generation. The mux only records ids from protocol fields.
/// </summary>
public sealed class WorkGenerationGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _highest = new(StringComparer.Ordinal);

    public bool AllowsStart(string? workId, long? generation)
    {
        if (!TryNormalize(workId, generation, out var id, out var gen))
            return true;

        lock (_gate)
        {
            if (_highest.TryGetValue(id, out var seen) && gen < seen)
                return false;
        }

        return true;
    }

    public bool AllowsOccupant(string? workId, long occupantGeneration)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return true;

        var id = workId.Trim();
        if (id.Length == 0)
            return true;

        // Generation 0 is valid only when WorkId is null. A Work occupant
        // without a positive generation is not entitled to mutate.
        if (occupantGeneration <= 0)
            return false;

        lock (_gate)
        {
            if (_highest.TryGetValue(id, out var seen) && occupantGeneration < seen)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Raise the highest seen generation for a Work. A lower value is ignored.
    /// </summary>
    public void Remember(string? workId, long? generation)
    {
        if (!TryNormalize(workId, generation, out var id, out var gen))
            return;

        lock (_gate)
        {
            if (!_highest.TryGetValue(id, out var seen) || gen > seen)
                _highest[id] = gen;
        }
    }

    public long? Highest(string? workId)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return null;

        var id = workId.Trim();
        lock (_gate)
            return _highest.TryGetValue(id, out var seen) ? seen : null;
    }

    private static bool TryNormalize(string? workId, long? generation, out string id, out long gen)
    {
        id = string.Empty;
        gen = 0;
        if (string.IsNullOrWhiteSpace(workId) || generation is null)
            return false;
        if (generation.Value < 1)
            return false;
        id = workId.Trim();
        gen = generation.Value;
        return id.Length > 0;
    }
}
