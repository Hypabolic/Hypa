using System.CommandLine;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Cli.Commands;

public sealed class ArtifactsCommand(ArtifactService artifactService, SessionService sessionService)
{
    public Command Build()
    {
        var cmd = new Command("artifacts", "Manage session artifacts.");
        cmd.Add(BuildList());
        return cmd;
    }

    private Command BuildList()
    {
        var cmd = new Command("list", "List artifacts for the current session.");
        cmd.SetAction(async (parseResult, ct) =>
        {
            var resolve = await sessionService.StatusAsync(
                new SessionResolveOptions { ProjectRoot = Directory.GetCurrentDirectory(), CreateIfMissing = false }, ct);
            if (!resolve.IsOk)
            {
                Console.Error.WriteLine($"error: {resolve.Error.Message}");
                return 1;
            }
            var result = await artifactService.ListAsync(resolve.Value.Id, ct);
            if (!result.IsOk)
            {
                Console.Error.WriteLine($"error: {result.Error.Message}");
                return 1;
            }
            var artifacts = result.Value;
            if (artifacts.Count == 0)
            {
                Console.WriteLine("No artifacts.");
                return 0;
            }
            Console.WriteLine($"{"ID",-36}  {"MIME TYPE",-20}  {"SIZE",8}  CREATED");
            foreach (var a in artifacts)
                Console.WriteLine($"{a.Id,-36}  {a.MimeType,-20}  {a.SizeBytes,8}  {a.CreatedAt:O}");

            return 0;
        });
        return cmd;
    }
}
