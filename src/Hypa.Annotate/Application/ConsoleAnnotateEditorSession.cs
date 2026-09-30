using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Minimal popup editor that reads comment lines from stdin.
/// </summary>
public sealed class ConsoleAnnotateEditorSession(TextReader input, TextWriter output) : IAnnotateEditorSession
{
    public AnnotateEditorSessionResult Run(PendingAnnotation pending)
    {
        output.WriteLine("Selected text");
        output.WriteLine(pending.SelectedText);
        output.WriteLine("Comment (/save to save, /cancel to cancel)");
        var lines = new List<string>();
        while (true)
        {
            var line = input.ReadLine();
            if (line is null)
                return new AnnotateEditorSessionResult { Outcome = AnnotateEditorOutcome.Cancelled };

            if (string.Equals(line, "/cancel", StringComparison.Ordinal))
                return new AnnotateEditorSessionResult { Outcome = AnnotateEditorOutcome.Cancelled };

            if (string.Equals(line, "/save", StringComparison.Ordinal))
            {
                return new AnnotateEditorSessionResult
                {
                    Outcome = AnnotateEditorOutcome.Saved,
                    Comment = string.Join('\n', lines),
                };
            }

            lines.Add(line);
        }
    }
}
