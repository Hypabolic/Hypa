using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Copy active annotations as Markdown, then archive them.
/// </summary>
public static class AnnotateCopyArchiveService
{
    public const string EmptyMessage = "There is nothing to copy yet.";

    public static Result<CopyArchiveReport, string> Run(CopyArchiveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loadedEmpty = false;
        var outcome = AnnotateArchiveWorkflow.CopyAndArchive(new CopyAndArchiveRequest
        {
            LoadActive = () =>
            {
                var loaded = AnnotationStore.LoadAnnotations(request.StateDirectory);
                if (loaded.IsOk && loaded.Value.Count == 0)
                    loadedEmpty = true;

                return loaded;
            },
            WriteClipboard = text => PaneClipboard.Write(text, request.ClipboardWriter, request.Osc52Emitter),
            SaveArchive = archive => AnnotationStore.AppendArchivedSet(request.StateDirectory, archive),
            RemoveActive = ids => AnnotationStore.RemoveAnnotationsById(request.StateDirectory, ids),
            CreateArchiveId = request.NewArchiveId,
            NowIso = request.NowIso,
        });

        return Result<CopyArchiveReport, string>.Ok(MapReport(outcome, loadedEmpty));
    }

    internal static CopyArchiveReport MapReport(CopyAndArchiveOutcome outcome, bool loadedEmpty) =>
        outcome.Kind switch
        {
            CopyAndArchiveKind.Close => new CopyArchiveReport
            {
                Line = FormatArchivedLine(outcome.ArchivedCount),
                Title = "Annotations copied and archived",
                Body = FormatArchivedLine(outcome.ArchivedCount),
                ExitCode = 0,
            },
            CopyAndArchiveKind.ArchivedActiveRetained => new CopyArchiveReport
            {
                Line = $"Copied and archived, but active annotations remain: {outcome.Message}",
                Title = "Copy and archive incomplete",
                Body = $"Copied and archived, but active annotations remain: {outcome.Message}",
                ExitCode = 1,
                IsError = true,
            },
            CopyAndArchiveKind.StayOpen when loadedEmpty => new CopyArchiveReport
            {
                Line = EmptyMessage,
                Title = "No annotations",
                Body = EmptyMessage,
                ExitCode = 0,
            },
            CopyAndArchiveKind.StayOpen => new CopyArchiveReport
            {
                Line = outcome.Message ?? "Copy and archive failed.",
                Title = "Copy and archive failed",
                Body = outcome.Message ?? "Copy and archive failed.",
                ExitCode = 1,
                IsError = true,
            },
            _ => new CopyArchiveReport
            {
                Line = "Copy and archive failed.",
                Title = "Copy and archive failed",
                Body = "Copy and archive failed.",
                ExitCode = 1,
                IsError = true,
            },
        };

    private static string FormatArchivedLine(int archivedCount) =>
        $"{archivedCount} annotation{(archivedCount == 1 ? "" : "s")} copied as Markdown and archived.";
}

public sealed record CopyArchiveRequest
{
    public required string StateDirectory { get; init; }

    public required IClipboardWriter ClipboardWriter { get; init; }

    public required IOsc52Emitter Osc52Emitter { get; init; }

    public Func<string> NewArchiveId { get; init; } = () => Guid.NewGuid().ToString();

    public Func<string> NowIso { get; init; } = AnnotateTimestamps.NowIso;
}

public sealed record CopyArchiveReport
{
    public required string Line { get; init; }

    public string Title { get; init; } = "";

    public string Body { get; init; } = "";

    public int ExitCode { get; init; }

    public bool IsError { get; init; }
}
