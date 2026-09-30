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
        var inputArg = new Argument<string>("command") { Description = "The command string to rewrite." };
        var jsonOpt = new Option<bool>("--json") { Description = "Output the result as JSON." };
        cmd.Add(inputArg);
        cmd.Add(jsonOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var json = parseResult.GetValue(jsonOpt);
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

            return decision.Outcome switch
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
