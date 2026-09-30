using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentIntelligence.Detection;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Occupants;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Canonical kinds, aliases, process detect, and occupant start recipes.
/// </summary>
public class AgentKindCatalogTests
{
    public static TheoryData<string, string> CanonicalLabels()
    {
        var data = new TheoryData<string, string>();
        foreach (var id in AgentKindCatalog.All)
            data.Add(id, id);
        return data;
    }

    public static TheoryData<string, string> KeyAliases() =>
        new()
        {
            { "muse-bin-0.1.0-R708.1", "muse" },
            { "muse-bin-1.2.3", "muse" },
            { "/home/user/.local/bin/muse-bin-0.2.1-R1215.1", "muse" },
            { @"C:\Users\user\muse-bin-0.2.1-R1215.1.exe", "muse" },
            { "muse-code", "muse" },
            { "muse-cli", "muse" },
            { "github-copilot", "copilot" },
            { "ghcs", "copilot" },
            { "antigravity", "agy" },
            { "antigravity-cli", "agy" },
            { "mastra-code", "mastracode" },
            { "mastra code", "mastracode" },
            { "kimi-code", "kimi" },
            { "kimi code", "kimi" },
            { "kilo-code", "kilo" },
            { "kilo code", "kilo" },
            { "cursor-agent", "cursor" },
            { "kiro-cli", "kiro" },
            { "opencode.exe", "opencode" },
            { "claude-code", "claude" },
            { "devin-cli", "devin" },
            { "devin cli", "devin" },
            { "amp-local", "amp" },
            { "grok-build", "grok" },
            { "hermes-agent", "hermes" },
            { "qwen-code", "qwen" },
            { "qwen code", "qwen" },
        };

