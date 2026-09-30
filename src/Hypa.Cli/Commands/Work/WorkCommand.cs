using System.CommandLine;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypa.Continuity.Harnesses.Fake;
using Hypa.Continuity.Harnesses.Pi;
using Hypa.Continuity.Infrastructure;
using Hypa.Placement;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using PlacementPeerProviders = Hypa.Placement.Domain.PeerProviders;

namespace Hypa.Cli.Commands.Work;

/// <summary>Hypa work pack|apply|handoff|status|attach|placements|adopt.</summary>
public sealed class WorkCommand
{
    private readonly ILivePaneOccupantProbe _paneProbe;
    private readonly IDestWorkExecutor? _destExecutor;
    private readonly IWorkAttachClient _attachClient;
    private readonly IPlacementHandoffRunner _handoffRunner;
    private readonly MuxReleaseCapability _release;

    public WorkCommand()
        : this(new ProtocolLivePaneOccupantProbe())
    {
    }

    public WorkCommand(ILivePaneOccupantProbe paneProbe)
        : this(paneProbe, destExecutor: null)
    {
    }

    public WorkCommand(ILivePaneOccupantProbe paneProbe, IDestWorkExecutor? destExecutor)
        : this(paneProbe, destExecutor, attachClient: null)
    {
    }

    public WorkCommand(
        ILivePaneOccupantProbe paneProbe,
        IDestWorkExecutor? destExecutor,
        IWorkAttachClient? attachClient)
        : this(paneProbe, destExecutor, attachClient, handoffRunner: null)
    {
    }

    public WorkCommand(
        ILivePaneOccupantProbe paneProbe,
        IDestWorkExecutor? destExecutor,
        IWorkAttachClient? attachClient,
        IPlacementHandoffRunner? handoffRunner,
        MuxReleaseCapability? release = null)
    {
        _paneProbe = paneProbe ?? throw new ArgumentNullException(nameof(paneProbe));
        _destExecutor = destExecutor;
        _attachClient = attachClient ?? new WorkAttachClientService();
        _handoffRunner = handoffRunner ?? new PlacementHandoffRunner();
        _release = release ?? MuxReleaseCapability.Product;
    }

    public Command Build()
    {
        var cmd = new Command(
            "work",
            _release.ContinuityEnabled
                ? "Work continuity: pack, apply, handoff, status, attach, placements, adopt."
                : "Saved Placement catalog.");
        if (_release.ContinuityEnabled)
        {
            cmd.Add(BuildPack());
            cmd.Add(BuildApply());
            cmd.Add(BuildHandoff());
            cmd.Add(BuildStatus());
            cmd.Add(BuildAttach());
            cmd.Add(BuildAdopt());
        }

        cmd.Add(BuildPlacements());
        return cmd;
    }

