using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Placement;

public sealed class RemoteMuxPathTests
{
    private const string CompatibleRunningProbe =
        "Linux\nx86_64\n---\nrunning\n{\"ok\":true,\"protocol\":1}\n";

    private const string IncompatibleRunningProbe =
        "Linux\nx86_64\n---\nrunning\n{\"ok\":true,\"protocol\":0}\n";

    [Fact]
    public async Task Disabled_profile_cannot_open()
    {
        var ssh = new RecordingOpenSsh();
        var adapter = CreateAdapter(ssh);
        Assert.True(PeerProfile.TryCreate(
            "id1", "lab", "host", "agents", enabled: false, PeerProviders.Ssh, out var profile, out _));
        var outcome = await adapter.OpenAsync(RemoteMuxOpenRequest.FromProfile(profile));
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.ProfileDisabled, outcome.Reason);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task Enabled_profile_open_uses_contract_fields()
    {
        var ssh = new RecordingOpenSsh { Stdout = CompatibleRunningProbe };
        var bridge = new StubBridgeFactory();
        var adapter = CreateAdapter(ssh, bridge: bridge);
        Assert.True(PeerProfile.TryCreate(
            "id1",
            "Build host",
            "user@dev",
            "agents",
            enabled: true,
            PeerProviders.Ssh,
            out var profile,
            out _));
        var outcome = await adapter.OpenAsync(RemoteMuxOpenRequest.FromProfile(profile));
        Assert.True(outcome.Ok);
        Assert.Equal("agents", outcome.Path!.Session);
        Assert.Equal("user@dev", outcome.Path.Target);
        Assert.Equal("id1", outcome.Path.ProfileId);
        Assert.Single(ssh.Calls);
        Assert.Equal("user@dev", ssh.Calls[0].Target);
        Assert.Single(bridge.Starts);
        Assert.Equal("user@dev", bridge.Starts[0].Target);
        Assert.DoesNotContain(ssh.Commands, c => c.Contains("mux serve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_profile_fails_closed()
    {
        var adapter = CreateAdapter(new RecordingOpenSsh());
        Assert.True(PeerProfile.TryCreate(
            "id1", "lab", "host", "default", enabled: true, PeerProviders.Ssh, out var profile, out _));
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            Profile = profile with { Session = "bad session" },
        });
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.ProfileInvalid, outcome.Reason);
    }

