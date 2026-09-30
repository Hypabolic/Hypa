using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Recoverable copy/archive and restore transitions.
/// </summary>
public static class AnnotateArchiveWorkflow
{
    public static CopyAndArchiveOutcome CopyAndArchive(CopyAndArchiveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var active = request.LoadActive();
        if (!active.IsOk)
            return CopyAndArchiveOutcome.StayOpen(active.Error);

        if (active.Value.Count == 0)
            return CopyAndArchiveOutcome.StayOpen("Nothing to copy and archive.");

        var newestFirst = AnnotationStore.NewestFirst(active.Value);
        var markdown = AnnotationMarkdown.Format(newestFirst);
        var copied = request.WriteClipboard(markdown);
        if (!copied.IsOk)
            return CopyAndArchiveOutcome.StayOpen(copied.Error);

        var archive = new ArchivedAnnotationSet
        {
            Id = request.CreateArchiveId(),
            ArchivedAt = request.NowIso(),
            Annotations = active.Value.ToArray(),
        };

        var saved = request.SaveArchive(archive);
        if (!saved.IsOk)
            return CopyAndArchiveOutcome.StayOpen(saved.Error);

        var ids = active.Value.Select(annotation => annotation.Id).ToArray();
        var removed = request.RemoveActive(ids);
        if (!removed.IsOk)
        {
            return CopyAndArchiveOutcome.ArchivedActiveRetained(removed.Error);
        }

        return CopyAndArchiveOutcome.Close(active.Value.Count);
    }

    public static RestoreArchivedSetOutcome RestoreArchivedSet(
        ArchivedAnnotationSet archive,
        RestoreArchiveRequest request)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(request);

        var restoredCount = request.MergeActive(archive.Annotations);
        if (!restoredCount.IsOk)
            return RestoreArchivedSetOutcome.StayOpen(restoredCount.Error);

        var removed = request.RemoveArchive(archive.Id);
        if (!removed.IsOk)
        {
            return RestoreArchivedSetOutcome.RestoredArchiveRetained(
                restoredCount.Value,
                removed.Error);
        }

        return RestoreArchivedSetOutcome.Restored(restoredCount.Value);
    }
}

public sealed record CopyAndArchiveRequest
{
    public required Func<Result<IReadOnlyList<Annotation>, string>> LoadActive { get; init; }

    public required Func<string, Result<bool, string>> WriteClipboard { get; init; }

    public required Func<ArchivedAnnotationSet, Result<StoreAck, string>> SaveArchive { get; init; }

    public required Func<IReadOnlyList<string>, Result<StoreAck, string>> RemoveActive { get; init; }

    public required Func<string> CreateArchiveId { get; init; }

    public required Func<string> NowIso { get; init; }
}

public sealed record RestoreArchiveRequest
{
    public required Func<IReadOnlyList<Annotation>, Result<int, string>> MergeActive { get; init; }

    public required Func<string, Result<StoreAck, string>> RemoveArchive { get; init; }
}

public sealed record CopyAndArchiveOutcome
{
    public CopyAndArchiveKind Kind { get; init; }

    public string? Message { get; init; }

    public int ArchivedCount { get; init; }

    public static CopyAndArchiveOutcome Close(int archivedCount) =>
        new() { Kind = CopyAndArchiveKind.Close, ArchivedCount = archivedCount };

    public static CopyAndArchiveOutcome StayOpen(string message) =>
        new() { Kind = CopyAndArchiveKind.StayOpen, Message = message };

    public static CopyAndArchiveOutcome ArchivedActiveRetained(string message) =>
        new() { Kind = CopyAndArchiveKind.ArchivedActiveRetained, Message = message };
}

public enum CopyAndArchiveKind
{
    Close,
    StayOpen,
    ArchivedActiveRetained,
}

public sealed record RestoreArchivedSetOutcome
{
    public RestoreArchivedSetKind Kind { get; init; }

    public string? Message { get; init; }

    public int RestoredCount { get; init; }

    public static RestoreArchivedSetOutcome Restored(int restoredCount) =>
        new() { Kind = RestoreArchivedSetKind.Restored, RestoredCount = restoredCount };

    public static RestoreArchivedSetOutcome StayOpen(string message) =>
        new() { Kind = RestoreArchivedSetKind.StayOpen, Message = message };

    public static RestoreArchivedSetOutcome RestoredArchiveRetained(int restoredCount, string message) =>
        new()
        {
            Kind = RestoreArchivedSetKind.RestoredArchiveRetained,
            RestoredCount = restoredCount,
            Message = message,
        };
}

public enum RestoreArchivedSetKind
{
    Restored,
    StayOpen,
    RestoredArchiveRetained,
}