    private static Command BuildPack()
    {
        var workIdArg = new Argument<string>("work_id");
        var socketOpt = new Option<string>("--socket") { Required = true };
        var cwdOpt = new Option<string?>("--cwd");
        var homeOpt = new Option<string?>("--home");
        var storeOpt = new Option<string?>("--store");
        var harnessOpt = new Option<string?>("--harness");

        var cmd = new Command("pack", "Write a WorkPack for work_id into the local spool.");
        cmd.Add(workIdArg);
        cmd.Add(socketOpt);
        cmd.Add(cwdOpt);
        cmd.Add(homeOpt);
        cmd.Add(storeOpt);
        cmd.Add(harnessOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var workId = WorkId.Parse(parseResult.GetValue(workIdArg)!);
            var socket = parseResult.GetValue(socketOpt)!;
            var storeDir = parseResult.GetValue(storeOpt) ?? DefaultStoreDir();
            var harnessOverride = parseResult.GetValue(harnessOpt);

            await using var store = new SqliteContinuityStore(storeDir);
            var works = new WorkService(store);
            var active = await works.GetActiveRunAsync(workId, ct);
            if (active is null)
                return WriteFail(ContinuityReasons.Internal, "work has no active Run", exit: 2);

            var cwdFlag = parseResult.GetValue(cwdOpt);
            var homeFlag = parseResult.GetValue(homeOpt);
            var cwdRaw = !string.IsNullOrWhiteSpace(cwdFlag) ? cwdFlag : active.Cwd;
            if (string.IsNullOrWhiteSpace(cwdRaw))
                return WriteFail(ContinuityReasons.Internal, "cwd is required", exit: 2);
            var cwd = Path.GetFullPath(cwdRaw);
            var homeRaw = !string.IsNullOrWhiteSpace(homeFlag)
                ? homeFlag
                : !string.IsNullOrWhiteSpace(active.Home)
                    ? active.Home
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var home = Path.GetFullPath(homeRaw);

            var work = await store.GetWorkAsync(workId, ct);
            if (work is null)
                return WriteFail(ContinuityReasons.Internal, "work not found", 2);
            if (!string.IsNullOrWhiteSpace(harnessOverride)
                && !string.Equals(work.HarnessAdapterId, harnessOverride, StringComparison.Ordinal))
            {
                return WriteFail(
                    ContinuityReasons.PackInvalid,
                    "harness.adapter_id does not match Work",
                    2);
            }

            var harness = CreateHarness(work.HarnessAdapterId);
            var versionOutcome = harness.ReadVersion(home, out var sourceVersion);
            if (!versionOutcome.Ok)
                return WriteFail(versionOutcome.Reason!, versionOutcome.Detail!, 2);
            if (string.IsNullOrWhiteSpace(sourceVersion))
            {
                return WriteFail(
                    ContinuityReasons.HarnessVersionUnreadable,
                    "source harness version empty",
                    2);
            }

            var probe = harness.ProbeConversationId(home, cwd);
            var conversationId = probe.ConversationId;
            if (!probe.Ok || string.IsNullOrWhiteSpace(conversationId))
                return WriteFail(probe.Reason ?? ContinuityReasons.Internal, probe.Detail ?? "", 2);

            var quiesce = harness.Quiesce(
                new HarnessRunContext
                {
                    Home = home,
                    Cwd = cwd,
                    ConversationId = conversationId!,
                },
                TimeSpan.FromSeconds(30));
            if (!quiesce.Ok)
                return WriteFail(quiesce.Reason!, quiesce.Detail!, 2);

            using var staging = new DisposableTemp("hypa-cli-pack-");
            var harnessDir = Path.Combine(staging.Path, "harness");
            var storeRoot = Path.Combine(harnessDir, "store");
            Directory.CreateDirectory(storeRoot);
            var capture = harness.CaptureStore(home, storeRoot, cwd, conversationId!);
            if (!capture.Ok)
                return WriteFail(capture.Reason!, capture.Detail!, 2);

            File.WriteAllText(Path.Combine(harnessDir, "adapter_id"), harness.AdapterId);
            File.WriteAllText(Path.Combine(harnessDir, "conversation_id.txt"), conversationId!);
            File.WriteAllText(Path.Combine(harnessDir, "version.txt"), sourceVersion);

            var workspaceDir = Path.Combine(staging.Path, "workspace");
            var packer = new GitWorkspacePacker();
            var ws = packer.Capture(cwd, workspaceDir);
            if (!ws.Ok)
                return WriteFail(ws.Reason!, ws.Detail!, 2);

            var wsManifest = JsonSerializer.Deserialize(
                File.ReadAllText(Path.Combine(workspaceDir, "workspace-manifest.json")),
                WorkspaceManifestJsonContext.Default.WorkspaceManifestDto)!;

            var manifest = new WorkPackManifestDto
            {
                Schema = 1,
                WorkId = workId.Value,
                Generation = active.Generation,
                CreatedAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                Harness = new WorkPackHarnessDto
                {
                    AdapterId = harness.AdapterId,
                    HarnessVersion = sourceVersion,
                    ConversationId = conversationId!,
                },
                Workspace = new WorkPackWorkspaceDto
                {
                    Head = wsManifest.Head,
                    Branch = wsManifest.Branch,
                    TrackedDiffSha256 = wsManifest.TrackedDiffSha256,
                    UntrackedSha256 = wsManifest.UntrackedSha256,
                },
                Source = new WorkPackSourceDto
                {
                    MuxEndpoint = socket.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
                        ? socket
                        : "unix:" + socket,
                    WorkspacePath = cwd,
                },
            };

            var spool = new LocalWorkPackSpool();
            var codec = new WorkPackCodec();
            var write = codec.Write(spool.SpoolDirectory, manifest, harnessDir, workspaceDir, out var packPath);
            if (!write.Ok)
                return WriteFail(write.Reason!, write.Detail!, 2);

            Console.Out.WriteLine(JsonSerializer.Serialize(new WorkPackCliOk
            {
                Packed = true,
                Pack = packPath,
                WorkId = workId.Value,
                Generation = active.Generation,
            }, ContinuityCliJsonContext.Default.WorkPackCliOk));
            return 0;
        });
        return cmd;
    }

    private static Command BuildApply()
    {
        var packOpt = new Option<string?>("--pack")
        {
            Description = "Local pack path. Omit when dest worker receives the pack over Connectivity.",
        };
        var homeOpt = new Option<string>("--home") { Required = true };
        var cwdOpt = new Option<string>("--cwd") { Required = true };
        var socketOpt = new Option<string>("--socket") { Required = true };
        var placementOpt = new Option<string>("--placement") { Required = true };
        var paneOpt = new Option<string>("--pane") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var harnessOpt = new Option<string?>("--harness");
        var attemptIdOpt = new Option<string?>("--attempt-id");
        var relayUrlOpt = new Option<string?>("--relay-url")
        {
            Description = "Outbound rendezvous URL. Dest worker joins as mux.",
        };
        var joinNonceOpt = new Option<string?>("--join-nonce");
        var joinSecretOpt = new Option<string?>("--join-secret");
        var deviceIdOpt = new Option<string?>("--device-id");
        var cmd = new Command("apply", "Destination worker. Verify pack, apply, start, probe, commit dest Run.");
        cmd.Add(packOpt);
        cmd.Add(homeOpt);
        cmd.Add(cwdOpt);
        cmd.Add(socketOpt);
        cmd.Add(placementOpt);
        cmd.Add(paneOpt);
        cmd.Add(storeOpt);
        cmd.Add(harnessOpt);
        cmd.Add(attemptIdOpt);
        cmd.Add(relayUrlOpt);
        cmd.Add(joinNonceOpt);
        cmd.Add(joinSecretOpt);
        cmd.Add(deviceIdOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var packPath = parseResult.GetValue(packOpt);
            var destHome = Path.GetFullPath(parseResult.GetValue(homeOpt)!);
            var destCwd = Path.GetFullPath(parseResult.GetValue(cwdOpt)!);
            var socket = parseResult.GetValue(socketOpt)!;
            var destEndpoint = socket.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
                ? socket
                : "unix:" + socket;
            var placementId = parseResult.GetValue(placementOpt)!;
            var paneId = parseResult.GetValue(paneOpt)!;
            var storeDir = parseResult.GetValue(storeOpt) ?? DefaultStoreDir();
            var harnessId = parseResult.GetValue(harnessOpt);
            var attemptId = parseResult.GetValue(attemptIdOpt);
            if (string.IsNullOrWhiteSpace(attemptId))
                attemptId = HandoffRelayFailure.NewAttemptId();

            await using var store = new SqliteContinuityStore(storeDir);
            var apply = new DestApplyService(
                new WorkService(store),
                new GitWorkspacePacker(),
                new WorkPackCodec());
            var destSpool = new LocalWorkPackSpool(Path.Combine(storeDir, "spool"));
            var hostHarnessId = harnessId;
            if (string.IsNullOrWhiteSpace(hostHarnessId) && !string.IsNullOrWhiteSpace(packPath))
            {
                if (!File.Exists(packPath))
                    return WriteFail(ContinuityReasons.PackInvalid, "pack is missing", 2);
                var codec = new WorkPackCodec();
                using var extract = new DisposableTemp("hypa-cli-apply-peek-");
                var peek = codec.Read(packPath, extract.Path, out var manifest);
                if (!peek.Ok || manifest is null)
                    return WriteFail(peek.Reason ?? ContinuityReasons.PackInvalid, peek.Detail ?? "pack read failed", 2);
                hostHarnessId = manifest.Harness.AdapterId;
            }

            if (string.IsNullOrWhiteSpace(hostHarnessId))
                return WriteFail(ContinuityReasons.HarnessUncertified, "harness is required", 2);

            var harness = CreateHarness(hostHarnessId!);
            IDestOccupantStarter starter = string.Equals(
                    harness.AdapterId,
                    FakeHarnessAdapter.Id,
                    StringComparison.Ordinal)
                ? new FakeDestOccupantStarter()
                : new ProtocolDestOccupantStarter();
            var host = new DestApplyHostContext
            {
                DestHome = destHome,
                DestWorkspace = destCwd,
                DestMuxEndpoint = destEndpoint,
                Harness = harness,
                DestStarter = starter,
                DestPaneId = paneId,
                RequireDestStart = true,
            };

            if (string.IsNullOrWhiteSpace(packPath))
            {
                var opener = ConnectivityDestWorkSessionOpener.Create(
                    ResolveRelayUrl(parseResult.GetValue(relayUrlOpt)),
                    placementId,
                    parseResult.GetValue(joinNonceOpt),
                    parseResult.GetValue(deviceIdOpt),
                    parseResult.GetValue(joinSecretOpt),
                    JoinRole.Mux);
                if (!opener.Ok || opener.Value is null)
                {
                    return WriteFail(
                        opener.Reason ?? ContinuityReasons.PeerUnavailable,
                        opener.Detail ?? "dest worker session is not joined",
                        2);
                }

                var opened = await opener.Value.OpenAsync(placementId, ct).ConfigureAwait(false);
                if (!opened.Ok || opened.Value is null)
                {
                    return WriteFail(
                        opened.Reason ?? ContinuityReasons.PeerUnavailable,
                        opened.Detail ?? "dest worker session is not joined",
                        2);
                }

                await using var session = opened.Value;
                var served = await new DestWorkerHost(session, apply, host, destSpool)
                    .ServeOnceAsync(ct)
                    .ConfigureAwait(false);
                if (!served.Ok || served.Value is null)
                {
                    return WriteFail(
                        served.Reason ?? ContinuityReasons.PeerUnavailable,
                        served.Detail ?? "dest worker serve failed",
                        2);
                }

                var servedResult = served.Value;
                if (!servedResult.Ok)
                {
                    return WriteFail(
                        servedResult.Reason ?? ContinuityReasons.Internal,
                        servedResult.Detail ?? "dest apply failed",
                        2);
                }

                return WriteApplyOk(servedResult);
            }

            if (!File.Exists(packPath))
                return WriteFail(ContinuityReasons.PackInvalid, "pack is missing", 2);

            var localCodec = new WorkPackCodec();
            using var localExtract = new DisposableTemp("hypa-cli-apply-peek-");
            var localPeek = localCodec.Read(packPath, localExtract.Path, out var localManifest);
            if (!localPeek.Ok || localManifest is null)
            {
                return WriteFail(
                    localPeek.Reason ?? ContinuityReasons.PackInvalid,
                    localPeek.Detail ?? "pack read failed",
                    2);
            }

            var result = await apply.ApplyPackAsync(
                packPath,
                new DestApplyRequest
                {
                    AttemptId = attemptId,
                    WorkId = localManifest.WorkId,
                    DestPlacementId = placementId,
                    DestPaneId = paneId,
                    PackSha256 = localCodec.HashPackFile(packPath),
                    HarnessAdapterId = harness.AdapterId,
                },
                host,
                ct);
            if (!result.Ok)
                return WriteFail(result.Reason ?? ContinuityReasons.Internal, result.Detail ?? "dest apply failed", 2);

            return WriteApplyOk(result);
        });
        return cmd;
    }

    private Command BuildHandoff()
    {
        var workIdArg = new Argument<string>("work_id");
        var fromOpt = new Option<string>("--from") { Required = true };
        var toOpt = new Option<string>("--to") { Required = true };
        var srcHomeOpt = new Option<string?>("--src-home");
        var destHomeOpt = new Option<string?>("--dest-home")
        {
            Description = "Deprecated. Dest worker owns dest HOME. Compatibility for socket dest.",
        };
        var srcCwdOpt = new Option<string?>("--src-cwd");
        var destCwdOpt = new Option<string?>("--dest-cwd")
        {
            Description = "Deprecated. Dest worker owns dest cwd. Compatibility for socket dest.",
        };
        var storeOpt = new Option<string?>("--store");
        var placementStoreOpt = new Option<string?>("--placement-store");
        var harnessOpt = new Option<string?>("--harness");
        var paneOpt = new Option<string?>("--pane");
        var sourcePaneOpt = new Option<string?>("--source-pane")
        {
            Description = "Best-effort pane.close on source after fence (spec §4.2).",
        };
        var startOpt = new Option<bool>("--start-dest") { DefaultValueFactory = _ => false };
        var peerSpoolOpt = new Option<string?>("--peer-spool")
        {
            Description = "Deprecated. Dest worker owns pack pull.",
        };
        var destSpoolOpt = new Option<string?>("--dest-spool")
        {
            Description = "Deprecated. Dest worker owns dest spool.",
        };
        var peerSshOpt = new Option<string?>("--peer-ssh")
        {
            Description = "Deprecated. Not the product surface. SSH may start dest worker.",
        };
        var peerRemoteSpoolOpt = new Option<string?>("--peer-remote-spool");
        var relayUrlOpt = new Option<string?>("--relay-url")
        {
            Description = "Outbound rendezvous URL. Dest worker joins as client.",
        };
        var joinNonceOpt = new Option<string?>("--join-nonce");
        var joinSecretOpt = new Option<string?>("--join-secret");
        var deviceIdOpt = new Option<string?>("--device-id");
        var attemptIdOpt = new Option<string?>("--attempt-id")
        {
            Description = "Handoff attempt id. Distinct from Work id.",
        };

        var cmd = new Command("handoff", "Handoff Work to a Placement id. Dest worker runs apply.");
        cmd.Add(workIdArg);
        cmd.Add(fromOpt);
        cmd.Add(toOpt);
        cmd.Add(srcHomeOpt);
        cmd.Add(destHomeOpt);
        cmd.Add(srcCwdOpt);
        cmd.Add(destCwdOpt);
        cmd.Add(storeOpt);
        cmd.Add(placementStoreOpt);
        cmd.Add(harnessOpt);
        cmd.Add(paneOpt);
        cmd.Add(sourcePaneOpt);
        cmd.Add(startOpt);
        cmd.Add(peerSpoolOpt);
        cmd.Add(destSpoolOpt);
        cmd.Add(peerSshOpt);
        cmd.Add(peerRemoteSpoolOpt);
        cmd.Add(relayUrlOpt);
        cmd.Add(joinNonceOpt);
        cmd.Add(joinSecretOpt);
        cmd.Add(deviceIdOpt);
        cmd.Add(attemptIdOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var workId = WorkId.Parse(parseResult.GetValue(workIdArg)!);
            var from = parseResult.GetValue(fromOpt)!;
            var to = parseResult.GetValue(toOpt)!;
            var srcEndpoint = from.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
                ? from
                : "unix:" + from;

            var storeDir = parseResult.GetValue(storeOpt) ?? DefaultStoreDir();
            await using var store = new SqliteContinuityStore(storeDir);
            var works = new WorkService(store);
            var sourceSpool = new LocalWorkPackSpool();
            var handoff = new HandoffService(
                works,
                new GitWorkspacePacker(),
                new WorkPackCodec(),
                sourceSpool);

            var srcHome = parseResult.GetValue(srcHomeOpt)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var srcCwd = parseResult.GetValue(srcCwdOpt) ?? Directory.GetCurrentDirectory();
            var startDest = parseResult.GetValue(startOpt);
            var paneId = parseResult.GetValue(paneOpt);
            var sourcePaneId = parseResult.GetValue(sourcePaneOpt);

            IDestWorkExecutor? destExecutor = null;
            string destEndpoint;
            string destPlacementId;
            string? destHome = null;
            string? destCwd = null;
            Hypa.Placement.Domain.PlacementId? productDest = null;
            IPlacementDirectory? productDirectory = null;
            if (Hypa.Placement.Domain.PlacementId.TryParse(to, out var placementId))
            {
                productDirectory = OpenDirectory(parseResult.GetValue(placementStoreOpt));
                var resolved = await new DestPlacementResolver(productDirectory)
                    .ResolveAsync(placementId, ct);
                if (!resolved.Ok || resolved.Value is null)
                {
                    return WriteFail(
                        resolved.Reason ?? ContinuityReasons.PeerUnavailable,
                        resolved.Detail ?? "placement is not in the directory",
                        2);
                }

                productDest = resolved.Value.PlacementId;
                destPlacementId = resolved.Value.PlacementId.Value;
                destEndpoint = resolved.Value.OpaqueMuxEndpoint;
                var relayUrl = parseResult.GetValue(relayUrlOpt);
                var joinNonce = parseResult.GetValue(joinNonceOpt);
                var deviceId = parseResult.GetValue(deviceIdOpt);
                var joinSecret = parseResult.GetValue(joinSecretOpt);
                var joinOpener = ConnectivityDestWorkSessionOpener.Create(
                    ResolveRelayUrl(relayUrl),
                    destPlacementId,
                    joinNonce,
                    deviceId,
                    joinSecret,
                    JoinRole.Client);
                if (joinOpener.Ok && joinOpener.Value is not null)
                {
                    destExecutor = new ConnectivityDestWorkExecutor(joinOpener.Value);
                }
                else if (HasDestWorkerJoinInput(relayUrl, joinNonce, deviceId, joinSecret)
                         || !CanReuseInjectedDestExecutor(_destExecutor))
                {
                    return WriteFail(
                        joinOpener.Reason ?? ContinuityReasons.PeerUnavailable,
                        joinOpener.Detail ?? "dest worker session is not joined",
                        2);
                }
                else
                {
                    destExecutor = _destExecutor;
                }
            }
            else
            {
                destEndpoint = NormalizeToEndpoint(to);
                destPlacementId = "dest";
                var destHomeRaw = parseResult.GetValue(destHomeOpt);
                var destCwdRaw = parseResult.GetValue(destCwdOpt);
                if (string.IsNullOrWhiteSpace(destHomeRaw) || string.IsNullOrWhiteSpace(destCwdRaw))
                {
                    return WriteFail(
                        ContinuityReasons.Internal,
                        "--dest-home and --dest-cwd are required for socket dest",
                        2);
                }

                destHome = Path.GetFullPath(destHomeRaw);
                destCwd = Path.GetFullPath(destCwdRaw);
            }

            if (destExecutor is null && startDest && string.IsNullOrWhiteSpace(paneId))
                return WriteFail(ContinuityReasons.StartFailed, "--pane is required with --start-dest", 2);

            IDestOccupantStarter? starter = startDest
                ? new ProtocolDestOccupantStarter()
                : null;
            ISourceOccupantStopper? sourceStopper = !string.IsNullOrWhiteSpace(sourcePaneId)
                ? new ProtocolSourceOccupantStopper()
                : null;

            IWorkPackPeerPull? peerPull = null;
            PeerPackSource? peerSource = null;
            IWorkPackSpool? destSpool = null;
            var peerSpool = parseResult.GetValue(peerSpoolOpt);
            var peerSsh = parseResult.GetValue(peerSshOpt);
            var peerRemote = parseResult.GetValue(peerRemoteSpoolOpt);
            var destSpoolPath = parseResult.GetValue(destSpoolOpt);

            if (destExecutor is not null)
            {
                peerPull = null;
                peerSource = null;
                destSpool = null;
            }
            else if (!string.IsNullOrWhiteSpace(peerSsh))
            {
                if (string.IsNullOrWhiteSpace(peerRemote))
                    return WriteFail(ContinuityReasons.Internal, "--peer-remote-spool required with --peer-ssh", 2);
                peerPull = new SshPeerWorkPackPull();
                peerSource = new PeerPackSource
                {
                    Kind = "ssh",
                    Host = peerSsh,
                    RemoteSpoolDirectory = peerRemote!,
                };
                destSpool = new LocalWorkPackSpool(
                    destSpoolPath ?? Path.Combine(DefaultStoreDir(), "spool-dest"));
            }
            else if (!string.IsNullOrWhiteSpace(peerSpool))
            {
                peerPull = new LocalPathPeerWorkPackPull();
                peerSource = new PeerPackSource
                {
                    Kind = "local-path",
                    RemoteSpoolDirectory = Path.GetFullPath(peerSpool!),
                };
                destSpool = new LocalWorkPackSpool(
                    destSpoolPath ?? Path.Combine(DefaultStoreDir(), "spool-dest"));
            }

            IHandoffTransport? transport = null;
            var relayUrlRaw = parseResult.GetValue(relayUrlOpt);
            if (destExecutor is null && !string.IsNullOrWhiteSpace(relayUrlRaw))
            {
                var parsedUrl = RendezvousUrl.ParseOutcome(relayUrlRaw);
                if (!parsedUrl.Ok)
                {
                    return WriteFail(
                        parsedUrl.Reason ?? ConnectivityReasons.RendezvousUrlInvalid,
                        parsedUrl.Detail ?? "relay url invalid",
                        2);
                }

                transport = new RelayDestConfirmationTransport(parsedUrl.Value, destPlacement: "plc_handoff");
            }

            var attemptId = parseResult.GetValue(attemptIdOpt);
            if (string.IsNullOrWhiteSpace(attemptId))
                attemptId = HandoffRelayFailure.NewAttemptId();

            var harnessFlag = parseResult.GetValue(harnessOpt);
            if (string.IsNullOrWhiteSpace(harnessFlag))
            {
                var work = await store.GetWorkAsync(workId, ct);
                harnessFlag = work?.HarnessAdapterId;
            }

            if (string.IsNullOrWhiteSpace(harnessFlag))
                return WriteFail(ContinuityReasons.HarnessUncertified, "harness is required", 2);

            HandoffResult result;
            if (productDest is { } destId && destExecutor is not null && productDirectory is not null)
            {
                result = await _handoffRunner.RunAsync(
                    new PlacementHandoffRequest
                    {
                        WorkId = workId,
                        Harness = CreateHarness(harnessFlag!),
                        SourceHome = Path.GetFullPath(srcHome),
                        SourceWorkspace = Path.GetFullPath(srcCwd),
                        SourceMuxEndpoint = srcEndpoint,
                        DestPlacementId = destId,
                        DestExecutor = destExecutor,
                        Handoff = handoff,
                        Directory = productDirectory,
                        SourcePaneId = sourcePaneId,
                        SourceStopper = sourceStopper,
                        RequireDestStart = startDest,
                        AttemptId = attemptId,
                    },
                    ct);
            }
            else
            {
                result = await handoff.RunAsync(
                    new HandoffRequest
                    {
                        WorkId = workId,
                        Harness = CreateHarness(harnessFlag!),
                        SourceHome = Path.GetFullPath(srcHome),
                        SourceWorkspace = Path.GetFullPath(srcCwd),
                        SourceMuxEndpoint = srcEndpoint,
                        SourcePlacementId = "src",
                        DestHome = destHome ?? "",
                        DestWorkspace = destCwd ?? "",
                        DestMuxEndpoint = destEndpoint,
                        DestPlacementId = destPlacementId,
                        DestPaneId = paneId,
                        DestStarter = destExecutor is null ? starter : null,
                        DestExecutor = destExecutor,
                        SourcePaneId = sourcePaneId,
                        SourceStopper = sourceStopper,
                        RequireDestStart = startDest,
                        PeerPull = peerPull,
                        PeerSource = peerSource,
                        DestSpool = destSpool,
                        Transport = transport,
                        AttemptId = attemptId,
                    },
                    ct);
            }

            if (!result.Ok)
                return WriteFail(result, 2);

            Console.Out.WriteLine(JsonSerializer.Serialize(new WorkHandoffCliOk
            {
                Ok = true,
                WorkId = workId.Value,
                DestGeneration = result.DestGeneration,
                ConversationId = result.ConversationId,
                DestResumeEvidence = result.DestResumeEvidence,
                Pack = result.PackPath,
                SourceStopped = result.SourceStopped,
                SourceStopDetail = result.SourceStopDetail,
                AttemptId = result.AttemptId,
            }, ContinuityCliJsonContext.Default.WorkHandoffCliOk));
            return 0;
        });
        return cmd;
    }

    private static Command BuildStatus()
    {
        var workIdArg = new Argument<string>("work_id");
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("status", "Show Work / active Run status.");
        cmd.Add(workIdArg);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var workId = WorkId.Parse(parseResult.GetValue(workIdArg)!);
            var storeDir = parseResult.GetValue(storeOpt) ?? DefaultStoreDir();
            await using var store = new SqliteContinuityStore(storeDir);
            var works = new WorkService(store);
            var work = await store.GetWorkAsync(workId, ct);
            if (work is null)
                return WriteFail(ContinuityReasons.Internal, "work not found", 2);
            var active = await works.GetActiveRunAsync(workId, ct);
            var runs = await store.ListRunsAsync(workId, ct);
            Console.Out.WriteLine(JsonSerializer.Serialize(new WorkStatusCliOk
            {
                Ok = true,
                WorkId = work.Id.Value,
                HarnessAdapterId = work.HarnessAdapterId,
                CreatedAt = work.CreatedAt,
                Active = active,
                Runs = runs.ToList(),
            }, ContinuityCliJsonContext.Default.WorkStatusCliOk));
            return 0;
        });
        return cmd;
    }

    private Command BuildAdopt()
    {
        var paneOpt = new Option<string>("--pane") { Required = true };
        var socketOpt = new Option<string>("--socket") { Required = true };
        var harnessOpt = new Option<string>("--harness") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("adopt", "Name a live pane as Work generation 1. Does not move Work.");
        cmd.Add(paneOpt);
        cmd.Add(socketOpt);
        cmd.Add(harnessOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var paneId = parseResult.GetValue(paneOpt) ?? "";
            var socket = parseResult.GetValue(socketOpt) ?? "";
            var harnessId = parseResult.GetValue(harnessOpt) ?? "";
            var storeDir = parseResult.GetValue(storeOpt) ?? DefaultStoreDir();

            if (!TryCreateCertifiedHarness(harnessId, out var harness) || harness is null)
            {
                return WriteFail(
                    ContinuityReasons.HarnessUncertified,
                    "pane is not a certified harness",
                    2);
            }

            await using var store = new SqliteContinuityStore(storeDir);
            var adopted = await new WorkAdoptService(new WorkService(store), _paneProbe)
                .AdoptAsync(
                    new WorkAdoptRequest
                    {
                        PaneId = paneId,
                        SocketPath = socket,
                        HarnessId = harness.AdapterId,
                        Harness = harness,
                    },
                    ct);
            if (!adopted.Ok || adopted.Work is null || adopted.Run is null)
            {
                return WriteFail(
                    adopted.Reason ?? ContinuityReasons.Internal,
                    adopted.Detail ?? "adopt failed",
                    2);
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(new WorkAdoptCliOk
            {
                Ok = true,
                WorkId = adopted.Work.Id.Value,
                Generation = adopted.Run.Generation,
                PlacementId = adopted.Run.PlacementId,
                HarnessAdapterId = adopted.Work.HarnessAdapterId,
                ConversationId = adopted.ConversationId,
                ResumeEvidence = adopted.ResumeEvidence,
                Home = adopted.Home,
                Cwd = adopted.Cwd,
            }, ContinuityCliJsonContext.Default.WorkAdoptCliOk));
            return 0;
        });
        return cmd;
    }

    private Command BuildAttach()
    {
        var workIdArg = new Argument<string>("work_id");
        var storeOpt = new Option<string?>("--store");
        var placementStoreOpt = new Option<string?>("--placement-store");
        var homeOpt = new Option<string?>("--home");
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Print attach target JSON. Do not open a client.",
            DefaultValueFactory = _ => false,
        };
        var onceOpt = new Option<bool>("--once")
        {
            Description = "Ping the dest mux, print the snapshot, and exit.",
            DefaultValueFactory = _ => false,
        };
        var cmd = new Command(
            "attach",
            "Open the attach client against the mux that owns the active Run.");
        cmd.Add(workIdArg);
        cmd.Add(storeOpt);
        cmd.Add(placementStoreOpt);
        cmd.Add(homeOpt);
        cmd.Add(jsonOpt);
        cmd.Add(onceOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var workId = WorkId.Parse(parseResult.GetValue(workIdArg)!);
            var storeDir = parseResult.GetValue(storeOpt) ?? DefaultStoreDir();
            var occupantHome = ResolveOccupantHome(parseResult.GetValue(homeOpt));
            var occupantBefore = CaptureOccupantHome(occupantHome);
            var json = parseResult.GetValue(jsonOpt);
            var once = parseResult.GetValue(onceOpt);
            await using var store = new SqliteContinuityStore(storeDir);
            var works = new WorkService(store);
            var directory = OpenDirectory(parseResult.GetValue(placementStoreOpt));
            var resolver = new WorkAttachTargetResolver(works, directory);
            var resolved = await resolver.ResolveAsync(workId, ct);
            if (!resolved.Ok || resolved.Value is null)
            {
                return WriteFail(
                    resolved.Reason ?? ContinuityReasons.Internal,
                    resolved.Detail ?? "attach target is missing",
                    2);
            }

            if (json)
            {
                var occupantGuard = EnsureOccupantHomeUnchanged(occupantBefore);
                if (!occupantGuard.Ok)
                {
                    return WriteFail(
                        occupantGuard.Reason ?? ContinuityReasons.Internal,
                        occupantGuard.Detail ?? "attach must not start an occupant",
                        2);
                }

                Console.Out.WriteLine(JsonSerializer.Serialize(new WorkAttachCliOk
                {
                    Ok = true,
                    WorkId = resolved.Value.WorkId.Value,
                    MuxEndpoint = resolved.Value.MuxEndpoint,
                    PlacementId = resolved.Value.PlacementId.Value,
                    MuxIdentity = resolved.Value.MuxIdentity.Value,
                    Generation = resolved.Value.Generation,
                }, ContinuityCliJsonContext.Default.WorkAttachCliOk));
                return 0;
            }

            var placement = await directory.GetAsync(resolved.Value.PlacementId, ct)
                .ConfigureAwait(false);
            if (!placement.Ok || placement.Value is null)
            {
                return WriteFail(
                    placement.Reason ?? PlacementReasons.PlacementNotFound,
                    placement.Detail ?? "placement is not in the directory",
                    2);
            }

            var outcome = await _attachClient.AttachAsync(
                    new WorkAttachClientRequest
                    {
                        Target = resolved.Value,
                        Placement = placement.Value,
                        PlacementDirectory = directory,
                        Once = once,
                        InputRedirected = Console.IsInputRedirected,
                        OutputRedirected = Console.IsOutputRedirected,
                        HypaEnv = Environment.GetEnvironmentVariable(NestedAttachGuard.EnvName),
                    },
                    ct)
                .ConfigureAwait(false);
            if (!outcome.Ok && !string.IsNullOrWhiteSpace(outcome.Reason))
            {
                return WriteFail(
                    outcome.Reason,
                    outcome.Detail ?? "attach client failed",
                    outcome.ExitCode == 0 ? 2 : outcome.ExitCode);
            }

            return outcome.ExitCode;
        });
        return cmd;
    }

    private static Command BuildPlacements()
    {
        var cmd = new Command("placements", "List and register named Placements.");
        cmd.Add(BuildPlacementList());
        cmd.Add(BuildPlacementRegister());
        cmd.Add(BuildPlacementReach());
        cmd.Add(BuildPlacementGrant());
        cmd.Add(BuildPlacementGrantWork());
        cmd.Add(BuildPlacementSetWork());
        cmd.Add(BuildPlacementAddSsh());
        cmd.Add(BuildPlacementAddQuic());
        cmd.Add(BuildPlacementRename());
        cmd.Add(BuildPlacementRemove());
        cmd.Add(BuildPlacementEnable());
        cmd.Add(BuildPlacementDisable());
        return cmd;
    }

    private static Command BuildPlacementList()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("list", "List Placements the identity owns or was granted.");
        cmd.Add(identityOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var identity, out var identityReason, out var identityDetail))
                return WritePlacementFail(identityReason, identityDetail, 2);

            var directory = OpenDirectory(parseResult.GetValue(storeOpt));
            var listed = await directory.ListAsync(identity, ct);
            if (!listed.Ok || listed.Value is null)
                return WritePlacementFail(listed.Reason ?? PlacementReasons.Internal, listed.Detail ?? "", 2);

            var document = new PlacementListDocument
            {
                Ok = true,
                Heading = PlacementDirectoryNames.OnScreenHeading,
                Placements = listed.Value.Select(ToRowDto).ToList(),
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(
                document,
                PlacementJsonContext.Default.PlacementListDocument));
            return 0;
        });
        return cmd;
    }

    private static Command BuildPlacementRegister()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var kindOpt = new Option<string>("--kind") { Required = true };
        var nameOpt = new Option<string>("--name") { Required = true };
        var muxOpt = new Option<string>("--mux") { Required = true };
        var idOpt = new Option<string?>("--id");
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("register", "Register a named Placement.");
        cmd.Add(identityOpt);
        cmd.Add(kindOpt);
        cmd.Add(nameOpt);
        cmd.Add(muxOpt);
        cmd.Add(idOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var identity, out var identityReason, out var identityDetail))
                return WritePlacementFail(identityReason, identityDetail, 2);
            if (!TryParseDirectoryKind(parseResult.GetValue(kindOpt), out var kind))
                return WritePlacementFail(PlacementReasons.KindInvalid, "kind must be local, peer, or cube", 2);
            if (!MuxIdentity.TryParse(parseResult.GetValue(muxOpt), out var mux))
                return WritePlacementFail(PlacementReasons.MuxIdentityInvalid, "mux identity must be an opaque mux_ value", 2);
            Hypa.Placement.Domain.PlacementId? requestedId = null;
            var rawId = parseResult.GetValue(idOpt);
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                if (!Hypa.Placement.Domain.PlacementId.TryParse(rawId, out var parsedId))
                    return WritePlacementFail(PlacementReasons.PlacementNotFound, "placement id is invalid", 2);
                requestedId = parsedId;
            }

            var directory = OpenDirectory(parseResult.GetValue(storeOpt));
            var registered = await directory.RegisterAsync(
                new PlacementRegistration
                {
                    Owner = identity,
                    DisplayName = parseResult.GetValue(nameOpt) ?? "",
                    Kind = kind,
                    MuxIdentity = mux,
                    Id = requestedId,
                },
                ct);
            if (!registered.Ok || registered.Value is null)
                return WritePlacementFail(registered.Reason ?? PlacementReasons.Internal, registered.Detail ?? "", 2);

            var document = new PlacementRegisterDocument
            {
                Ok = true,
                PlacementId = registered.Value.Id.Value,
                Kind = registered.Value.Kind,
                MuxIdentity = registered.Value.MuxIdentity.Value,
                Reachability = registered.Value.Reachability,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(
                document,
                PlacementJsonContext.Default.PlacementRegisterDocument));
            return 0;
        });
        return cmd;
    }

    private static Command BuildPlacementReach()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var placementOpt = new Option<string>("--placement") { Required = true };
        var statusOpt = new Option<string>("--status") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("reach", "Change reachability without changing placement id.");
        cmd.Add(identityOpt);
        cmd.Add(placementOpt);
        cmd.Add(statusOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var actor, out var identityReason, out var identityDetail))
                return WritePlacementFail(identityReason, identityDetail, 2);
            if (!Hypa.Placement.Domain.PlacementId.TryParse(parseResult.GetValue(placementOpt), out var placementId))
                return WritePlacementFail(PlacementReasons.PlacementNotFound, "placement id is invalid", 2);
            if (!TryParseReachability(parseResult.GetValue(statusOpt), out var reachability))
                return WritePlacementFail(PlacementReasons.ReachabilityInvalid, "status must be local, reachable, unreachable, or asleep", 2);

            var directory = OpenDirectory(parseResult.GetValue(storeOpt));
            var updated = await directory.SetReachabilityAsync(actor, placementId, reachability, ct);
            if (!updated.Ok || updated.Value is null)
                return WritePlacementFail(updated.Reason ?? PlacementReasons.Internal, updated.Detail ?? "", 2);

            var document = new PlacementRegisterDocument
            {
                Ok = true,
                PlacementId = updated.Value.Id.Value,
                Kind = updated.Value.Kind,
                MuxIdentity = updated.Value.MuxIdentity.Value,
                Reachability = updated.Value.Reachability,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(
                document,
                PlacementJsonContext.Default.PlacementRegisterDocument));
            return 0;
        });
        return cmd;
    }

    private static Command BuildPlacementGrant()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var placementOpt = new Option<string>("--placement") { Required = true };
        var toOpt = new Option<string>("--to") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("grant", "Grant list access to a Placement.");
        cmd.Add(identityOpt);
        cmd.Add(placementOpt);
        cmd.Add(toOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var actor, out var identityReason, out var identityDetail))
                return WritePlacementFail(identityReason, identityDetail, 2);
            if (!Hypa.Placement.Domain.PlacementId.TryParse(parseResult.GetValue(placementOpt), out var placementId))
                return WritePlacementFail(PlacementReasons.PlacementNotFound, "placement id is invalid", 2);
            if (!DirectoryIdentity.TryParse(parseResult.GetValue(toOpt), out var grantee))
                return WritePlacementFail(PlacementReasons.IdentityInvalid, "grantee is required", 2);

            var directory = OpenDirectory(parseResult.GetValue(storeOpt));
            var granted = await directory.GrantListAccessAsync(actor, placementId, grantee, ct);
            if (!granted.Ok)
                return WritePlacementFail(granted.Reason ?? PlacementReasons.Internal, granted.Detail ?? "", 2);
            return WritePlacementOk();
        });
        return cmd;
    }

    private static Command BuildPlacementGrantWork()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var toOpt = new Option<string>("--to") { Required = true };
        var workOpt = new Option<string>("--work") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("grant-work", "Grant Work access so a list row may show the Work id.");
        cmd.Add(identityOpt);
        cmd.Add(toOpt);
        cmd.Add(workOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var actor, out var identityReason, out var identityDetail))
                return WritePlacementFail(identityReason, identityDetail, 2);
            if (!DirectoryIdentity.TryParse(parseResult.GetValue(toOpt), out var identity))
                return WritePlacementFail(PlacementReasons.IdentityInvalid, "identity is required", 2);

            var directory = OpenDirectory(parseResult.GetValue(storeOpt));
            var granted = await directory.GrantWorkAccessAsync(
                actor,
                identity,
                parseResult.GetValue(workOpt) ?? "",
                ct);
            if (!granted.Ok)
                return WritePlacementFail(granted.Reason ?? PlacementReasons.Internal, granted.Detail ?? "", 2);
            return WritePlacementOk();
        });
        return cmd;
    }

    private static Command BuildPlacementSetWork()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var placementOpt = new Option<string>("--placement") { Required = true };
        var workOpt = new Option<string>("--work") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("set-work", "Set the optional active Work id on a Placement.");
        cmd.Add(identityOpt);
        cmd.Add(placementOpt);
        cmd.Add(workOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var actor, out var identityReason, out var identityDetail))
                return WritePlacementFail(identityReason, identityDetail, 2);
            if (!Hypa.Placement.Domain.PlacementId.TryParse(parseResult.GetValue(placementOpt), out var placementId))
                return WritePlacementFail(PlacementReasons.PlacementNotFound, "placement id is invalid", 2);

            var directory = OpenDirectory(parseResult.GetValue(storeOpt));
            var updated = await directory.SetActiveWorkAsync(
                actor,
                placementId,
                parseResult.GetValue(workOpt),
                ct);
            if (!updated.Ok)
                return WritePlacementFail(updated.Reason ?? PlacementReasons.Internal, updated.Detail ?? "", 2);
            return WritePlacementOk();
        });
        return cmd;
    }

    private static IPlacementDirectory OpenDirectory(string? storeDir) =>
        new PlacementDirectoryService(new FilePlacementDirectoryStore(storeDir ?? DefaultPlacementStoreDir()));

    private static ISshPlacementCatalog OpenSshCatalog(string? storeDir)
    {
        var store = new FilePlacementDirectoryStore(storeDir ?? DefaultPlacementStoreDir());
        var preparation = new SshPlacementPreparationService(
            new OpenSshRemoteMuxAdapter(),
            new ControlPlaneRemoteMuxIdentityReader());
        return new SshPlacementCatalogService(store, preparation);
    }

    private static IQuicPlacementCatalog OpenQuicCatalog(string? storeDir) =>
        new QuicPlacementCatalogService(new FilePlacementDirectoryStore(storeDir ?? DefaultPlacementStoreDir()));

    private static Command BuildPlacementAddSsh()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var nameOpt = new Option<string>("--name") { Required = true };
        var targetOpt = new Option<string>("--target") { Required = true };
        var sessionOpt = new Option<string>("--session") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("add-ssh", "Add an SSH-backed peer Placement.");
        cmd.Add(identityOpt);
        cmd.Add(nameOpt);
        cmd.Add(targetOpt);
        cmd.Add(sessionOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var owner, out var identityReason, out var identityDetail))
                return WriteSshMutationFail(null, identityReason, identityDetail, 2);
            var catalog = OpenSshCatalog(parseResult.GetValue(storeOpt));
            var added = await catalog.AddAsync(
                new SshPlacementAddRequest
                {
                    Owner = owner,
                    Label = parseResult.GetValue(nameOpt) ?? "",
                    Target = parseResult.GetValue(targetOpt) ?? "",
                    Session = parseResult.GetValue(sessionOpt) ?? "",
                },
                ct);
            if (!added.Ok || added.Value is null)
            {
                var exit = added.Reason is PlacementReasons.Internal ? 1 : 2;
                return WriteSshMutationFail(null, added.Reason, added.Detail, exit);
            }

            Console.Error.WriteLine($"Placement {added.Value.Id.Value} saved. Remote session is ready.");
            return WriteSshMutationOk(added.Value.Id.Value);
        });
        return cmd;
    }

    private static Command BuildPlacementAddQuic()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var nameOpt = new Option<string>("--name") { Required = true };
        var targetOpt = new Option<string>("--target") { Required = true };
        var sessionOpt = new Option<string>("--session") { Required = true };
        var fingerprintOpt = new Option<string?>("--fingerprint")
        {
            Description = "SHA-256 fingerprint of the accept certificate.",
        };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("add-quic", "Add a QUIC-backed peer Placement.");
        cmd.Add(identityOpt);
        cmd.Add(nameOpt);
        cmd.Add(targetOpt);
        cmd.Add(sessionOpt);
        cmd.Add(fingerprintOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var owner, out var identityReason, out var identityDetail))
                return WriteSshMutationFail(null, identityReason, identityDetail, 2);
            var catalog = OpenQuicCatalog(parseResult.GetValue(storeOpt));
            var added = await catalog.AddAsync(
                new QuicPlacementAddRequest
                {
                    Owner = owner,
                    Label = parseResult.GetValue(nameOpt) ?? "",
                    Target = parseResult.GetValue(targetOpt) ?? "",
                    Session = parseResult.GetValue(sessionOpt) ?? "",
                    CertificateSha256 = parseResult.GetValue(fingerprintOpt),
                },
                ct);
            if (!added.Ok || added.Value is null)
            {
                var exit = added.Reason is PlacementReasons.Internal ? 1 : 2;
                return WriteSshMutationFail(null, added.Reason, added.Detail, exit);
            }

            Console.Error.WriteLine($"Placement {added.Value.Id.Value} saved.");
            return WriteSshMutationOk(added.Value.Id.Value);
        });
        return cmd;
    }

    private static Command BuildPlacementRename()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var placementOpt = new Option<string>("--placement") { Required = true };
        var nameOpt = new Option<string>("--name") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("rename", "Rename a provider-backed peer Placement.");
        cmd.Add(identityOpt);
        cmd.Add(placementOpt);
        cmd.Add(nameOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var owner, out var identityReason, out var identityDetail))
                return WriteSshMutationFail(null, identityReason, identityDetail, 2);
            if (!Hypa.Placement.Domain.PlacementId.TryParse(parseResult.GetValue(placementOpt), out var placementId))
                return WriteSshMutationFail(null, PlacementReasons.PlacementNotFound, "placement id is invalid", 2);
            var storeDir = parseResult.GetValue(storeOpt);
            var renamed = await RenamePeerPlacementAsync(
                owner,
                placementId,
                parseResult.GetValue(nameOpt) ?? "",
                storeDir,
                ct);
            if (!renamed.Ok || renamed.Value is null)
            {
                var exit = renamed.Reason is PlacementReasons.Internal ? 1 : 2;
                return WriteSshMutationFail(null, renamed.Reason, renamed.Detail, exit);
            }

            Console.Error.WriteLine($"Placement {renamed.Value.Id.Value} renamed.");
            return WriteSshMutationOk(renamed.Value.Id.Value);
        });
        return cmd;
    }

    private static Command BuildPlacementRemove()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var placementOpt = new Option<string>("--placement") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("remove", "Remove a provider-backed peer Placement.");
        cmd.Add(identityOpt);
        cmd.Add(placementOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var owner, out var identityReason, out var identityDetail))
                return WriteSshMutationFail(null, identityReason, identityDetail, 2);
            if (!Hypa.Placement.Domain.PlacementId.TryParse(parseResult.GetValue(placementOpt), out var placementId))
                return WriteSshMutationFail(null, PlacementReasons.PlacementNotFound, "placement id is invalid", 2);
            var removed = await RemovePeerPlacementAsync(owner, placementId, parseResult.GetValue(storeOpt), ct);
            if (!removed.Ok || removed.Value is null)
            {
                var exit = removed.Reason is PlacementReasons.Internal ? 1 : 2;
                return WriteSshMutationFail(null, removed.Reason, removed.Detail, exit);
            }

            Console.Error.WriteLine(RemoveSuccessDetail(removed.Value));
            return WriteSshMutationOk(removed.Value.Id.Value);
        });
        return cmd;
    }

    private static Command BuildPlacementEnable()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var placementOpt = new Option<string>("--placement") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("enable", "Enable a provider-backed peer Placement.");
        cmd.Add(identityOpt);
        cmd.Add(placementOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var owner, out var identityReason, out var identityDetail))
                return WriteSshMutationFail(null, identityReason, identityDetail, 2);
            if (!Hypa.Placement.Domain.PlacementId.TryParse(parseResult.GetValue(placementOpt), out var placementId))
                return WriteSshMutationFail(null, PlacementReasons.PlacementNotFound, "placement id is invalid", 2);
            var storeDir = parseResult.GetValue(storeOpt);
            var enabled = await EnablePeerPlacementAsync(owner, placementId, storeDir, ct);
            if (!enabled.Ok)
            {
                var exit = enabled.Reason is PlacementReasons.Internal ? 1 : 2;
                var label = enabled.Value?.DisplayName;
                if (string.IsNullOrWhiteSpace(label))
                {
                    var loaded = await OpenDirectory(storeDir).GetAsync(placementId, ct);
                    label = loaded.Value?.DisplayName;
                }

                var detail = enabled.Detail
                    ?? $"Remote preparation failed for {label ?? placementId.Value}. Placement remains disabled.";
                return WriteSshMutationFail(placementId.Value, enabled.Reason, detail, exit);
            }

            if (enabled.Value is not { } enabledRecord)
                return WriteSshMutationFail(placementId.Value, PlacementReasons.Internal, "enable returned no row", 1);

            Console.Error.WriteLine($"Placement {enabledRecord.Id.Value} enabled.");
            return WriteSshMutationOk(enabledRecord.Id.Value);
        });
        return cmd;
    }

    private static Command BuildPlacementDisable()
    {
        var identityOpt = new Option<string>("--identity") { Required = true };
        var placementOpt = new Option<string>("--placement") { Required = true };
        var storeOpt = new Option<string?>("--store");
        var cmd = new Command("disable", "Disable a provider-backed peer Placement.");
        cmd.Add(identityOpt);
        cmd.Add(placementOpt);
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolveActor(parseResult.GetValue(identityOpt), out var owner, out var identityReason, out var identityDetail))
                return WriteSshMutationFail(null, identityReason, identityDetail, 2);
            if (!Hypa.Placement.Domain.PlacementId.TryParse(parseResult.GetValue(placementOpt), out var placementId))
                return WriteSshMutationFail(null, PlacementReasons.PlacementNotFound, "placement id is invalid", 2);
            var disabled = await DisablePeerPlacementAsync(owner, placementId, parseResult.GetValue(storeOpt), ct);
            if (!disabled.Ok || disabled.Value is null)
            {
                var exit = disabled.Reason is PlacementReasons.Internal ? 1 : 2;
                return WriteSshMutationFail(null, disabled.Reason, disabled.Detail, exit);
            }

            Console.Error.WriteLine(DisableSuccessDetail(disabled.Value));
            return WriteSshMutationOk(disabled.Value.Id.Value);
        });
        return cmd;
    }

    private static string RemoveSuccessDetail(PlacementRecord record) =>
        record.Ssh is not null
            ? $"Placement {record.Id.Value} removed. Remote session remains running."
            : $"Placement {record.Id.Value} removed.";

    private static string DisableSuccessDetail(PlacementRecord record) =>
        record.Ssh is not null
            ? $"Placement {record.Id.Value} disabled. Remote session remains running."
            : $"Placement {record.Id.Value} disabled.";

    private static async ValueTask<PlacementOutcome<PlacementRecord>> RenamePeerPlacementAsync(
        DirectoryIdentity owner,
        Hypa.Placement.Domain.PlacementId placementId,
        string label,
        string? storeDir,
        CancellationToken cancellationToken)
    {
        var provider = await ResolvePeerProviderAsync(placementId, storeDir, cancellationToken).ConfigureAwait(false);
        if (!provider.Ok)
            return PlacementOutcome<PlacementRecord>.Failure(provider.Reason ?? PlacementReasons.Internal, provider.Detail ?? "");

        return provider.Value switch
        {
            PlacementPeerProviders.Ssh => await OpenSshCatalog(storeDir)
                .RenameAsync(owner, placementId, label, cancellationToken),
            PlacementPeerProviders.Quic => await OpenQuicCatalog(storeDir)
                .RenameAsync(owner, placementId, label, cancellationToken),
            _ => PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProviderMismatch,
                "This Placement does not use a peer provider."),
        };
    }

    private static async ValueTask<PlacementOutcome<PlacementRecord>> RemovePeerPlacementAsync(
        DirectoryIdentity owner,
        Hypa.Placement.Domain.PlacementId placementId,
        string? storeDir,
        CancellationToken cancellationToken)
    {
        var provider = await ResolvePeerProviderAsync(placementId, storeDir, cancellationToken).ConfigureAwait(false);
        if (!provider.Ok)
            return PlacementOutcome<PlacementRecord>.Failure(provider.Reason ?? PlacementReasons.Internal, provider.Detail ?? "");

        return provider.Value switch
        {
            PlacementPeerProviders.Ssh => await OpenSshCatalog(storeDir).RemoveAsync(owner, placementId, cancellationToken),
            PlacementPeerProviders.Quic => await OpenQuicCatalog(storeDir).RemoveAsync(owner, placementId, cancellationToken),
            _ => PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProviderMismatch,
                "This Placement does not use a peer provider."),
        };
    }

    private static async ValueTask<PlacementOutcome<PlacementRecord>> EnablePeerPlacementAsync(
        DirectoryIdentity owner,
        Hypa.Placement.Domain.PlacementId placementId,
        string? storeDir,
        CancellationToken cancellationToken)
    {
        var provider = await ResolvePeerProviderAsync(placementId, storeDir, cancellationToken).ConfigureAwait(false);
        if (!provider.Ok)
            return PlacementOutcome<PlacementRecord>.Failure(provider.Reason ?? PlacementReasons.Internal, provider.Detail ?? "");

        return provider.Value switch
        {
            PlacementPeerProviders.Ssh => await OpenSshCatalog(storeDir).EnableAsync(owner, placementId, cancellationToken),
            PlacementPeerProviders.Quic => await OpenQuicCatalog(storeDir).EnableAsync(owner, placementId, cancellationToken),
            _ => PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProviderMismatch,
                "This Placement does not use a peer provider."),
        };
    }

    private static async ValueTask<PlacementOutcome<PlacementRecord>> DisablePeerPlacementAsync(
        DirectoryIdentity owner,
        Hypa.Placement.Domain.PlacementId placementId,
        string? storeDir,
        CancellationToken cancellationToken)
    {
        var provider = await ResolvePeerProviderAsync(placementId, storeDir, cancellationToken).ConfigureAwait(false);
        if (!provider.Ok)
            return PlacementOutcome<PlacementRecord>.Failure(provider.Reason ?? PlacementReasons.Internal, provider.Detail ?? "");

        return provider.Value switch
        {
            PlacementPeerProviders.Ssh => await OpenSshCatalog(storeDir).DisableAsync(owner, placementId, cancellationToken),
            PlacementPeerProviders.Quic => await OpenQuicCatalog(storeDir).DisableAsync(owner, placementId, cancellationToken),
            _ => PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProviderMismatch,
                "This Placement does not use a peer provider."),
        };
    }

    private static async ValueTask<PlacementOutcome<string>> ResolvePeerProviderAsync(
        Hypa.Placement.Domain.PlacementId placementId,
        string? storeDir,
        CancellationToken cancellationToken)
    {
        var loaded = await OpenDirectory(storeDir).GetAsync(placementId, cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null)
            return PlacementOutcome<string>.Failure(loaded.Reason ?? PlacementReasons.PlacementNotFound, loaded.Detail ?? "");

        if (loaded.Value.Ssh is not null)
            return PlacementOutcome<string>.Success(PlacementPeerProviders.Ssh);
        if (loaded.Value.Quic is not null)
            return PlacementOutcome<string>.Success(PlacementPeerProviders.Quic);

        return PlacementOutcome<string>.Failure(
            PlacementReasons.ProviderMismatch,
            "This Placement does not use a peer provider.");
    }

    private static bool TryResolveActor(
        string? claimed,
        out DirectoryIdentity actor,
        out string reason,
        out string detail)
    {
        actor = default;
        reason = PlacementReasons.IdentityInvalid;
        detail = "local operator identity is required";
        if (!ProcessLocalOperatorIdentity.TryResolve(out var authenticated))
            return false;

        var bound = DirectoryActorBinding.Bind(authenticated, claimed);
        if (!bound.Ok)
        {
            reason = bound.Reason ?? PlacementReasons.Unauthorized;
            detail = bound.Detail ?? "caller identity is not the local operator";
            return false;
        }

        actor = bound.Value;
        reason = "";
        detail = "";
        return true;
    }

    private static string DefaultPlacementStoreDir() => PlacementStatePaths.ResolveFromEnvironment();

    private static PlacementRowDto ToRowDto(PlacementRow row) =>
        new()
        {
            PlacementId = row.Id.Value,
            DisplayName = row.DisplayName,
            Kind = row.Kind,
            MuxIdentity = row.MuxIdentity.Value,
            Reachability = row.Reachability,
            ActiveWorkId = row.ActiveWorkId,
            LastSeen = row.LastSeen.UtcDateTime.ToString("O"),
            Ssh = SshPlacementProfileMapper.ToDto(row.Ssh),
            Quic = QuicPlacementProfileMapper.ToDto(row.Quic),
        };

    private static bool TryParseDirectoryKind(string? raw, out PlacementDirectoryKind kind)
    {
        kind = default;
        if (string.Equals(raw, "local", StringComparison.OrdinalIgnoreCase))
        {
            kind = PlacementDirectoryKind.Local;
            return true;
        }

        if (string.Equals(raw, "peer", StringComparison.OrdinalIgnoreCase))
        {
            kind = PlacementDirectoryKind.Peer;
            return true;
        }

        if (string.Equals(raw, "cube", StringComparison.OrdinalIgnoreCase))
        {
            kind = PlacementDirectoryKind.Cube;
            return true;
        }

        return false;
    }

    private static bool TryParseReachability(string? raw, out PlacementReachability reachability)
    {
        reachability = default;
        if (string.Equals(raw, "local", StringComparison.OrdinalIgnoreCase))
        {
            reachability = PlacementReachability.Local;
            return true;
        }

        if (string.Equals(raw, "reachable", StringComparison.OrdinalIgnoreCase))
        {
            reachability = PlacementReachability.Reachable;
            return true;
        }

        if (string.Equals(raw, "unreachable", StringComparison.OrdinalIgnoreCase))
        {
            reachability = PlacementReachability.Unreachable;
            return true;
        }

        if (string.Equals(raw, "asleep", StringComparison.OrdinalIgnoreCase))
        {
            reachability = PlacementReachability.Asleep;
            return true;
        }

        return false;
    }

    private static int WritePlacementOk()
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new PlacementMutationDocument { Ok = true },
            PlacementJsonContext.Default.PlacementMutationDocument));
        return 0;
    }

    private static int WritePlacementFail(string reason, string detail, int exit)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new PlacementFailureDocument
            {
                Ok = false,
                Reason = reason,
                Detail = detail,
            },
            PlacementJsonContext.Default.PlacementFailureDocument));
        return exit;
    }

    private static int WriteSshMutationOk(string placementId)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new PlacementSshMutationDocument
            {
                Ok = true,
                PlacementId = placementId,
            },
            PlacementJsonContext.Default.PlacementSshMutationDocument));
        return 0;
    }

    private static int WriteSshMutationFail(string? placementId, string? reason, string? detail, int exit)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new PlacementSshMutationDocument
            {
                Ok = false,
                PlacementId = placementId,
                Reason = reason,
                Detail = detail,
            },
            PlacementJsonContext.Default.PlacementSshMutationDocument));
        if (!string.IsNullOrWhiteSpace(detail))
            Console.Error.WriteLine(detail);
        return exit;
    }

    private static IHarnessAdapter CreateHarness(string id) =>
        TryCreateCertifiedHarness(id, out var harness) && harness is not null
            ? harness
            : new FakeHarnessAdapter();

    private static bool TryCreateCertifiedHarness(string? id, out IHarnessAdapter? harness)
    {
        harness = null;
        if (string.Equals(id, CertifiedHarnessIds.Pi, StringComparison.OrdinalIgnoreCase))
        {
            harness = new PiHarnessAdapter();
            return true;
        }

        if (string.Equals(id, CertifiedHarnessIds.Fake, StringComparison.OrdinalIgnoreCase))
        {
            harness = new FakeHarnessAdapter();
            return true;
        }

        return false;
    }

    private static string? ResolveOccupantHome(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return null;

        return Path.GetFullPath(home);
    }

    private static OccupantHomeCapture CaptureOccupantHome(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return OccupantHomeCapture.Empty;

        return new OccupantHomeCapture
        {
            Home = home,
            Files = ListOccupantStoreFiles(home),
            StartedMarker = ReadOccupantStartMarker(home),
        };
    }

    private static ContinuityOutcome EnsureOccupantHomeUnchanged(OccupantHomeCapture before)
    {
        if (string.IsNullOrWhiteSpace(before.Home))
            return ContinuityOutcome.Success();

        var after = CaptureOccupantHome(before.Home);
        if (!before.Files.SequenceEqual(after.Files, StringComparer.Ordinal)
            || !string.Equals(before.StartedMarker, after.StartedMarker, StringComparison.Ordinal))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.Internal,
                "attach must not start an occupant");
        }

        return ContinuityOutcome.Success();
    }

    private static string[] ListOccupantStoreFiles(string home)
    {
        var files = new List<string>();
        foreach (var root in OccupantStoreRoots(home))
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                files.Add(string.Join(
                    '\t',
                    file,
                    info.Length.ToString(),
                    info.LastWriteTimeUtc.Ticks.ToString()));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files.ToArray();
    }

    private static string? ReadOccupantStartMarker(string home)
    {
        var path = Path.Combine(
            FakeHarnessAdapter.AgentStoreRoot(home),
            FakeHarnessAdapter.StartedFileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static IEnumerable<string> OccupantStoreRoots(string home)
    {
        yield return FakeHarnessAdapter.AgentStoreRoot(home);
        yield return Path.Combine(home, ".pi", "agent");
    }

    private sealed record OccupantHomeCapture
    {
        public static OccupantHomeCapture Empty { get; } = new()
        {
            Home = null,
            Files = [],
            StartedMarker = null,
        };

        public string? Home { get; init; }
        public string[] Files { get; init; } = [];
        public string? StartedMarker { get; init; }
    }

    private static int WriteApplyOk(DestApplyResult result)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new WorkApplyCliOk
        {
            Ok = true,
            WorkId = result.WorkId,
            DestGeneration = result.DestGeneration,
            ConversationId = result.ConversationId,
            DestResumeEvidence = result.DestResumeEvidence,
            AttemptId = result.AttemptId,
        }, ContinuityCliJsonContext.Default.WorkApplyCliOk));
        return 0;
    }

    private static string? ResolveRelayUrl(string? relayUrl)
    {
        if (!string.IsNullOrWhiteSpace(relayUrl))
            return relayUrl.Trim();
        var fromEnv = Environment.GetEnvironmentVariable("HYPA_RELAY_URL");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();
        return null;
    }

    private static bool HasDestWorkerJoinInput(
        string? relayUrl,
        string? nonce,
        string? deviceId,
        string? joinSecret) =>
        !string.IsNullOrWhiteSpace(ResolveRelayUrl(relayUrl))
        || !string.IsNullOrWhiteSpace(nonce)
        || !string.IsNullOrWhiteSpace(deviceId)
        || !string.IsNullOrWhiteSpace(joinSecret);

    /// <summary>
    /// In-process dest workers may omit join flags. The DI singleton
    /// <see cref="ConnectivityDestWorkExecutor"/> may not.
    /// </summary>
    private static bool CanReuseInjectedDestExecutor(IDestWorkExecutor? executor) =>
        executor is not null and not ConnectivityDestWorkExecutor;

    private static string DefaultStoreDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg, "hypa", "continuity");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "hypa", "continuity");
    }

    private static string NormalizeToEndpoint(string to)
    {
        const string mux = "mux://";
        if (to.StartsWith(mux, StringComparison.OrdinalIgnoreCase))
        {
            var rest = to[mux.Length..];
            return rest.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
                ? rest
                : "unix:" + rest;
        }

        return to.StartsWith("unix:", StringComparison.OrdinalIgnoreCase) ? to : "unix:" + to;
    }

    private static int WriteFail(string reason, string detail, int exit) =>
        WriteFail(new ContinuityCliResult
        {
            Ok = false,
            Reason = reason,
            Detail = detail,
        }, exit);

    private static int WriteFail(HandoffResult result, int exit) =>
        WriteFail(new ContinuityCliResult
        {
            Ok = false,
            Reason = result.Reason,
            Detail = result.Detail,
            Stage = result.Stage,
            AttemptId = result.AttemptId,
            Retryable = result.Retryable,
            SourceStatus = result.SourceStatus,
            DestConfirmation = result.DestConfirmation,
        }, exit);

    private static int WriteFail(ContinuityCliResult payload, int exit)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, ContinuityCliJsonContext.Default.ContinuityCliResult));
        return exit;
    }

    private sealed class DisposableTemp : IDisposable
    {
        public string Path { get; }

        public DisposableTemp(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class RelayDestConfirmationTransport : IHandoffTransport
    {
        private readonly RendezvousUrl _url;
        private readonly string _destPlacementId;

        public RelayDestConfirmationTransport(RendezvousUrl url, string destPlacement)
        {
            _url = url;
            _destPlacementId = destPlacement;
        }

        public async ValueTask<ContinuityOutcome> NotifyStageAsync(
            HandoffState stage,
            string attemptId,
            CancellationToken cancellationToken = default)
        {
            if (stage is HandoffState.Probing or HandoffState.Fencing)
                return await ConfirmDestAsync(attemptId, cancellationToken).ConfigureAwait(false);
            return await ProbeReachabilityAsync(cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<ContinuityOutcome> ProbeReachabilityAsync(CancellationToken cancellationToken)
        {
            if (!OutboundRendezvousJoin.TryGetEndpoint(_url, out var host, out var port))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.PeerUnavailable,
                    "relay is not reachable");
            }

            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                return ContinuityOutcome.Success();
            }
            catch (Exception)
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.PeerUnavailable,
                    "relay closed before dest received the transfer");
            }
        }

        private async ValueTask<ContinuityOutcome> ConfirmDestAsync(
            string attemptId,
            CancellationToken cancellationToken)
        {
            if (!TryBuildDestJoin(attemptId, out var bootstrap, out var buildFail) || bootstrap is null)
                return buildFail;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                var joined = await new OutboundRendezvousJoin(_url)
                    .JoinAsync(bootstrap, timeout.Token)
                    .ConfigureAwait(false);
                if (joined.Ok)
                {
                    return ContinuityOutcome.Failure(
                        ContinuityReasons.UnknownCommit,
                        "join match is not dest confirmation");
                }

                return ContinuityOutcome.Failure(
                    joined.Reason ?? ContinuityReasons.PeerUnavailable,
                    joined.Detail ?? "dest did not confirm");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.PeerUnavailable,
                    "dest did not confirm before timeout");
            }
            catch (Exception)
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.PeerUnavailable,
                    "relay closed before dest confirmation");
            }
        }

        private bool TryBuildDestJoin(
            string attemptId,
            out JoinBootstrap? bootstrap,
            out ContinuityOutcome failure)
        {
            bootstrap = null;
            failure = ContinuityOutcome.Success();
            var nonceValue = "nonce" + Guid.NewGuid().ToString("N")[..8];
            if (!JoinNonce.TryParse(nonceValue, out var nonce))
            {
                failure = ContinuityOutcome.Failure(
                    ContinuityReasons.PeerUnavailable,
                    "dest confirm nonce is invalid");
                return false;
            }

            if (!DeviceId.TryParse("dev_handoff1", out var deviceId))
            {
                failure = ContinuityOutcome.Failure(
                    ContinuityReasons.PeerUnavailable,
                    "dest confirm device is invalid");
                return false;
            }

            if (!Hypa.Connectivity.Domain.PlacementId.TryParse(_destPlacementId, out var destPlacement))
            {
                failure = ContinuityOutcome.Failure(
                    ContinuityReasons.PeerUnavailable,
                    "dest confirm placement is invalid");
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var issued = new DeveloperJoinCapabilityIssuer().Issue(
                destPlacement,
                deviceId,
                JoinRole.Client,
                nonce,
                now.AddMinutes(1),
                now);
            if (!issued.Ok || issued.Value is null)
            {
                failure = ContinuityOutcome.Failure(
                    issued.Reason ?? ContinuityReasons.PeerUnavailable,
                    issued.Detail ?? "dest confirm capability failed");
                return false;
            }

            _ = attemptId;
            bootstrap = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = JoinRole.Client,
                PlacementId = destPlacement,
                Nonce = nonce,
                StreamClass = StreamClass.Control,
                Capability = issued.Value,
                Audience = RendezvousAudiences.Rendezvous,
                TenantScope = RendezvousTenants.Local,
            };
            return true;
        }
    }
}

