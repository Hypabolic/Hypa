using System.CommandLine;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentServer;
using Hypa.Cli;
using Hypa.Cli.DI;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Infrastructure.DI;
using Hypa.Infrastructure.ProjectRoot;
using Hypa.Infrastructure.Runner;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// --default-config and --skill print and exit before the root parser.
// They do not open a Continuity store. A removed root is a parse error.
if (HasDefaultConfigFlag(args))
{
    Console.Write(new FileAttachConfigLoader().DefaultToml());
    return 0;
}

if (HasSkillFlag(args))
{
    RuntimeSkillPrinter.Write(Console.Out);
    return 0;
}

// OpenSSL 3 is a Linux host requirement for TLS and QUIC.
// ping, mux serve, compression, --help, and --version do not use it.
if (LinuxOpenSsl3Requirement.TryReject(args, Console.Error))
    return 1;

if (RemoteClientBridgeCommand.IsRemoteClientBridge(args))
    return await RemoteClientBridgeCommand.RunAsync(args);

if (MuxInvocation.IsMuxServe(args))
{
    var muxExec = MuxInvocation.ExecLeanMuxIfProductHypa(args);
    if (muxExec is int muxCode)
        return muxCode;
    return await AgentRuntimeHostBuilder.Run(MuxInvocation.ForwardServeArgs(args));
}

if (MuxInvocation.IsAttach(args))
{
    var execCode = MuxInvocation.ExecLeanAttachIfProductHypa(args);
    if (execCode is int code)
        return code;
    return await AttachHostEntry.Run(args);
}

if (ControlPlaneCliCommands.IsClientCommand(args))
{
    if (IsAgentAttachArgv(args))
    {
        var services = AttachHostEntry.CreateServices();
        await using var provider = services.BuildServiceProvider();
        var attach = provider.GetRequiredService<MuxAttachService>();
        return await ControlPlaneCliCommands.Run(args, attachConfig: null, liveAttach: attach);
    }

    return await ControlPlaneCliCommands.Run(args);
}

if (IsCodeParseWorker(args))
    return await BuildParseWorkerRoot().Parse(args).InvokeAsync();

if (IsCodeIndexExport(args))
    return await BuildExportRoot(args).Parse(args).InvokeAsync();

// When the working directory is gone, the host's default content-root probe would throw.
// Seed a fallback below the DOTNET_ environment-variable precedence instead of overriding it.
var builder = HostWithoutFileWatch.CreateApplicationBuilder(
    fallbackContentRoot: CurrentDirectory.TryGet() is null ? AppContext.BaseDirectory : null);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
builder.Services.AddInfrastructure();
builder.Services.AddCli();
var host = builder.Build();

var rootCommand = host.Services.GetRequiredService<RootCommand>();
return await rootCommand.Parse(args).InvokeAsync();

static bool HasDefaultConfigFlag(string[] args)
{
    foreach (var arg in args)
    {
        if (arg == "--default-config")
            return true;
    }

    return false;
}

static bool HasSkillFlag(string[] args)
{
    foreach (var arg in args)
    {
        if (arg == "--skill")
            return true;
    }

    return false;
}

/// <summary>
/// terminal attach. Do not treat this as a one-shot RPC verb.
/// </summary>
static bool IsAgentAttachArgv(string[] args)
{
    var i = 0;
    while (i < args.Length)
    {
        if (args[i] is "--session" or "--socket" or "--timeout-ms")
        {
            i += i + 1 < args.Length ? 2 : 1;
            continue;
        }

        break;
    }

    return i + 1 < args.Length
        && args[i] == "agent"
        && args[i + 1] == "attach";
}

static bool IsCodeIndexExport(string[] args) =>
    args.Length >= 3
    && args[0] == "code"
    && args[1] == "index"
    && args.Contains("--emit", StringComparer.Ordinal);

static bool IsCodeParseWorker(string[] args) =>
    args.Length >= 2
    && args[0] == "code"
    && args[1] == "parse-worker";

/// <summary>
/// Lean DI for isolation workers: providers only. Never register
/// <see cref="INativeParseHost"/> / <see cref="ProcessNativeParseHost"/>.
/// </summary>
static RootCommand BuildParseWorkerRoot()
{
    var services = new ServiceCollection();
    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    services.AddSingleton<ICodeStructureProvider, TreeSitterCodeStructureProvider>();
    services.AddSingleton<ICodeStructureProvider, MarkdownStructureProvider>();
    services.AddSingleton<ICodeStructureProvider, RegexFallbackCodeStructureProvider>();
    services.AddSingleton<CodeStructureProviderRegistry>();
    services.AddSingleton<CodeParseWorkerCommand>();

    var provider = services.BuildServiceProvider();
    var root = new RootCommand("hypa — workspace mux and context optimisation for AI harnesses.");
    var code = new Command("code", "Index and query source code structure.");
    code.Add(provider.GetRequiredService<CodeParseWorkerCommand>().Build());
    root.Add(code);
    return root;
}

static RootCommand BuildExportRoot(string[] args)
{
    var services = new ServiceCollection();
    var dbPath = OptionValue(args, "--db");
    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    services.AddSingleton<IProjectRootDetector, GitProjectRootDetector>();
    services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
    services.AddSingleton<INativeParseHost, ProcessNativeParseHost>();
    services.AddSingleton<HypaDataOptions>(_ => new HypaDataOptions
    {
        DatabasePathOverride = string.IsNullOrWhiteSpace(dbPath) ? null : Path.GetFullPath(dbPath),
    });
    services.AddSingleton<SqliteSchemaInitializer>();
    services.AddSingleton<ICodeIndexRepository, SqliteCodeIndexRepository>();
    services.AddSingleton<IGitFileStateProvider, GitFileStateProvider>();
    services.AddSingleton<ICodeStructureProvider, TreeSitterCodeStructureProvider>();
    services.AddSingleton<ICodeStructureProvider, MarkdownStructureProvider>();
    services.AddSingleton<ICodeStructureProvider, RegexFallbackCodeStructureProvider>();
    services.AddSingleton<CodeStructureProviderRegistry>();
    services.AddSingleton<CodeIndexService>();
    services.AddSingleton<IndexArtifactExportService>();
    services.AddSingleton<CodeQueryService>();
    services.AddSingleton<CodeDiagnosticsService>();
    services.AddSingleton<CodeCommand>();

    var provider = services.BuildServiceProvider();
    var root = new RootCommand("hypa — workspace mux and context optimisation for AI harnesses.");
    root.Add(provider.GetRequiredService<CodeCommand>().Build());
    return root;
}

static string? OptionValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
            return args[i + 1];
    }

    return null;
}
