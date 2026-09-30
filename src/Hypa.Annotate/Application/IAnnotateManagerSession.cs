using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Lite manager popup session port for tests.
/// </summary>
public interface IAnnotateManagerSession
{
    AnnotateManagerCommand? ReadCommand(AnnotateManagerState state);
}

public sealed record AnnotateManagerState
{
    public required IReadOnlyList<Annotation> Active { get; init; }

    public required IReadOnlyList<ArchivedAnnotationSet> Archives { get; init; }
}

public sealed record AnnotateManagerCommand
{
    public required AnnotateManagerAction Action { get; init; }

    public int Index { get; init; } = 1;
}

public enum AnnotateManagerAction
{
    CopyOne,
    ArchiveAll,
    DeleteActive,
    RestoreArchive,
    DeleteArchive,
    Quit,
}