public sealed record ContinuityCliResult
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? Stage { get; init; }
    public string? AttemptId { get; init; }
    public bool? Retryable { get; init; }
    public string? SourceStatus { get; init; }
    public string? DestConfirmation { get; init; }
}

public sealed record WorkPackCliOk
{
    public bool Packed { get; init; }
    public string? Pack { get; init; }
    public string? WorkId { get; init; }
    public long Generation { get; init; }
}

public sealed record WorkApplyCliOk
{
    public bool Ok { get; init; }
    public string? WorkId { get; init; }
    public long? DestGeneration { get; init; }
    public string? ConversationId { get; init; }
    [JsonConverter(typeof(ResumeEvidenceJsonConverter))]
    public ResumeEvidence DestResumeEvidence { get; init; }
    public string? AttemptId { get; init; }
}

public sealed record WorkHandoffCliOk
{
    public bool Ok { get; init; }
    public string? WorkId { get; init; }
    public long? DestGeneration { get; init; }
    public string? ConversationId { get; init; }
    [JsonConverter(typeof(ResumeEvidenceJsonConverter))]
    public ResumeEvidence DestResumeEvidence { get; init; }
    public string? Pack { get; init; }
    public bool? SourceStopped { get; init; }
    public string? SourceStopDetail { get; init; }
    public string? AttemptId { get; init; }
}

