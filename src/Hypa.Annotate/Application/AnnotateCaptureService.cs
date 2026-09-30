using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Capture dispatch.
/// Does not read <c>selected_text</c> from invocation context.
/// </summary>
public static class AnnotateCaptureService
{
    public const string BlankSelectionMessage = "Nothing to annotate.";

    public const string BlankSelectionTitle = "Nothing to annotate";

    public const string BlankSelectionBody = "Select text in the pane or copy text to the clipboard.";

    public static async Task<Result<AnnotateCaptureCompletion, string>> RunAsync(
        AnnotateCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = CaptureContextParser.Parse(request.ContextJson);
        var selectedText = request.HandoffText;
        if (string.IsNullOrEmpty(selectedText))
            selectedText = request.ClipboardReader.ReadText();

        if (AnnotationParser.JavascriptTrim(selectedText).Length == 0)
            return Result<AnnotateCaptureCompletion, string>.Ok(AnnotateCaptureCompletion.BlankSelection);

        var pending = new PendingAnnotation
        {
            SelectedText = selectedText,
            Context = context,
            CapturedAt = request.NowIso(),
        };

        var pendingPath = PendingAnnotationFiles.WritePending(request.StateDirectory, pending);
        if (!pendingPath.IsOk)
            return Result<AnnotateCaptureCompletion, string>.Fail(pendingPath.Error);

        var keepPending = false;
        try
        {
            var opened = await request.PaneOpener.OpenEditorPopupAsync(
                new PluginPaneOpenRequest
                {
                    PluginRoot = request.PluginRoot,
                    TargetPaneId = request.TargetPaneId,
                    PendingFilePath = pendingPath.Value,
                },
                cancellationToken).ConfigureAwait(false);
            if (!opened.IsOk)
                return Result<AnnotateCaptureCompletion, string>.Fail(opened.Error);

            keepPending = true;
            return Result<AnnotateCaptureCompletion, string>.Ok(AnnotateCaptureCompletion.EditorOpened);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Result<AnnotateCaptureCompletion, string>.Fail(error.Message);
        }
        finally
        {
            if (!keepPending)
                _ = PendingAnnotationFiles.RemovePending(pendingPath.Value);
        }
    }
}

public enum AnnotateCaptureCompletion
{
    BlankSelection,
    EditorOpened,
}

public sealed record AnnotateCaptureRequest
{
    public required string StateDirectory { get; init; }

    public required string PluginRoot { get; init; }

    public required string TargetPaneId { get; init; }

    public string? ContextJson { get; init; }

    public string? HandoffText { get; init; }

    public required IClipboardReader ClipboardReader { get; init; }

    public required IPluginPaneOpener PaneOpener { get; init; }

    public Func<string> NowIso { get; init; } = AnnotateTimestamps.NowIso;
}
