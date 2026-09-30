using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Manager pane list and store actions.
/// </summary>
public static class AnnotateManagerService
{
    public const string EmptyStoreMessage = "No annotations.";

    public static Result<int, string> Run(AnnotateManagerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = LoadState(request.StateDirectory);
        if (!loaded.IsOk)
            return Result<int, string>.Fail(loaded.Error);

        if (loaded.Value.Active.Count == 0 && loaded.Value.Archives.Count == 0)
        {
            request.Output.WriteLine(EmptyStoreMessage);
            return Result<int, string>.Ok(0);
        }

        var state = loaded.Value;
        while (true)
        {
            var command = request.ManagerSession.ReadCommand(state);
            if (command is null || command.Action == AnnotateManagerAction.Quit)
                return Result<int, string>.Ok(0);

            var handled = HandleCommand(request, state, command);
            if (!handled.IsOk)
                return Result<int, string>.Fail(handled.Error);

            if (handled.Value.Exit)
                return Result<int, string>.Ok(0);

            if (!string.IsNullOrEmpty(handled.Value.Message))
                request.Output.WriteLine(handled.Value.Message);

            var reloaded = LoadState(request.StateDirectory);
            if (!reloaded.IsOk)
                return Result<int, string>.Fail(reloaded.Error);

            state = reloaded.Value;
        }
    }

