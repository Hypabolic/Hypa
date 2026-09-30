using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Minimal popup manager that reads commands from stdin.
/// </summary>
public sealed class ConsoleAnnotateManagerSession(TextReader input, TextWriter output) : IAnnotateManagerSession
{
    public AnnotateManagerCommand? ReadCommand(AnnotateManagerState state)
    {
        Render(state, output);
        while (true)
        {
            var line = input.ReadLine();
            if (line is null)
                return new AnnotateManagerCommand { Action = AnnotateManagerAction.Quit };

            if (TryParse(line, out var command))
                return command;

            output.WriteLine("Unknown command. Use copy N, archive, delete N, restore N, delete-archive N, /quit.");
        }
    }

    internal static void Render(AnnotateManagerState state, TextWriter output)
    {
        output.WriteLine($"Active annotations ({state.Active.Count}) newest first");
        for (var index = 0; index < state.Active.Count; index++)
        {
            var annotation = state.Active[index];
            output.WriteLine($"{index + 1}. {AnnotateTerminalText.ClipForList(annotation.SelectedText)}");
        }

        if (state.Active.Count == 0)
            output.WriteLine("No active annotations.");

        output.WriteLine($"Archived sets ({state.Archives.Count}) newest first");
        for (var index = 0; index < state.Archives.Count; index++)
        {
            var archive = state.Archives[index];
            output.WriteLine($"{index + 1}. {archive.ArchivedAt} · {archive.Annotations.Count} annotation(s)");
        }

        if (state.Archives.Count == 0)
            output.WriteLine("No archived sets.");

        output.WriteLine("Commands: copy N, archive, delete N, restore N, delete-archive N, /quit");
    }

    private static bool TryParse(string line, out AnnotateManagerCommand command)
    {
        command = default!;
        var trimmed = line.Trim();
        if (string.Equals(trimmed, "/quit", StringComparison.Ordinal)
            || string.Equals(trimmed, "quit", StringComparison.Ordinal)
            || string.Equals(trimmed, "q", StringComparison.Ordinal))
        {
            command = new AnnotateManagerCommand { Action = AnnotateManagerAction.Quit };
            return true;
        }

        if (TryParseIndexed(trimmed, "copy", AnnotateManagerAction.CopyOne, out command))
            return true;
        if (string.Equals(trimmed, "archive", StringComparison.Ordinal))
        {
            command = new AnnotateManagerCommand { Action = AnnotateManagerAction.ArchiveAll };
            return true;
        }

        if (TryParseIndexed(trimmed, "delete", AnnotateManagerAction.DeleteActive, out command))
            return true;
        if (TryParseIndexed(trimmed, "restore", AnnotateManagerAction.RestoreArchive, out command))
            return true;
        if (TryParseIndexed(trimmed, "delete-archive", AnnotateManagerAction.DeleteArchive, out command))
            return true;

        return false;
    }

    private static bool TryParseIndexed(
        string line,
        string verb,
        AnnotateManagerAction action,
        out AnnotateManagerCommand command)
    {
        command = default!;
        if (!line.StartsWith(verb + ' ', StringComparison.Ordinal))
            return false;

        var indexText = line[(verb.Length + 1)..].Trim();
        if (!int.TryParse(indexText, out var index) || index < 1)
            return false;

        command = new AnnotateManagerCommand { Action = action, Index = index };
        return true;
    }
}
