using System.CommandLine;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach;

namespace Hypa.Cli.Commands;

public static class ClientActionCommand
{
    public static Command Build(IAttachConfigLoader? attachConfig)
    {
        var actionArg = new Argument<string>("action")
        {
            Description = "Action name. Use tab.focus, placement.connect, settings.open, or settings.close.",
        };
        var tabOpt = new Option<string?>("--tab")
        {
            Description = "Tab id for tab.focus.",
        };
        var placementOpt = new Option<string?>("--placement")
        {
            Description = "Placement id for placement.connect.",
        };
        var pageOpt = new Option<string?>("--page")
        {
            Description = "Settings page id for settings.open, for example integrations.",
        };
        var clientOpt = new Option<string?>("--client")
        {
            Description = "Attach client id. Required when more than one attach is running.",
        };
        var sessionOpt = new Option<string?>("--session")
        {
            Description = "Mux session name.",
        };
        var endpointOpt = new Option<string?>("--endpoint")
        {
            Description = "Expected endpoint id. The attach rejects a stale endpoint.",
        };
        var generationOpt = new Option<ulong?>("--generation")
        {
            Description = "Expected connection generation. The attach rejects a stale generation.",
        };
        var verboseOpt = new Option<bool>("--verbose")
        {
            Description = "Include the RPC document in the reply.",
        };
        var action = new Command("action", "Run one semantic action on a running attach.")
        {
            actionArg,
            tabOpt,
            placementOpt,
            pageOpt,
            clientOpt,
            sessionOpt,
            endpointOpt,
            generationOpt,
            verboseOpt,
        };
        action.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(actionArg) ?? "";
            var tabId = parseResult.GetValue(tabOpt);
            var placementId = parseResult.GetValue(placementOpt);
            if (name == AttachSemanticActions.TabFocus && string.IsNullOrWhiteSpace(tabId))
                return AttachSemanticClient.WriteUsage(Console.Out, name, "tab id is required");
            if (name == AttachSemanticActions.PlacementConnect && string.IsNullOrWhiteSpace(placementId))
                return AttachSemanticClient.WriteUsage(Console.Out, name, "placement id is required");

            if (!AttachConfigErrors.TryLoad(attachConfig, Console.Error, out var config))
                return 1;

            var sessionResult = parseResult.GetResult(sessionOpt);
            var sessionExplicit = sessionResult is { Tokens.Count: > 0 };
            var session = AttachSessionResolver.Resolve(
                sessionExplicit ? parseResult.GetValue(sessionOpt) : null,
                config);
            var request = new AttachSemanticRequest
            {
                Action = name,
                TabId = tabId,
                PlacementId = placementId,
                PageId = parseResult.GetValue(pageOpt),
                EndpointId = parseResult.GetValue(endpointOpt),
                ConnectionGeneration = parseResult.GetValue(generationOpt),
                Verbose = parseResult.GetValue(verboseOpt),
            };
            return await AttachSemanticClient.ExecuteAsync(
                    MuxSessionCatalog.AttachStateDirectory(session),
                    parseResult.GetValue(clientOpt),
                    request,
                    Console.Out,
                    ct)
                .ConfigureAwait(false);
        });

        var client = new Command("client", "Drive a running attach client.")
        {
            action,
        };
        return client;
    }
}
