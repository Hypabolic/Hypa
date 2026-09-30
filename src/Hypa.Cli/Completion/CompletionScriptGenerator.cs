using System.CommandLine;
using System.Text;

namespace Hypa.Cli.Completion;

internal static class CompletionScriptGenerator
{
    internal static string Generate(RootCommand root, string shell)
    {
        var snapshot = CommandTreeSnapshotBuilder.FromRoot(root);
        return shell switch
        {
            "bash" => BashScriptWriter.Write(snapshot),
            "elvish" => ElvishScriptWriter.Write(snapshot),
            "fish" => FishScriptWriter.Write(snapshot),
            "powershell" => PowerShellScriptWriter.Write(snapshot),
            "zsh" => ZshScriptWriter.Write(snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unsupported shell."),
        };
    }
}

internal static class ShellScriptEscape
{
    internal static string SingleQuoted(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "''";

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    internal static string DoubleQuoted(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

internal static class CommandPath
{
    internal static string FunctionSuffix(IReadOnlyList<string> pathSegments)
    {
        if (pathSegments.Count == 0)
            return string.Empty;

        return "__" + string.Join("__", pathSegments.Select(SafeIdentifier));
    }

    internal static string SafeIdentifier(string value) =>
        value.Replace("-", "_", StringComparison.Ordinal);
}

internal static class ZshScriptWriter
{
    internal static string Write(CommandTreeSnapshot snapshot)
    {
        var writer = new StringBuilder();
        writer.AppendLine("#compdef " + snapshot.AppName);
        writer.AppendLine();
        writer.AppendLine("autoload -U is-at-least");
        writer.AppendLine();
        WriteFunction(writer, snapshot, path: []);
        writer.AppendLine("compdef _" + snapshot.AppName + " " + snapshot.AppName);
        return writer.ToString();
    }

    private static void WriteFunction(StringBuilder writer, CommandTreeSnapshot snapshot, IReadOnlyList<string> path)
    {
        var functionName = path.Count == 0
            ? "_" + snapshot.AppName
            : "_" + snapshot.AppName + CommandPath.FunctionSuffix(path);
        var options = path.Count == 0 ? snapshot.RootOptions : FindNode(snapshot, path)?.Options ?? [];
        var subcommands = path.Count == 0 ? snapshot.RootCommands : FindNode(snapshot, path)?.Subcommands ?? [];

        writer.AppendLine(functionName + "() {");
        writer.AppendLine("    typeset -A opt_args");
        writer.AppendLine("    typeset -a _arguments_options");
        writer.AppendLine("    local ret=1");
        writer.AppendLine();
        writer.AppendLine("    if is-at-least 5.2; then");
        writer.AppendLine("        _arguments_options=(-s -S -C)");
        writer.AppendLine("    else");
        writer.AppendLine("        _arguments_options=(-s -C)");
        writer.AppendLine("    fi");
        writer.AppendLine();
        writer.AppendLine("    local context curcontext=\"$curcontext\" state line");
        writer.AppendLine("    _arguments \"${_arguments_options[@]}\" : \\");

        foreach (var option in options)
        {
            writer.Append("    ");
            writer.Append(FormatOption(option));
            writer.Append(" \\\n");
        }

        if (subcommands.Count > 0)
        {
            var commandsFunction = functionName + "_commands";
            writer.AppendLine("    \":: :" + commandsFunction + "\" \\");
            writer.AppendLine("    \"*::: :->" + snapshot.AppName + "\" \\");
            writer.AppendLine("    && ret=0");
            writer.AppendLine();
            WriteCommandsFunction(writer, commandsFunction, subcommands);
            writer.AppendLine();
            writer.AppendLine("    case $state in");
            writer.AppendLine("    (" + snapshot.AppName + ")");
            writer.AppendLine("        case $line[1] in");
            foreach (var subcommand in subcommands)
            {
                foreach (var name in subcommand.Names)
                {
                    writer.AppendLine("        (" + ShellScriptEscape.SingleQuoted(name) + ")");
                    var nextPath = path.Concat([subcommand.Name]).ToArray();
                    writer.AppendLine("            _" + snapshot.AppName + CommandPath.FunctionSuffix(nextPath));
                    writer.AppendLine("            ;;");
                }
            }

            writer.AppendLine("        esac");
            writer.AppendLine("        ;;");
            writer.AppendLine("    esac");
        }
        else
        {
            writer.AppendLine("    && ret=0");
        }

        writer.AppendLine("}");
        writer.AppendLine();

        foreach (var subcommand in subcommands)
            WriteFunction(writer, snapshot, path.Concat([subcommand.Name]).ToArray());
    }

    private static void WriteCommandsFunction(StringBuilder writer, string functionName, IReadOnlyList<CommandNode> subcommands)
    {
        writer.AppendLine(functionName + "() {");
        writer.AppendLine("    local commands;");
        writer.AppendLine("    commands=(");
        foreach (var subcommand in subcommands)
        {
            var label = subcommand.Names[0];
            var description = subcommand.Description ?? string.Empty;
            writer.AppendLine("        " + ShellScriptEscape.SingleQuoted(label + ':' + description));
        }

        writer.AppendLine("    )");
        writer.AppendLine("    _describe -t commands '" + functionName + "' commands");
        writer.AppendLine("}");
    }

    private static string FormatOption(OptionNode option)
    {
        var primary = option.Names[0];
        var description = option.Description ?? string.Empty;
        var builder = new StringBuilder();
        builder.Append('\'');
        builder.Append(primary);
        if (option.TakesValue)
        {
            builder.Append("[]");
            if (option.Choices.Count > 0)
                builder.Append(":CHOICES:(").Append(string.Join(' ', option.Choices)).Append(')');
            else
                builder.Append(":ARG:");
        }

        if (description.Length > 0)
            builder.Append('[').Append(description).Append(']');

        builder.Append('\'');
        return builder.ToString();
    }

    private static CommandNode? FindNode(CommandTreeSnapshot snapshot, IReadOnlyList<string> path)
    {
        IReadOnlyList<CommandNode> current = snapshot.RootCommands;
        CommandNode? found = null;
        foreach (var segment in path)
        {
            found = current.FirstOrDefault(node => string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (found is null)
                return null;
            current = found.Subcommands;
        }

        return found;
    }
}

internal static class BashScriptWriter
{
    internal static string Write(CommandTreeSnapshot snapshot)
    {
        var writer = new StringBuilder();
        var rootFunction = "_" + snapshot.AppName;
        writer.AppendLine(rootFunction + "() {");
        writer.AppendLine("    local cur prev words cword");
        writer.AppendLine("    _init_completion || return");
        writer.AppendLine("    local cmd=\"\"");
        writer.AppendLine("    local i");
        writer.AppendLine("    for ((i = 1; i < cword; i++)); do");
        writer.AppendLine("        if [[ ${words[i]} != -* ]]; then");
        writer.AppendLine("            cmd=\"${cmd:+${cmd}__}${words[i]}\"");
        writer.AppendLine("        fi");
        writer.AppendLine("    done");
        writer.AppendLine("    case \"${cmd}\" in");
        writer.AppendLine("    \"\")");
        WriteCaseBody(writer, snapshot.RootOptions, snapshot.RootCommands, indent: "        ");
        writer.AppendLine("        ;;");
        WriteSubcommandCases(writer, snapshot, path: [], indent: "        ");
        writer.AppendLine("    esac");
        writer.AppendLine("}");
        writer.AppendLine("complete -F " + rootFunction + " " + snapshot.AppName);
        return writer.ToString();
    }

    private static void WriteSubcommandCases(StringBuilder writer, CommandTreeSnapshot snapshot, IReadOnlyList<string> path, string indent)
    {
        var node = path.Count == 0 ? null : FindNode(snapshot, path);
        var subcommands = path.Count == 0 ? snapshot.RootCommands : node?.Subcommands ?? [];
        foreach (var subcommand in subcommands)
        {
            var caseName = string.Join("__", path.Append(subcommand.Name));
            writer.AppendLine(indent + caseName + ")");
            var options = subcommand.Options;
            WriteCaseBody(writer, options, subcommand.Subcommands, indent + "    ");
            writer.AppendLine(indent + "    ;;");
            WriteSubcommandCases(writer, snapshot, path.Concat([subcommand.Name]).ToArray(), indent);
        }
    }

    private static void WriteCaseBody(
        StringBuilder writer,
        IReadOnlyList<OptionNode> options,
        IReadOnlyList<CommandNode> subcommands,
        string indent)
    {
        if (options.Count > 0)
        {
            writer.AppendLine(indent + "if [[ ${cur} == -* ]]; then");
            writer.AppendLine(indent + "    COMPREPLY=( $(compgen -W '" + string.Join(' ', options.SelectMany(static o => o.Names)) + "' -- \"$cur\") )");
            writer.AppendLine(indent + "    return");
            writer.AppendLine(indent + "fi");
        }

        if (subcommands.Count > 0)
        {
            writer.AppendLine(indent + "COMPREPLY=( $(compgen -W '" + string.Join(' ', subcommands.SelectMany(static c => c.Names)) + "' -- \"$cur\") )");
        }
    }

    private static CommandNode? FindNode(CommandTreeSnapshot snapshot, IReadOnlyList<string> path)
    {
        IReadOnlyList<CommandNode> current = snapshot.RootCommands;
        CommandNode? found = null;
        foreach (var segment in path)
        {
            found = current.FirstOrDefault(node => string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (found is null)
                return null;
            current = found.Subcommands;
        }

        return found;
    }
}

internal static class FishScriptWriter
{
    internal static string Write(CommandTreeSnapshot snapshot)
    {
        var writer = new StringBuilder();
        writer.AppendLine("complete -c " + snapshot.AppName + " -f");
        WriteOptions(writer, snapshot.AppName, snapshot.RootOptions, condition: string.Empty);
        WriteSubcommands(writer, snapshot.AppName, snapshot.RootCommands, condition: string.Empty);
        return writer.ToString();
    }

    private static void WriteSubcommands(
        StringBuilder writer,
        string appName,
        IReadOnlyList<CommandNode> subcommands,
        string condition)
    {
        foreach (var subcommand in subcommands)
        {
            foreach (var name in subcommand.Names)
            {
                writer.Append("complete -c " + appName + " -n '" + BuildNotAlreadySelected(subcommands, name) + condition + "' -a " + ShellScriptEscape.SingleQuoted(name));
                if (!string.IsNullOrEmpty(subcommand.Description))
                    writer.Append(" -d " + ShellScriptEscape.SingleQuoted(subcommand.Description));
                writer.AppendLine();
            }

            var nextCondition = condition + " && __fish_seen_subcommand_from " + string.Join(' ', subcommand.Names.Select(ShellScriptEscape.SingleQuoted));
            WriteOptions(writer, appName, subcommand.Options, nextCondition);
            WriteSubcommands(writer, appName, subcommand.Subcommands, nextCondition);
        }
    }

    private static void WriteOptions(
        StringBuilder writer,
        string appName,
        IReadOnlyList<OptionNode> options,
        string condition)
    {
        foreach (var option in options)
        {
            foreach (var name in option.Names)
            {
                var flag = FishOptionFlag(name);
                if (flag is null)
                    continue;

                writer.Append("complete -c " + appName + " " + flag);
                if (!string.IsNullOrEmpty(condition))
                    writer.Append(" -n '" + condition.TrimStart(' ', '&') + "'");
                if (!string.IsNullOrEmpty(option.Description))
                    writer.Append(" -d " + ShellScriptEscape.SingleQuoted(option.Description));
                if (option.Choices.Count > 0)
                    writer.Append(" -a " + ShellScriptEscape.SingleQuoted(string.Join(' ', option.Choices)));
                writer.AppendLine();
            }
        }
    }

    private static string? FishOptionFlag(string name)
    {
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            var longName = name[2..];
            if (longName.Length == 0)
                return null;

            return "-l " + ShellScriptEscape.SingleQuoted(longName);
        }

        if (name.StartsWith('-') && name.Length == 2)
            return "-s " + ShellScriptEscape.SingleQuoted(name[1..]);

        return null;
    }

    private static string BuildNotAlreadySelected(IReadOnlyList<CommandNode> siblings, string selectedName)
    {
        var names = siblings.SelectMany(static sibling => sibling.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return "not __fish_seen_subcommand_from " + string.Join(' ', names.Select(ShellScriptEscape.SingleQuoted));
    }
}

internal static class PowerShellScriptWriter
{
    internal static string Write(CommandTreeSnapshot snapshot)
    {
        var writer = new StringBuilder();
        writer.AppendLine("Register-ArgumentCompleter -Native -CommandName " + snapshot.AppName + " -ScriptBlock {");
        writer.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        writer.AppendLine("    $commandElements = $commandAst.CommandElements");
        writer.AppendLine("    $commandPath = @()");
        writer.AppendLine("    for ($i = 1; $i -lt $commandElements.Count; $i++) {");
        writer.AppendLine("        $token = [string]$commandElements[$i]");
        writer.AppendLine("        if ($token.StartsWith('-')) { break }");
        writer.AppendLine("        if ($token -eq $wordToComplete -and $i -eq ($commandElements.Count - 1)) { break }");
        writer.AppendLine("        $commandPath += $token");
        writer.AppendLine("    }");
        writer.AppendLine("    $node = $commandPath -join '|'");
        writer.AppendLine("    switch ($node) {");
        WriteNode(writer, snapshot.RootOptions, snapshot.RootCommands, pathKey: "''", indent: "        ");
        writer.AppendLine("    }");
        writer.AppendLine("}");
        return writer.ToString();
    }

    private static void WriteNode(
        StringBuilder writer,
        IReadOnlyList<OptionNode> options,
        IReadOnlyList<CommandNode> subcommands,
        string pathKey,
        string indent)
    {
        writer.AppendLine(indent + pathKey + " {");
        foreach (var option in options)
        {
            foreach (var name in option.Names)
            {
                writer.AppendLine(indent + "    if ($wordToComplete.StartsWith('-')) {");
                writer.AppendLine(indent + "        " + ShellScriptEscape.SingleQuoted(name));
                writer.AppendLine(indent + "    }");
            }
        }

        foreach (var subcommand in subcommands)
        {
            foreach (var name in subcommand.Names)
                writer.AppendLine(indent + "    " + ShellScriptEscape.SingleQuoted(name));
        }

        writer.AppendLine(indent + "}");
        foreach (var subcommand in subcommands)
        {
            var nextKey = pathKey == "''"
                ? ShellScriptEscape.SingleQuoted(subcommand.Name)
                : pathKey.TrimEnd('\'') + "|" + subcommand.Name + "'";
            WriteNode(writer, subcommand.Options, subcommand.Subcommands, nextKey, indent);
        }
    }
}

internal static class ElvishScriptWriter
{
    internal static string Write(CommandTreeSnapshot snapshot)
    {
        var writer = new StringBuilder();
        writer.AppendLine("use edit:completion");
        writer.AppendLine();
        writer.AppendLine("fn spaces {|n|");
        writer.AppendLine("    repeat $n ' ' | str:join ''");
        writer.AppendLine("}");
        writer.AppendLine();
        writer.AppendLine("fn cand {|text desc|");
        writer.AppendLine("    edit:complex-candidate $text &display=$text' '(spaces (- 14 (wcswidth $text)))$desc");
        writer.AppendLine("}");
        writer.AppendLine();
        writer.AppendLine("set edit:completion:arg-completer[" + snapshot.AppName + "] = {|@words|");
        writer.AppendLine("    var command = '" + snapshot.AppName + "'");
        writer.AppendLine("    for word $words[1..-1] {");
        writer.AppendLine("        if (str:has-prefix $word '-') { break }");
        writer.AppendLine("        set command = $command';'$word");
        writer.AppendLine("    }");
        writer.AppendLine("    var completions = [");
        WriteMapEntry(writer, snapshot.AppName, snapshot.RootOptions, snapshot.RootCommands, indent: "        ");
        writer.AppendLine("    ]");
        writer.AppendLine("    if (has-key $completions $command) {");
        writer.AppendLine("        $completions[$command]");
        writer.AppendLine("    }");
        writer.AppendLine("}");
        return writer.ToString();
    }

    private static void WriteMapEntry(
        StringBuilder writer,
        string commandKey,
        IReadOnlyList<OptionNode> options,
        IReadOnlyList<CommandNode> subcommands,
        string indent)
    {
        writer.AppendLine(indent + "&'" + commandKey + "'= {");
        foreach (var option in options)
        {
            foreach (var name in option.Names)
            {
                writer.AppendLine(indent + "    cand " + ShellScriptEscape.SingleQuoted(name) + " " + ShellScriptEscape.SingleQuoted(option.Description ?? string.Empty));
            }
        }

        foreach (var subcommand in subcommands)
        {
            foreach (var name in subcommand.Names)
            {
                writer.AppendLine(indent + "    cand " + ShellScriptEscape.SingleQuoted(name) + " " + ShellScriptEscape.SingleQuoted(subcommand.Description ?? string.Empty));
            }
        }

        writer.AppendLine(indent + "}");
        foreach (var subcommand in subcommands)
        {
            WriteMapEntry(
                writer,
                commandKey + ";" + subcommand.Name,
                subcommand.Options,
                subcommand.Subcommands,
                indent);
        }
    }
}