    [Fact]
    public void All_has_twenty_three_canonical_labels()
    {
        Assert.Equal(23, AgentKindCatalog.All.Length);
        Assert.Equal(AgentKindCatalog.All.Length, AgentKindCatalog.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Screen_manifest_ids_are_twenty_one_without_omp_or_mastracode()
    {
        Assert.Equal(21, AgentKindCatalog.ScreenManifestIds.Length);
        Assert.Equal(21, ScreenAgentCatalog.Ids.Length);
        Assert.DoesNotContain("omp", AgentKindCatalog.ScreenManifestIds);
        Assert.DoesNotContain("mastracode", AgentKindCatalog.ScreenManifestIds);
        Assert.True(AgentKindCatalog.IsProcessOnly("omp"));
        Assert.True(AgentKindCatalog.IsProcessOnly("mastracode"));
        Assert.False(AgentKindCatalog.IsScreenManifest("omp"));
        Assert.False(AgentKindCatalog.IsScreenManifest("mastracode"));
        Assert.True(AgentKindCatalog.IsScreenManifest("muse"));
        Assert.True(AgentKindCatalog.IsScreenManifest("copilot"));
        Assert.True(AgentKindCatalog.IsScreenManifest("agy"));
    }

    [Fact]
    public void Bundled_screen_toml_omits_omp_and_mastracode()
    {
        var names = typeof(HeuristicAgentDetector).Assembly.GetManifestResourceNames();
        var detection = names
            .Where(n => n.Contains(".Resources.agent-detection.", StringComparison.Ordinal)
                        && n.EndsWith(".toml", StringComparison.Ordinal))
            .Select(n => n[(n.LastIndexOf(".agent-detection.", StringComparison.Ordinal)
                            + ".agent-detection.".Length)..^".toml".Length])
            .ToArray();
        Assert.Equal(21, detection.Length);
        Assert.DoesNotContain("omp", detection);
        Assert.DoesNotContain("mastracode", detection);
        foreach (var id in AgentKindCatalog.ScreenManifestIds)
            Assert.Contains(id, detection);
    }

    [Theory]
    [MemberData(nameof(CanonicalLabels))]
    public void TryResolve_maps_each_canonical_label(string input, string expected)
    {
        Assert.True(AgentKindCatalog.TryResolve(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(KeyAliases))]
    public void TryResolve_maps_aliases_to_canonical_ids(string input, string expected)
    {
        Assert.True(AgentKindCatalog.TryResolve(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-agent")]
    [InlineData("muse-bin")]
    [InlineData("muse-binary")]
    [InlineData("python")]
    [InlineData("fake")]
    public void DetectKind_refuses_unknown(string? input)
    {
        Assert.False(AgentKindCatalog.TryResolve(input, out _));
    }

    [Theory]
    [InlineData("not-an-agent")]
    [InlineData("muse-binary")]
    [InlineData("python")]
    public void Occupant_registry_refuses_unknown_kind(string input)
    {
        Assert.False(BundledOccupantManifestRegistry.Default.TryGet(input, out _));
    }

    [Fact]
    public void Interactive_executables_match_herdr()
    {
        Assert.Equal("agy", AgentKindCatalog.InteractiveExecutable("agy"));
        Assert.Equal("omp", AgentKindCatalog.InteractiveExecutable("omp"));
        Assert.Equal("mastracode", AgentKindCatalog.InteractiveExecutable("mastracode"));
        Assert.Equal("kiro-cli", AgentKindCatalog.InteractiveExecutable("kiro"));
        var cursor = AgentKindCatalog.InteractiveExecutable("cursor");
        Assert.Equal(OperatingSystem.IsWindows() ? "cursor-agent.cmd" : "cursor-agent", cursor);
    }

    [Theory]
    [MemberData(nameof(CanonicalLabels))]
    public void Occupant_registry_has_each_kind(string id, string expected)
    {
        Assert.True(BundledOccupantManifestRegistry.Default.TryGet(id, out var manifest));
        Assert.NotNull(manifest);
        Assert.Equal(expected, manifest.Id);
        Assert.Equal(AgentKindCatalog.InteractiveExecutable(id), manifest.Command[0]);
        Assert.Contains("HYPA_CUBE_HOME", manifest.Env.Keys);
        Assert.DoesNotContain(manifest.Env.Keys, k => k.StartsWith("HERDR_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("antigravity", "agy")]
    [InlineData("github-copilot", "copilot")]
    [InlineData("mastra-code", "mastracode")]
    [InlineData("muse-bin-0.1.0-R708.1", "muse")]
    public void Occupant_registry_resolves_aliases(string input, string expected)
    {
        Assert.True(BundledOccupantManifestRegistry.Default.TryGet(input, out var manifest));
        Assert.NotNull(manifest);
        Assert.Equal(expected, manifest.Id);
    }

    [Fact]
    public void Occupant_registry_keeps_pi_resume_env_and_fake()
    {
        Assert.True(BundledOccupantManifestRegistry.Default.TryGet("pi", out var pi));
        Assert.NotNull(pi);
        Assert.Contains("HYPA_RESUME_ATTEMPT_ID", pi.Env.Keys);
        Assert.Contains("HYPA_RESUME_REPORT_PATH", pi.Env.Keys);
        Assert.True(BundledOccupantManifestRegistry.Default.TryGet("fake", out var fake));
        Assert.NotNull(fake);
        Assert.Equal("fake", fake.Id);
    }

    [Fact]
    public void Detect_sets_kind_from_process_for_omp_and_mastracode()
    {
        var detector = new HeuristicAgentDetector();
        var omp = detector.Detect("$ ", "omp");
        Assert.Equal("omp", omp.AgentKind);
        Assert.NotEqual("bundled", omp.ManifestSourceKind);

        var mastra = detector.Detect("$ ", "mastra-code");
        Assert.Equal("mastracode", mastra.AgentKind);
        Assert.NotEqual("bundled", mastra.ManifestSourceKind);
    }

    [Fact]
    public void Detect_refuses_unknown_process_name()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("ordinary assistant text", "not-an-agent");
        Assert.Null(result.AgentKind);
    }

    [Fact]
    public void Detect_muse_versioned_binary_uses_screen_manifest()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("⟩ hello\n", "muse-bin-0.1.0-R708.1");
        Assert.Equal("muse", result.AgentKind);
        Assert.Equal("bundled", result.ManifestSourceKind);
    }
}

/// <summary>
// / agent.start --kind.
/// </summary>
public class AgentStartKindTests
{
    [Fact]
    public void Cli_kind_muse_sends_kind_without_occupant()
    {
        var parsed = ControlPlaneCliCommands.Parse(["agent", "start", "p9", "--kind", "muse"]);
        var obj = ControlPlaneCliCommands.BuildAgentStartParams(parsed, ["start", "p9"]);
        Assert.Equal("p9", obj["pane_id"]!.GetValue<string>());
        Assert.Equal("muse", obj["kind"]!.GetValue<string>());
        Assert.False(obj.ContainsKey("occupant"));
        Assert.False(obj.ContainsKey("command"));
    }

    [Fact]
    public void Cli_kind_omp_and_antigravity_alias()
    {
        var omp = ControlPlaneCliCommands.BuildAgentStartParams(
            ControlPlaneCliCommands.Parse(["agent", "start", "p1", "--kind", "omp"]),
            ["start", "p1"]);
        Assert.Equal("omp", omp["kind"]!.GetValue<string>());

        var agy = ControlPlaneCliCommands.BuildAgentStartParams(
            ControlPlaneCliCommands.Parse(["agent", "start", "p1", "--kind", "antigravity"]),
            ["start", "p1"]);
        Assert.Equal("antigravity", agy["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Cli_unknown_kind_fails_closed()
    {
        var parsed = ControlPlaneCliCommands.Parse(["agent", "start", "p1", "--kind", "not-an-agent"]);
        var ex = Assert.Throws<ControlPlaneCliUsageException>(
            () => ControlPlaneCliCommands.BuildAgentStartParams(parsed, ["start", "p1"]));
        Assert.Equal(AgentKindCatalog.UnsupportedKindMessage("not-an-agent"), ex.Message);
    }

    [Fact]
    public void Cli_blank_kind_fails_closed()
    {
        var space = ControlPlaneCliCommands.Parse(["agent", "start", "p1", "--kind", " "]);
        var spaceEx = Assert.Throws<ControlPlaneCliUsageException>(
            () => ControlPlaneCliCommands.BuildAgentStartParams(space, ["start", "p1"]));
        Assert.Equal(AgentKindCatalog.UnsupportedKindMessage(" "), spaceEx.Message);

        var empty = ControlPlaneCliCommands.Parse(["agent", "start", "p1", "--kind", ""]);
        var emptyEx = Assert.Throws<ControlPlaneCliUsageException>(
            () => ControlPlaneCliCommands.BuildAgentStartParams(empty, ["start", "p1"]));
        Assert.Equal(AgentKindCatalog.UnsupportedKindMessage(""), emptyEx.Message);
    }

    [Fact]
    public void Cli_kind_and_occupant_are_mutually_exclusive()
    {
        var parsed = ControlPlaneCliCommands.Parse(
            ["agent", "start", "p1", "--kind", "muse", "--occupant", "pi"]);
        var ex = Assert.Throws<ControlPlaneCliUsageException>(
            () => ControlPlaneCliCommands.BuildAgentStartParams(parsed, ["start", "p1"]));
        Assert.Contains("mutually exclusive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_occupant_still_parses()
    {
        var parsed = ControlPlaneCliCommands.Parse(["agent", "start", "p9", "--occupant", "pi"]);
        var obj = ControlPlaneCliCommands.BuildAgentStartParams(parsed, ["start", "p9"]);
        Assert.Equal("pi", obj["occupant"]!.GetValue<string>());
        Assert.False(obj.ContainsKey("kind"));
    }

    [Fact]
    public async Task Start_kind_muse_spawns_muse_occupant()
    {
        var (cp, factory, paneId) = await ReadyPaneAsync("start-kind-muse");
        try
        {
            var result = await cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["kind"] = "muse",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.Equal("muse", result.GetProperty("occupant").GetString());
            Assert.Equal("muse", factory.LastOptions!.Command);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_kind_omp_spawns_omp_occupant()
    {
        var (cp, factory, paneId) = await ReadyPaneAsync("start-kind-omp");
        try
        {
            var result = await cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["kind"] = "omp",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.Equal("omp", result.GetProperty("occupant").GetString());
            Assert.Equal("omp", factory.LastOptions!.Command);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_kind_antigravity_canonicalizes_to_agy()
    {
        var (cp, factory, paneId) = await ReadyPaneAsync("start-kind-agy");
        try
        {
            var result = await cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["kind"] = "antigravity",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.Equal("agy", result.GetProperty("occupant").GetString());
            Assert.Equal("agy", factory.LastOptions!.Command);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_unknown_kind_fails_closed()
    {
        var (cp, _, paneId) = await ReadyPaneAsync("start-kind-unknown");
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.AgentStart,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["kind"] = "not-an-agent",
                    }.ToJsonString()).RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Equal(AgentKindCatalog.UnsupportedKindMessage("not-an-agent"), ex.Message);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_unknown_occupant_fails_closed()
    {
        var (cp, _, paneId) = await ReadyPaneAsync("start-occupant-unknown");
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.AgentStart,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["occupant"] = "not-an-agent",
                    }.ToJsonString()).RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.NotFound, ex.Code);
            Assert.Contains("not-an-agent", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_whitespace_kind_fails_closed_without_replacement()
    {
        var (cp, factory, paneId) = await ReadyPaneAsync("start-kind-whitespace");
        try
        {
            await AssertStartFailsClosedWithoutReplacementAsync(
                cp,
                factory,
                paneId,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["kind"] = " ",
                },
                AgentKindCatalog.UnsupportedKindMessage(" "));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_empty_kind_fails_closed_without_replacement()
    {
        var (cp, factory, paneId) = await ReadyPaneAsync("start-kind-empty");
        try
        {
            await AssertStartFailsClosedWithoutReplacementAsync(
                cp,
                factory,
                paneId,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["kind"] = "",
                },
                AgentKindCatalog.UnsupportedKindMessage(""));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_omitted_kind_restarts_existing_command()
    {
        var (cp, factory, paneId) = await ReadyPaneAsync("start-kind-omitted");
        try
        {
            Assert.Equal("/bin/sh", factory.LastOptions!.Command);
            Assert.Single(factory.Options);
            var result = await cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.Equal("/bin/sh", result.GetProperty("command").GetString());
            Assert.Equal(2, factory.Options.Count);
            Assert.Equal("/bin/sh", factory.LastOptions!.Command);
            Assert.Equal("/bin/sh", await PaneCommandAsync(cp, paneId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_blank_kind_with_occupant_is_mutually_exclusive()
    {
        var (cp, factory, paneId) = await ReadyPaneAsync("start-kind-blank-occupant");
        try
        {
            await AssertStartFailsClosedWithoutReplacementAsync(
                cp,
                factory,
                paneId,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["kind"] = " ",
                    ["occupant"] = "pi",
                },
                "kind and occupant are mutually exclusive");
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_blank_kind_with_command_is_mutually_exclusive()
    {
        var (cp, factory, paneId) = await ReadyPaneAsync("start-kind-blank-command");
        try
        {
            await AssertStartFailsClosedWithoutReplacementAsync(
                cp,
                factory,
                paneId,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["kind"] = "",
                    ["command"] = "/bin/echo",
                },
                "kind and command are mutually exclusive");
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static async Task AssertStartFailsClosedWithoutReplacementAsync(
        ControlPlaneService cp,
        TestPaneFactories.CapturingPaneFactory factory,
        string paneId,
        JsonObject startParams,
        string expectedMessage)
    {
        var spawnCount = factory.Options.Count;
        var lastCommand = factory.LastOptions!.Command;
        var paneCommand = await PaneCommandAsync(cp, paneId);

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(startParams.ToJsonString()).RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
        Assert.Equal(expectedMessage, ex.Message);
        Assert.Equal(spawnCount, factory.Options.Count);
        Assert.Equal(lastCommand, factory.LastOptions!.Command);
        Assert.Equal(paneCommand, await PaneCommandAsync(cp, paneId));
    }

    private static async Task<string?> PaneCommandAsync(ControlPlaneService cp, string paneId)
    {
        var pane = await cp.DispatchAsync(
            ProtocolMethods.PaneGet,
            JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
            CancellationToken.None);
        return pane.GetProperty("command").GetString();
    }

    private static async Task<(ControlPlaneService Cp, TestPaneFactories.CapturingPaneFactory Factory, string PaneId)>
        ReadyPaneAsync(string session)
    {
        var state = new AppState(SessionId.New(session));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Capturing();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(state, factory, intel, new HeuristicAgentDetector());
        await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["command"] = "/bin/sh",
                ["create_pane"] = true,
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var paneId = factory.LastOptions!.Id.Value;
        return (cp, factory, paneId);
    }
}
