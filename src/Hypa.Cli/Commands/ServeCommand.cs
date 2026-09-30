using System.CommandLine;
using Hypa.Infrastructure.DI;
using Hypa.Infrastructure.Mcp;
using Hypa.Infrastructure.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hypa.Cli.Commands;

public sealed class ServeCommand
{
    public Command Build()
    {
        var cmd = new Command("serve", "Start the MCP stdio server (JSON-RPC 2.0).");

        var readOnlyOpt = new Option<bool>("--read-only") { Description = "Disable mutating tools and actions." };
        var toolOpt = new Option<string[]?>("--tool")
        {
            Description = "Restrict to specific tool names.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore
        };

        cmd.Add(readOnlyOpt);
        cmd.Add(toolOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var readOnly = parseResult.GetValue(readOnlyOpt);
            var toolFilter = parseResult.GetValue(toolOpt);
            var options = new McpRuntimeOptions
            {
                ReadOnly = readOnly,
                ToolFilter = toolFilter is { Length: > 0 } ? toolFilter : null
            };

            var builder = Host.CreateApplicationBuilder();

            // Route all logs to stderr — stdout must stay clean for the MCP JSON-RPC stream
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

            builder.Services.AddInfrastructure();

            // Override the McpRuntimeOptions registration with pre-configured values
            builder.Services.AddSingleton(options);

            var mcpBuilder = builder.Services.AddMcpServer()
                .WithStdioServerTransport();

            // Apply tool filter at registration time (affects tools/list and tools/call)
            var filter = options.ToolFilter;
            if (filter is null || filter.Contains("hypa_session"))
                mcpBuilder.WithTools<HypaSessionTool>();
            if (filter is null || filter.Contains("hypa_shell"))
                mcpBuilder.WithTools<HypaShellTool>();
            if (filter is null || filter.Contains("hypa_read"))
                mcpBuilder.WithTools<HypaReadTool>();
            if (filter is null || filter.Contains("hypa_search"))
                mcpBuilder.WithTools<HypaSearchTool>();
            if (filter is null || filter.Contains("hypa_code"))
                mcpBuilder.WithTools<HypaCodeTool>();
            if (filter is null || filter.Contains("hypa_compress"))
                mcpBuilder.WithTools<HypaCompressTool>();
            if (filter is null || filter.Contains("hypa_mcp"))
                mcpBuilder.WithTools<HypaMcpTool>();

            await builder.Build().RunAsync(ct);

            return 0;
        });

        return cmd;
    }
}
