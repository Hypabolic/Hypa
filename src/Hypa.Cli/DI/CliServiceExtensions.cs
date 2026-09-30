using System.CommandLine;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Commands;
using Hypa.Cli.Commands.Work;
using Hypa.Cli.Doctor;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Infrastructure;
using Hypa.Continuity.Application;
using Hypa.Placement;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Cli.DI;

public static class CliServiceExtensions
{
    public static IServiceCollection AddCli(this IServiceCollection services) =>
        AddCli(services, MuxReleaseCapability.Product);

    public static IServiceCollection AddCli(
        this IServiceCollection services,
        MuxReleaseCapability release)
    {
        ArgumentNullException.ThrowIfNull(release);
        services.AddSingleton(release);
        AddServices(services, release);
        AddCommands(services, release);
        return services;
    }

    private static void AddServices(IServiceCollection services, MuxReleaseCapability release)
    {
        services.AddSingleton<IPluginFiles, SystemPluginFiles>();
        services.AddSingleton<IPluginPathRoots, SystemPluginPathRoots>();
        services.AddSingleton<IPluginRegistry, FilePluginRegistry>();
        services.AddSingleton<IPluginDoctorRunner, ProcessPluginDoctorRunner>();
        services.AddSingleton<IDoctorCheckSource>(sp => new PluginDoctorCheckSource(
            sp.GetRequiredService<IPluginRegistry>(),
            sp.GetRequiredService<IPluginFiles>(),
            sp.GetRequiredService<IPluginPathRoots>(),
            sp.GetRequiredService<IPluginDoctorRunner>(),
            Environment.ProcessPath));
        services.AddSingleton<IDoctorCheck, MuxLogDoctorCheck>();
        services.AddSingleton(sp => new DoctorService(
            sp.GetServices<IDoctorCheck>(),
            sp.GetServices<IDoctorCheckSource>()));
        services.AddSingleton<ConfigService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<ArtifactService>();
        services.AddSingleton<CommandRewriteService>();
        services.AddSingleton<TrustService>();
        services.AddSingleton<ParseHealthService>();
        services.AddSingleton<CodeIndexService>();
        services.AddSingleton<IndexArtifactExportService>();
        services.AddSingleton<CodeQueryService>();
        services.AddSingleton<CodeDiagnosticsService>();
        services.AddSingleton<HookService>();
        services.AddSingleton<InitService>();
        services.AddSingleton<UninstallService>();
        services.AddSingleton<IOfficialIntegrationService>(_ => OfficialIntegrationService.CreateSystem());
        services.AddSingleton<IMuxSupervisor, ProcessMuxSupervisor>();
        services.AddSingleton<MuxSessionCatalog>();
        services.AddSingleton<IAttachReconnect, AttachReconnectService>();
        services.AddSingleton(ProcessOperatorPaths.Resolve());
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
                remoteMux: sp.GetRequiredService<Hypa.Placement.Application.IRemoteMuxPath>()));
        services.AddSingleton<ICubesConnectRetargeter, CubesConnectRetargetService>();
        services.AddSingleton<ISidebarCubeCatalogSource, EnvironmentSidebarCubeCatalogSource>();
        services.AddSingleton<IHostReachCatalog, NetworkInterfaceHostReachCatalog>();
        if (release.ContinuityEnabled)
        {
            services.AddSingleton<IPlacementHandoffRunner, PlacementHandoffRunner>();
            services.AddSingleton<ICubesConnectJoinMaterialSource, EnvironmentCubesConnectJoinMaterialSource>();
            services.AddSingleton<IMoveWorkDestExecutorFactory, JoinMaterialMoveWorkDestExecutorFactory>();
            services.AddSingleton<IMoveWorkMenu, MoveWorkMenuService>();
            services.AddSingleton<IWorkAttachClient, WorkAttachClientService>();
        }

        services.AddSingleton<IMuxAttachDriver, AttachSession>();
        services.AddSingleton<Hypa.Placement.Application.RemoteConnectionFence>();
        services.AddSingleton<Hypa.Placement.Infrastructure.IOpenSshProcess, Hypa.Placement.Infrastructure.ProcessOpenSshProcess>();
        services.AddSingleton<Hypa.Placement.Application.IRemoteMuxPath, Hypa.Placement.Infrastructure.OpenSshRemoteMuxAdapter>();
        services.AddSingleton<IAttachConfigEnvironment, SystemAttachConfigEnvironment>();
        services.AddSingleton<IAttachConfigFiles, SystemAttachConfigFiles>();
        services.AddSingleton<IAttachConfigLoader, FileAttachConfigLoader>();
        services.AddSingleton<IClientViewPreferencesStore, FileClientViewPreferencesStore>();
        services.AddSingleton<MuxAttachService>();
    }

    private static void AddCommands(IServiceCollection services, MuxReleaseCapability release)
    {
        services.AddSingleton<UpdateCommand>();
        services.AddSingleton<DoctorCommand>();
        services.AddSingleton<ConfigCommand>();
        services.AddSingleton<VersionCommand>();
        services.AddSingleton<CompletionCommand>();
        services.AddSingleton<SessionCommand>();
        services.AddSingleton<ArtifactsCommand>();
        services.AddSingleton<RewriteCommand>();
        services.AddSingleton<RunCommand>();
        services.AddSingleton<GitCommand>();
        services.AddSingleton<DotnetCommand>();
        services.AddSingleton<KubectlCommand>();
        services.AddSingleton<DockerCommand>();
        services.AddSingleton<FiltersCommand>();
        services.AddSingleton<TrustCommand>();
        services.AddSingleton<ParseHealthCommand>();
        services.AddSingleton<CodeCommand>();
        services.AddSingleton<ReadCommand>();
        services.AddSingleton<CompressCommand>();
        services.AddSingleton<SearchCommand>();
        services.AddSingleton<HookCommand>();
        services.AddSingleton<InitCommand>();
        services.AddSingleton<UninstallCommand>();
        services.AddSingleton<SkillCommand>();
        services.AddSingleton<ServeCommand>();
        services.AddSingleton<McpCommand>();
        services.AddSingleton<AttachCommand>();
        services.AddSingleton<DetachCommand>();
        services.AddSingleton<MuxCommand>();
        services.AddSingleton<MuxClientCommand>();
        services.AddSingleton<StatusCommand>();
        services.AddSingleton<ApiCommand>();
        services.AddSingleton<ConnectivityCommand>();
        services.AddSingleton(sp =>
        {
            var capability = sp.GetRequiredService<MuxReleaseCapability>();
            return new WorkCommand(
                new ProtocolLivePaneOccupantProbe(),
                destExecutor: null,
                attachClient: null,
                handoffRunner: null,
                release: capability);
        });
        if (release.ContinuityEnabled)
        {
            services.AddSingleton<ILivePaneOccupantProbe, ProtocolLivePaneOccupantProbe>();
            services.AddSingleton<IDestWorkSessionOpener, ConnectivityDestWorkSessionOpener>();
            services.AddSingleton<IDestWorkExecutor, ConnectivityDestWorkExecutor>();
            services.AddSingleton<RendezvousCommand>();
        }

        services.AddSingleton<DeviceCommand>();

        services.AddSingleton(sp =>
        {
            var root = new RootCommand(
                "hypa — workspace mux and context optimisation for AI harnesses.");
            root.Add(sp.GetRequiredService<DoctorCommand>().Build());
            root.Add(sp.GetRequiredService<UpdateCommand>().Build());
            root.Add(sp.GetRequiredService<ConfigCommand>().Build());
            root.Add(sp.GetRequiredService<VersionCommand>().Build());
            root.Add(sp.GetRequiredService<CompletionCommand>().Build());
            root.Add(sp.GetRequiredService<SessionCommand>().Build());
            root.Add(sp.GetRequiredService<ArtifactsCommand>().Build());
            root.Add(sp.GetRequiredService<RewriteCommand>().Build());
            root.Add(sp.GetRequiredService<GitCommand>().Build());
            root.Add(sp.GetRequiredService<DotnetCommand>().Build());
            root.Add(sp.GetRequiredService<KubectlCommand>().Build());
            root.Add(sp.GetRequiredService<DockerCommand>().Build());
            root.Add(sp.GetRequiredService<FiltersCommand>().Build());
            root.Add(sp.GetRequiredService<TrustCommand>().Build());
            root.Add(sp.GetRequiredService<ParseHealthCommand>().Build());
            root.Add(sp.GetRequiredService<CodeCommand>().Build());
            root.Add(sp.GetRequiredService<CodeCommand>().BuildMd());
            root.Add(sp.GetRequiredService<ReadCommand>().Build());
            root.Add(sp.GetRequiredService<CompressCommand>().Build());
            root.Add(sp.GetRequiredService<SearchCommand>().Build());
            root.Add(sp.GetRequiredService<HookCommand>().Build());
            root.Add(sp.GetRequiredService<InitCommand>().Build());
            root.Add(sp.GetRequiredService<UninstallCommand>().Build());
            root.Add(sp.GetRequiredService<SkillCommand>().Build());
            root.Add(sp.GetRequiredService<ServeCommand>().Build());
            root.Add(sp.GetRequiredService<McpCommand>().Build());
            root.Add(sp.GetRequiredService<AttachCommand>().Build());
            root.Add(sp.GetRequiredService<DetachCommand>().Build());
            root.Add(sp.GetRequiredService<MuxCommand>().Build());
            root.Add(sp.GetRequiredService<StatusCommand>().Build());
            root.Add(sp.GetRequiredService<ApiCommand>().Build());
            root.Add(sp.GetRequiredService<ConnectivityCommand>().Build());
            root.Add(sp.GetRequiredService<WorkCommand>().Build());
            root.Add(sp.GetRequiredService<DeviceCommand>().Build());
            var capability = sp.GetService<MuxReleaseCapability>() ?? MuxReleaseCapability.Product;
            if (capability.ContinuityEnabled)
            {
                root.Add(sp.GetRequiredService<RendezvousCommand>().Build());
            }

            foreach (var clientCmd in sp.GetRequiredService<MuxClientCommand>().BuildAll())
                root.Add(clientCmd);
            sp.GetRequiredService<RunCommand>().AttachTo(root);
            return root;
        });
    }
}
