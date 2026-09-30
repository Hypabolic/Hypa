using System.CommandLine;
using System.CommandLine.Completions;

namespace Hypa.Cli.Completion;

internal sealed record CommandTreeSnapshot(
    string AppName,
    IReadOnlyList<CommandNode> RootCommands,
    IReadOnlyList<OptionNode> RootOptions);

internal sealed record CommandNode(
    string Name,
    IReadOnlyList<string> Names,
    string? Description,
    IReadOnlyList<OptionNode> Options,
    IReadOnlyList<CommandNode> Subcommands);

internal sealed record OptionNode(
    IReadOnlyList<string> Names,
    string? Description,
    bool TakesValue,
    IReadOnlyList<string> Choices);

internal static class CommandTreeSnapshotBuilder
{
    internal static CommandTreeSnapshot FromRoot(RootCommand root, string appName = "hypa")
    {
        var rootOptions = CollectOptions(root);
        var rootCommands = root.Subcommands
            .Where(static command => !command.Hidden)
            .Select(CollectCommand)
            .ToArray();
        return new CommandTreeSnapshot(appName, rootCommands, rootOptions);
    }

    private static CommandNode CollectCommand(Command command)
    {
        var names = AllNames(command.Name, command.Aliases);
        var options = CollectOptions(command);
        var subcommands = command.Subcommands
            .Where(static subcommand => !subcommand.Hidden)
            .Select(CollectCommand)
            .ToArray();
        return new CommandNode(command.Name, names, command.Description, options, subcommands);
    }

    private static OptionNode[] CollectOptions(Command command)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<OptionNode>();
        foreach (var option in command.Options)
        {
            if (option.Hidden)
                continue;

            var primary = option.Name;
            if (!seen.Add(primary))
                continue;

            options.Add(new OptionNode(
                AllNames(primary, option.Aliases),
                option.Description,
                OptionTakesValue(option),
                CollectChoices(option)));
        }

        return options.ToArray();
    }

    private static bool OptionTakesValue(Option option)
    {
        if (option is Option<bool>)
            return false;

        return option.Arity.MinimumNumberOfValues > 0
            || option.Arity.MaximumNumberOfValues > 0;
    }

    private static string[] CollectChoices(Option option)
    {
        foreach (var source in option.CompletionSources)
        {
            var items = source(CompletionContext.Empty);
            var labels = items.Select(static item => item.Label).Where(static label => label.Length > 0).ToArray();
            if (labels.Length > 0)
                return labels;
        }

        return [];
    }

    private static string[] AllNames(string primary, IEnumerable<string> aliases) =>
        aliases.Prepend(primary).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
