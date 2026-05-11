using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Cli.Commands;

public sealed class RewriteCommand(CommandRewriteService rewriteService)
{
    public Command Build()
    {
        var cmd = new Command("rewrite", "Rewrite a shell command through the registry.");
        var inputArg = new Argument<string>("command", "The command string to rewrite.");
        var jsonOpt = new Option<bool>("--json", "Output the result as JSON.");
        cmd.AddArgument(inputArg);
        cmd.AddOption(jsonOpt);
        cmd.SetHandler(async context =>
        {
            var input = context.ParseResult.GetValueForArgument(inputArg);
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ct = context.GetCancellationToken();

            var decision = await rewriteService.RewriteAsync(input, ct);
            var output = decision.Command ?? input;

            if (json)
            {
                var result = new RewriteResult(input, decision.Outcome.ToString(), output);
                Console.WriteLine(JsonSerializer.Serialize(result, RewriteJsonContext.Default.RewriteResult));
            }
            else
            {
                Console.WriteLine(output);
            }

            context.ExitCode = decision.Outcome switch
            {
                RewriteOutcome.Rewritten or RewriteOutcome.GenericWrapper => 0,
                RewriteOutcome.Passthrough => 1,
                RewriteOutcome.Deny => 2,
                RewriteOutcome.Ask => 3,
                _ => 1,
            };
        });
        return cmd;
    }
}

public sealed record RewriteResult(string Input, string Outcome, string Command);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RewriteResult))]
internal sealed partial class RewriteJsonContext : JsonSerializerContext { }
