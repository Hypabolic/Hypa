using System.CommandLine;
using Hypa.Cli.Completion;

namespace Hypa.Cli.Commands;

public sealed class CompletionCommand
{
    public Command Build()
    {
        var cmd = new Command("completion", "Generate shell completion scripts.");
        cmd.Aliases.Add("completions");
        var shellArg = new Argument<string>("shell")
        {
            Description = "Shell to generate completions for.",
        };
        cmd.Add(shellArg);
        cmd.SetAction((parseResult, _) =>
        {
            var shell = parseResult.GetValue(shellArg)!;
            if (!CompletionShell.TryParse(shell, out var normalized))
            {
                Console.Error.WriteLine($"unknown shell: {shell}");
                Console.Error.WriteLine($"usage: hypa completion <{CompletionShell.Usage()}>");
                return Task.FromResult(2);
            }

            if (parseResult.RootCommandResult.Command is not RootCommand root)
                throw new InvalidOperationException("Completion requires a root command.");

            Console.Write(CompletionScriptGenerator.Generate(root, normalized));
            return Task.FromResult(0);
        });
        return cmd;
    }
}
