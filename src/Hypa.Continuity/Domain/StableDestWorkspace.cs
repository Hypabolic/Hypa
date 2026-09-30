namespace Hypa.Continuity.Domain;

/// <summary>
/// Cube dest workspace convention. v1 requires this stable path.
/// This is not a Cube factory.
/// </summary>
public static class StableDestWorkspace
{
    public const string Root = "/work";

    public static string ForWork(WorkId workId)
    {
        var id = workId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (var ch in id)
        {
            if (ch is '/' or '\\' or ':' or '\0')
                throw new ArgumentException("work id is not a path segment", nameof(workId));
        }

        return Root + "/" + id;
    }
}