    private static Result<AnnotateManagerHandleResult, string> HandleCommand(
        AnnotateManagerRequest request,
        AnnotateManagerState state,
        AnnotateManagerCommand command)
    {
        return command.Action switch
        {
            AnnotateManagerAction.CopyOne => HandleCopyOne(request, state, command.Index),
            AnnotateManagerAction.ArchiveAll => HandleArchiveAll(request),
            AnnotateManagerAction.DeleteActive => HandleDeleteActive(request, state, command.Index),
            AnnotateManagerAction.RestoreArchive => HandleRestoreArchive(request, state, command.Index),
            AnnotateManagerAction.DeleteArchive => HandleDeleteArchive(request, state, command.Index),
            AnnotateManagerAction.Quit => Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult
            {
                Exit = true,
            }),
            _ => Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult()),
        };
    }

    private static Result<AnnotateManagerHandleResult, string> HandleCopyOne(
        AnnotateManagerRequest request,
        AnnotateManagerState state,
        int index)
    {
        if (index < 1 || index > state.Active.Count)
            return Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult { Message = "No annotation selected." });

        var target = state.Active[index - 1];
        var outcome = AnnotateManagerCopy.CopyAnnotations(
            [target],
            text => WriteClipboard(text, request.ClipboardWriter, request.Osc52Emitter));
        return Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult
        {
            Exit = outcome.Close,
            Message = outcome.Message,
        });
    }

    private static Result<AnnotateManagerHandleResult, string> HandleArchiveAll(AnnotateManagerRequest request)
    {
        var outcome = AnnotateArchiveWorkflow.CopyAndArchive(CreateCopyAndArchiveRequest(request));
        return MapArchiveOutcome(outcome);
    }

    private static Result<AnnotateManagerHandleResult, string> HandleDeleteActive(
        AnnotateManagerRequest request,
        AnnotateManagerState state,
        int index)
    {
        if (index < 1 || index > state.Active.Count)
            return Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult { Message = "No annotation selected." });

        var target = state.Active[index - 1];
        var removed = AnnotationStore.RemoveAnnotationsById(request.StateDirectory, [target.Id]);
        if (!removed.IsOk)
            return Result<AnnotateManagerHandleResult, string>.Fail(removed.Error);

        return Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult
        {
            Message = "Annotation deleted.",
        });
    }

    private static Result<AnnotateManagerHandleResult, string> HandleRestoreArchive(
        AnnotateManagerRequest request,
        AnnotateManagerState state,
        int index)
    {
        if (index < 1 || index > state.Archives.Count)
            return Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult { Message = "No archive selected." });

        var target = state.Archives[index - 1];
        var outcome = AnnotateArchiveWorkflow.RestoreArchivedSet(
            target,
            new RestoreArchiveRequest
            {
                MergeActive = items => AnnotationStore.MergeAnnotations(request.StateDirectory, items),
                RemoveArchive = archiveId => AnnotationStore.RemoveArchivedSet(request.StateDirectory, archiveId),
            });

        return Result<AnnotateManagerHandleResult, string>.Ok(MapRestoreOutcome(outcome));
    }

    private static Result<AnnotateManagerHandleResult, string> HandleDeleteArchive(
        AnnotateManagerRequest request,
        AnnotateManagerState state,
        int index)
    {
        if (index < 1 || index > state.Archives.Count)
            return Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult { Message = "No archive selected." });

        var target = state.Archives[index - 1];
        var removed = AnnotationStore.RemoveArchivedSet(request.StateDirectory, target.Id);
        if (!removed.IsOk)
            return Result<AnnotateManagerHandleResult, string>.Fail(removed.Error);

        return Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult
        {
            Message = "Archive permanently deleted.",
        });
    }

    private static Result<AnnotateManagerState, string> LoadState(string stateDirectory)
    {
        var active = AnnotationStore.LoadAnnotations(stateDirectory);
        if (!active.IsOk)
            return Result<AnnotateManagerState, string>.Fail(active.Error);

        var archives = AnnotationStore.LoadArchivedSets(stateDirectory);
        if (!archives.IsOk)
            return Result<AnnotateManagerState, string>.Fail(archives.Error);

        return Result<AnnotateManagerState, string>.Ok(new AnnotateManagerState
        {
            Active = AnnotationStore.NewestFirst(active.Value),
            Archives = AnnotationStore.NewestFirstArchives(archives.Value),
        });
    }

    private static CopyAndArchiveRequest CreateCopyAndArchiveRequest(AnnotateManagerRequest request) =>
        new()
        {
            LoadActive = () => AnnotationStore.LoadAnnotations(request.StateDirectory),
            WriteClipboard = text => WriteClipboard(text, request.ClipboardWriter, request.Osc52Emitter),
            SaveArchive = archive => AnnotationStore.AppendArchivedSet(request.StateDirectory, archive),
            RemoveActive = ids => AnnotationStore.RemoveAnnotationsById(request.StateDirectory, ids),
            CreateArchiveId = request.NewArchiveId,
            NowIso = request.NowIso,
        };

    private static Result<bool, string> WriteClipboard(
        string text,
        IClipboardWriter clipboardWriter,
        IOsc52Emitter osc52Emitter) =>
        PaneClipboard.Write(text, clipboardWriter, osc52Emitter);

    private static Result<AnnotateManagerHandleResult, string> MapArchiveOutcome(CopyAndArchiveOutcome outcome) =>
        outcome.Kind switch
        {
            CopyAndArchiveKind.Close => Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult
            {
                Exit = true,
            }),
            CopyAndArchiveKind.ArchivedActiveRetained => Result<AnnotateManagerHandleResult, string>.Ok(
                new AnnotateManagerHandleResult
                {
                    Message = $"Copied and archived, but active annotations remain: {outcome.Message}",
                }),
            CopyAndArchiveKind.StayOpen => Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult
            {
                Message = outcome.Message,
            }),
            _ => Result<AnnotateManagerHandleResult, string>.Ok(new AnnotateManagerHandleResult()),
        };

    private static AnnotateManagerHandleResult MapRestoreOutcome(RestoreArchivedSetOutcome outcome) =>
        outcome.Kind switch
        {
            RestoreArchivedSetKind.Restored => new AnnotateManagerHandleResult
            {
                Message = outcome.RestoredCount == 0
                    ? "Archive removed; its annotations were already active."
                    : $"{outcome.RestoredCount} annotation(s) restored.",
            },
            RestoreArchivedSetKind.RestoredArchiveRetained => new AnnotateManagerHandleResult
            {
                Message = $"Annotations restored, but the archive remains: {outcome.Message}",
            },
            RestoreArchivedSetKind.StayOpen => new AnnotateManagerHandleResult
            {
                Message = outcome.Message,
            },
            _ => new AnnotateManagerHandleResult(),
        };
}

public sealed record AnnotateManagerRequest
{
    public required string StateDirectory { get; init; }

    public required IAnnotateManagerSession ManagerSession { get; init; }

    public required IClipboardWriter ClipboardWriter { get; init; }

    public required IOsc52Emitter Osc52Emitter { get; init; }

    public required TextWriter Output { get; init; }

    public Func<string> NewArchiveId { get; init; } = () => Guid.NewGuid().ToString();

    public Func<string> NowIso { get; init; } = AnnotateTimestamps.NowIso;
}

internal sealed record AnnotateManagerHandleResult
{
    public bool Exit { get; init; }

    public string? Message { get; init; }
}