public sealed record WorkStatusCliOk
{
    public bool Ok { get; init; }
    public string? WorkId { get; init; }
    public string? HarnessAdapterId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public RunRecord? Active { get; init; }
    public List<RunRecord>? Runs { get; init; }
}

public sealed record WorkAttachCliOk
{
    public bool Ok { get; init; }
    public string? WorkId { get; init; }
    public string? MuxEndpoint { get; init; }
    public string? PlacementId { get; init; }
    public string? MuxIdentity { get; init; }
    public long Generation { get; init; }
}

public sealed record WorkAdoptCliOk
{
    public bool Ok { get; init; }
    public string? WorkId { get; init; }
    public long Generation { get; init; }
    public string? PlacementId { get; init; }
    public string? HarnessAdapterId { get; init; }
    public string? ConversationId { get; init; }
    [JsonConverter(typeof(ResumeEvidenceJsonConverter))]
    public ResumeEvidence ResumeEvidence { get; init; }
    public string? Home { get; init; }
    public string? Cwd { get; init; }
}

[JsonSerializable(typeof(ContinuityCliResult))]
[JsonSerializable(typeof(WorkPackCliOk))]
[JsonSerializable(typeof(WorkApplyCliOk))]
[JsonSerializable(typeof(WorkHandoffCliOk))]
[JsonSerializable(typeof(DestApplyRequest))]
[JsonSerializable(typeof(DestApplyResult))]
[JsonSerializable(typeof(WorkStatusCliOk))]
[JsonSerializable(typeof(WorkAttachCliOk))]
[JsonSerializable(typeof(WorkAdoptCliOk))]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(WorkRecord))]
[JsonSerializable(typeof(List<RunRecord>))]
[JsonSerializable(typeof(ResumeEvidence))]
[JsonSerializable(typeof(ResumeProbeResult))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    Converters = [typeof(ResumeEvidenceJsonConverter)])]
public partial class ContinuityCliJsonContext : JsonSerializerContext;
