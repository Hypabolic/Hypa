using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Application;
using Hypa.Placement.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Cli.Mux;

/// <summary>
// / Thin attach composition root.
/// client argv role without the server graph. Do not call AddInfrastructure
/// or AddCli. Keep -c, doctor, code, and MCP on the generic host.
/// </summary>
internal static class AttachHostEntry
{
    public static async Task<int> Run(string[] args)
    {
        if (HasHelp(args))
        {
            Console.WriteLine(
                "Usage: hypa attach [--session NAME] [--cwd DIR] [--once] [--remote TARGET]");
            Console.WriteLine(
                "       [--remote-keybindings local|server] [--handoff] [--connect-placement ID]");
            return 0;
        }

        if (!RemoteAttachArgs.TryParse(args, out var remote, out var remoteError))
        {
            Console.Error.WriteLine("hypa: " + remoteError);
            return 2;
        }

        Parse(args, out var session, out var cwd, out var once, out var sessionSet, out var connectPlacement);
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var attach = provider.GetRequiredService<MuxAttachService>();
        return await attach
            .AttachAsync(
                session,
                cwd,
                once,
                socketOverride: null,
                sessionSet,
                CancellationToken.None,
                remote: remote,
                connectPlacementId: connectPlacement)
            .ConfigureAwait(false);
    }

    internal static ServiceCollection CreateServices(Func<string?>? loginHome = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ProcessOperatorPaths.Resolve(loginHome));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(MuxReleaseCapability.Product);
        services.AddSingleton<IMuxSupervisor, ProcessMuxSupervisor>();
        services.AddSingleton<IAttachReconnect, AttachReconnectService>();
        services.AddSingleton<IQuicTransportCapabilityProbe>(_ =>
            new CachingQuicTransportCapabilityProbe(new QuicTransportCapabilityProbe()));
        services.AddSingleton<ICubesConnectDirectMaterialSource>(sp =>
            new PairingCubesConnectDirectMaterialSource(
                sp.GetRequiredService<ProcessOperatorPaths>().TryCreatePairing()));
        services.AddSingleton<ICubesConnectEndpointResolver>(sp =>
            new DirectoryCubesConnectEndpointResolver(
                directory: null,
                directoryFactory: () => sp.GetRequiredService<ProcessOperatorPaths>().OpenPlacementDirectory(),
                directMaterial: sp.GetRequiredService<ICubesConnectDirectMaterialSource>(),
                quicProbe: sp.GetRequiredService<IQuicTransportCapabilityProbe>(),
                remoteMux: sp.GetRequiredService<IRemoteMuxPath>()));
        services.AddSingleton<ICubesConnectRetargeter, CubesConnectRetargetService>();
        services.AddSingleton<ISidebarCubeCatalogSource, EnvironmentSidebarCubeCatalogSource>();
        services.AddSingleton<IHostReachCatalog, NetworkInterfaceHostReachCatalog>();
        services.AddSingleton<IMuxAttachDriver, AttachSession>();
        services.AddSingleton<RemoteConnectionFence>();
        services.AddSingleton<IOpenSshProcess, ProcessOpenSshProcess>();
        services.AddSingleton<IRemoteMuxPath, OpenSshRemoteMuxAdapter>();
        services.AddSingleton<IAttachConfigEnvironment, SystemAttachConfigEnvironment>();
        services.AddSingleton<IAttachConfigFiles, SystemAttachConfigFiles>();
        services.AddSingleton<IAttachConfigLoader, FileAttachConfigLoader>();
        services.AddSingleton<MuxSessionCatalog>();
        services.AddSingleton<MuxStaleServerGuard>();
        services.AddSingleton<MuxAttachService>();
        return services;
    }

    internal static bool HasHelp(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg is "--help" or "-h")
                return true;
        }

        return false;
    }

    internal static void Parse(
        string[] args,
        out string? session,
        out string? cwd,
        out bool once,
        out bool sessionSet,
        out string? connectPlacement)
    {
        session = null;
        cwd = null;
        once = false;
        sessionSet = false;
        connectPlacement = null;
        var start = 0;
        if (args.Length > 0 && args[0] == "attach")
            start = 1;
        for (var i = start; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session" when i + 1 < args.Length:
                    session = args[++i];
                    sessionSet = true;
                    break;
                case "--cwd" when i + 1 < args.Length:
                    cwd = args[++i];
                    break;
                case "--once":
                    once = true;
                    break;
                case "--connect-placement" when i + 1 < args.Length:
                    connectPlacement = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("--session=", StringComparison.Ordinal))
                    {
                        session = args[i]["--session=".Length..];
                        sessionSet = true;
                    }
                    else if (args[i].StartsWith("--cwd=", StringComparison.Ordinal))
                        cwd = args[i]["--cwd=".Length..];
                    else if (args[i].StartsWith("--connect-placement=", StringComparison.Ordinal))
                        connectPlacement = args[i]["--connect-placement=".Length..];
                    break;
            }
        }
    }
}