    [Fact]
    public async Task Windows_remote_host_is_refused()
    {
        var ssh = new RecordingOpenSsh { Stdout = "Windows_NT\nx86_64\n---\nabsent\n" };
        var adapter = CreateAdapter(ssh);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "winhost",
            Session = "default",
            ManageSshConfig = false,
        });
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.PlatformUnsupported, outcome.Reason);
        Assert.Contains("Windows", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_uname_reports_platform_name()
    {
        var ssh = new RecordingOpenSsh { Stdout = "FreeBSD\nx86_64\n---\nabsent\n" };
        var adapter = CreateAdapter(ssh);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "bsdhost",
            Session = "default",
            ManageSshConfig = false,
        });
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.PlatformUnsupported, outcome.Reason);
        Assert.Contains("FreeBSD", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_reuses_running_server_and_assigns_generation()
    {
        var ssh = new RecordingOpenSsh { Stdout = CompatibleRunningProbe };
        var fence = new RemoteConnectionFence();
        var adapter = CreateAdapter(ssh, fence);
        var first = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "agents",
            ManageSshConfig = false,
        });
        Assert.True(first.Ok);
        Assert.False(first.Path!.StartedRemoteServer);
        Assert.True(fence.Accept(RemoteConnectionFence.EndpointKey("dev", "agents"), first.Path.Generation));

        var second = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "agents",
            ManageSshConfig = false,
        });
        Assert.True(second.Ok);
        Assert.Equal("agents", second.Path!.Session);
        Assert.NotEqual(first.Path.Generation, second.Path.Generation);
        Assert.False(fence.Accept(RemoteConnectionFence.EndpointKey("dev", "agents"), first.Path.Generation));
        Assert.True(fence.Accept(RemoteConnectionFence.EndpointKey("dev", "agents"), second.Path.Generation));
        Assert.DoesNotContain(ssh.Commands, c => c.Contains("mux serve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Absent_remote_socket_starts_mux_serve()
    {
        var ssh = new RecordingOpenSsh();
        var adapter = CreateAdapter(ssh);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "agents",
            ManageSshConfig = false,
        });
        Assert.True(outcome.Ok);
        Assert.True(outcome.Path!.StartedRemoteServer);
        Assert.Equal(2, ssh.Calls.Count);
        Assert.Contains("mux serve", ssh.Commands[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_without_compatible_ping_requires_restart()
    {
        var ssh = new RecordingOpenSsh { Stdout = "Linux\nx86_64\n---\nrunning\n" };
        var adapter = CreateAdapter(ssh);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "default",
            ManageSshConfig = false,
        });
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.HandoffRequired, outcome.Reason);
    }

    [Fact]
    public async Task Handoff_fails_closed_on_non_unix_client()
    {
        var adapter = CreateAdapter(new RecordingOpenSsh(), unixClient: false);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "default",
            LiveHandoff = true,
        });
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.HandoffUnsupported, outcome.Reason);
    }

    [Fact]
    public async Task Restart_without_consent_fails_closed()
    {
        var ssh = new RecordingOpenSsh { Stdout = IncompatibleRunningProbe };
        var adapter = CreateAdapter(ssh);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "default",
            ManageSshConfig = false,
        });
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.HandoffRequired, outcome.Reason);
        Assert.Contains("--handoff", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consented_live_handoff_replaces_incompatible_server()
    {
        var ssh = new RecordingOpenSsh { Stdout = IncompatibleRunningProbe };
        var adapter = CreateAdapter(ssh);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "default",
            ManageSshConfig = false,
            LiveHandoff = true,
            LiveHandoffEnabled = true,
            OperatorConsent = true,
        });
        Assert.True(outcome.Ok);
        Assert.True(outcome.Path!.RequestedLiveHandoff);
        Assert.Contains(ssh.Commands, c => c.Contains("mux live-handoff", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Host_key_failure_is_approval_required()
    {
        var ssh = new RecordingOpenSsh
        {
            ExitCode = 255,
            Stderr = "Host key verification failed.",
        };
        var adapter = CreateAdapter(ssh);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "default",
            ManageSshConfig = false,
        });
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.ApprovalRequired, outcome.Reason);
    }

    [Fact]
    public async Task Permission_denied_is_authentication_failed()
    {
        var ssh = new RecordingOpenSsh
        {
            ExitCode = 255,
            Stderr = "Permission denied (publickey).",
        };
        var adapter = CreateAdapter(ssh);
        var outcome = await adapter.OpenAsync(new RemoteMuxOpenRequest
        {
            ExplicitTarget = "dev",
            Session = "default",
            ManageSshConfig = false,
        });
        Assert.False(outcome.Ok);
        Assert.Equal(RemoteMuxReasons.AuthenticationFailed, outcome.Reason);
    }

    [Fact]
    public void Restart_policy_matches_herdr_enablement_and_request()
    {
        Assert.Equal(
            RemoteServerRestartPlan.KeepRunning,
            RemoteRestartPolicy.Decide(RemoteServerRestartReason.None, liveHandoff: true, liveHandoffEnabled: true));
        Assert.Equal(
            RemoteServerRestartPlan.LiveHandoff,
            RemoteRestartPolicy.Decide(RemoteServerRestartReason.EndpointProtocol, liveHandoff: true, liveHandoffEnabled: true));
        Assert.Equal(
            RemoteServerRestartPlan.StopRequired,
            RemoteRestartPolicy.Decide(RemoteServerRestartReason.EndpointProtocol, liveHandoff: false, liveHandoffEnabled: true));
        Assert.Equal(
            RemoteServerRestartPlan.StopRequired,
            RemoteRestartPolicy.Decide(RemoteServerRestartReason.HealthCheck, liveHandoff: true, liveHandoffEnabled: false));
        Assert.True(RemoteRestartPolicy.RequiresConsent(RemoteServerRestartPlan.StopRequired));
        Assert.False(RemoteRestartPolicy.LiveHandoffAllowed(unix: false, requested: true));
    }

    [Fact]
    public void Fence_rejects_stale_generation()
    {
        var fence = new RemoteConnectionFence();
        var first = fence.Assign("dev\u001fagents");
        var second = fence.Assign("dev\u001fagents");
        Assert.False(fence.Accept("dev\u001fagents", first));
        Assert.True(fence.Accept("dev\u001fagents", second));
        fence.Retire("dev\u001fagents", second);
        Assert.False(fence.Accept("dev\u001fagents", second));
    }

    [Fact]
    public void Stale_retire_does_not_drop_newer_generation()
    {
        var fence = new RemoteConnectionFence();
        var first = fence.Assign("dev\u001fagents");
        var second = fence.Assign("dev\u001fagents");
        fence.Retire("dev\u001fagents", first);
        Assert.True(fence.Accept("dev\u001fagents", second));
    }

    [Fact]
    public void Probe_parse_treats_missing_ping_as_incompatible()
    {
        var parsed = RemoteMuxProbe.Parse("Linux\nx86_64\n---\nrunning\n");
        Assert.True(parsed.Ok);
        Assert.True(parsed.ServerRunning);
        Assert.Equal(RemoteServerRestartReason.EndpointProtocol, parsed.RestartReason);
    }

    [Fact]
    public void Managed_ssh_config_includes_user_file_then_keepalive_fallbacks()
    {
        var user = Path.Combine(Path.GetTempPath(), "hypa-user-ssh-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(user, "Host *\n  ServerAliveInterval 99\n");
        try
        {
            using var managed = ManagedSshConfigWriter.Write(user, includeControlSocket: true);
            var text = File.ReadAllText(managed.ConfigPath);
            Assert.StartsWith("Include ", text, StringComparison.Ordinal);
            Assert.Contains("ServerAliveInterval 15", text, StringComparison.Ordinal);
            Assert.Contains("ServerAliveCountMax 4", text, StringComparison.Ordinal);
            Assert.NotNull(managed.ControlPath);
        }
        finally
        {
            File.Delete(user);
        }
    }

    [Fact]
    public void Managed_ssh_config_omits_control_socket_when_not_supported()
    {
        using var managed = ManagedSshConfigWriter.Write(includeControlSocket: false);
        Assert.Null(managed.ControlPath);
        var text = File.ReadAllText(managed.ConfigPath);
        Assert.Contains("ServerAliveInterval 15", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlPath", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ssh_args_do_not_include_identity_files_or_passwords()
    {
        var args = OpenSshArgumentBuilder.Build(new OpenSshProcessRequest
        {
            Target = "user@host",
            RemoteCommand = "true",
            ConfigPath = "/tmp/cfg",
            ControlPath = "/tmp/ctl",
            BatchMode = true,
        });
        Assert.DoesNotContain(args, a => a.Contains("IdentityFile", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, a => a.Contains("Identity=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, a => a.Contains("PasswordAuthentication", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-F", args);
        Assert.Contains("user@host", args);
    }

    [Fact]
    public void Remote_attach_strips_local_custom_commands()
    {
        var config = Hypa.AgentRuntime.Domain.AttachConfig.AttachClientConfig.Default with
        {
            Keys = Hypa.AgentRuntime.Domain.AttachConfig.AttachKeysConfig.Default with
            {
                Commands =
                [
                    new Hypa.AgentRuntime.Domain.AttachConfig.AttachKeyCommandConfig
                    {
                        Command = "echo secret",
                    },
                ],
            },
        };
        var local = MuxAttachService.ForRemoteAttach(config, RemoteKeybindingsMode.Local);
        var server = MuxAttachService.ForRemoteAttach(config, RemoteKeybindingsMode.Server);
        Assert.Empty(local.Keys.Commands);
        Assert.Empty(server.Keys.Commands);
    }

    [Fact]
    public void Endpoint_kind_is_ssh_and_creates_control_plane_client()
    {
        var path = new RemoteMuxPath
        {
            LocalSocketPath = "/tmp/hypa-remote-client.sock",
            Session = "agents",
            Target = "dev",
            Generation = 2,
        };
        var endpoint = new SshAttachEndpoint(path);
        Assert.Equal(AttachEndpointKinds.Ssh, endpoint.Kind);
        Assert.Equal(2ul, endpoint.Generation);
        var client = endpoint.CreateClient();
        Assert.NotNull(client);
    }

    [Fact]
    public void Bridge_directory_paths_are_unique_per_attempt()
    {
        using var first = RemoteBridgeDirectory.Create();
        using var second = RemoteBridgeDirectory.Create();
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.NotEqual(first.SocketPath, second.SocketPath);
        Assert.Contains("hypa-ssh-bridge-", first.DirectoryPath, StringComparison.Ordinal);
    }
    [Fact]
    public void Bridge_command_uses_remote_client_bridge_subcommand()
    {
        Assert.Equal("exec hypa remote-client-bridge", RemoteMuxCommands.RemoteBridgeCommand("default"));
        Assert.Contains(
            "remote-client-bridge --session 'agents'",
            RemoteMuxCommands.RemoteBridgeCommand("agents"),
            StringComparison.Ordinal);
    }

    private static OpenSshRemoteMuxAdapter CreateAdapter(
        RecordingOpenSsh ssh,
        RemoteConnectionFence? fence = null,
        StubBridgeFactory? bridge = null,
        bool unixClient = true) =>
        new(
            ssh,
            fence ?? new RemoteConnectionFence(),
            bridge ?? new StubBridgeFactory(),
            unixClient);

    private sealed class RecordingOpenSsh : IOpenSshProcess
    {
        public string Stdout { get; set; } = "Linux\nx86_64\n---\nabsent\n";
        public int ExitCode { get; set; }
        public string Stderr { get; set; } = "";
        public List<string> Commands { get; } = [];
        public List<OpenSshProcessRequest> Calls { get; } = [];

        public Task<OpenSshProcessResult> RunAsync(
            OpenSshProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            Commands.Add(request.RemoteCommand);
            return Task.FromResult(new OpenSshProcessResult
            {
                ExitCode = ExitCode,
                Stdout = Stdout,
                Stderr = Stderr,
            });
        }
    }

    private sealed class StubBridgeFactory : ISshStdioBridgeFactory
    {
        public List<SshStdioBridgeStartRequest> Starts { get; } = [];

        public ISshStdioBridge Start(SshStdioBridgeStartRequest request)
        {
            Starts.Add(request);
            return new FakeBridge(request.LocalSocketPath);
        }

        private sealed class FakeBridge(string path) : ISshStdioBridge
        {
            public string LocalSocketPath => path;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
