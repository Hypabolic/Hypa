using System.Text.Json;
using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class CheckpointArtifactJsonTests
{
    private static readonly Regex CamelKey = new(
        """["']([a-z]+[A-Z][A-Za-z0-9]*)["']\s*:""",
        RegexOptions.Compiled);

    [Fact]
    public void Metadata_and_git_artifacts_round_trip_source_generated_snake_case()
    {
        var metadata = new CheckpointMetadataArtifactDto
        {
            CheckpointId = "ckpt_1",
            RuntimeSessionId = "rs_1",
            SessionName = "export",
            Placement = "local",
            PlacementGeneration = 2,
            SessionFingerprint = "fp",
            BarrierSeq = 9,
            Binding = new AtomicBindingStorageDto
            {
                RunId = "run_1",
                ProjectRoot = "/ws",
            },
            Governed = true,
            ProtocolVersion = 1,
            ReplayComplete = true,
            Workspaces =
            [
                new CheckpointWorkspaceArtifactDto
                {
                    WorkspaceId = "ws_1",
                    Label = "default",
                    Cwd = "/ws",
                },
            ],
            Panes =
            [
                new CheckpointPaneArtifactDto
                {
                    PaneId = "p_1",
                    WorkspaceId = "ws_1",
                    TabId = "t_1",
                    Label = "shell",
                    Cwd = "/ws",
                    Command = "echo",
                    LifecycleState = PaneLifecycle.Running,
                    OccupantGeneration = 1,
                },
            ],
        };

        var metaJson = JsonSerializer.Serialize(
            metadata, RuntimeStorageJsonContext.Default.CheckpointMetadataArtifactDto);
        AssertNoCamelCaseKeys(metaJson);
        Assert.Contains("\"checkpoint_id\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"runtime_session_id\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"session_fingerprint\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"barrier_seq\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"protocol_version\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"replay_complete\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"workspace_id\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"lifecycle_state\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"occupant_generation\"", metaJson, StringComparison.Ordinal);
        Assert.Contains("\"project_root\"", metaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpointId", metaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ToJsonString", metaJson, StringComparison.Ordinal);

        var metaRound = JsonSerializer.Deserialize(
            metaJson, RuntimeStorageJsonContext.Default.CheckpointMetadataArtifactDto);
        Assert.NotNull(metaRound);
        Assert.Equal(metadata.CheckpointId, metaRound!.CheckpointId);
        Assert.Equal(metadata.RuntimeSessionId, metaRound.RuntimeSessionId);
        Assert.Equal("/ws", metaRound.Binding!.ProjectRoot);
        Assert.Equal("ws_1", metaRound.Workspaces.Single().WorkspaceId);
        Assert.Equal("p_1", metaRound.Panes.Single().PaneId);

        var git = new CheckpointGitMetaArtifactDto
        {
            Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Dirty = false,
            Incomplete = false,
        };
        var gitJson = JsonSerializer.Serialize(
            git, RuntimeStorageJsonContext.Default.CheckpointGitMetaArtifactDto);
        AssertNoCamelCaseKeys(gitJson);
        Assert.Contains("\"head\"", gitJson, StringComparison.Ordinal);
        Assert.Contains("\"dirty\"", gitJson, StringComparison.Ordinal);
        Assert.Contains("\"incomplete\"", gitJson, StringComparison.Ordinal);

        var gitRound = JsonSerializer.Deserialize(
            gitJson, RuntimeStorageJsonContext.Default.CheckpointGitMetaArtifactDto);
        Assert.NotNull(gitRound);
        Assert.Equal(git.Head, gitRound!.Head);
        Assert.False(gitRound.Dirty);
        Assert.False(gitRound.Incomplete);

        var discarded = JsonSerializer.Serialize(
            new CheckpointGitMetaArtifactDto { Head = null, Dirty = null, Incomplete = true },
            RuntimeStorageJsonContext.Default.CheckpointGitMetaArtifactDto);
        using var discardedDoc = JsonDocument.Parse(discarded);
        Assert.True(discardedDoc.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, discardedDoc.RootElement.GetProperty("head").ValueKind);
        Assert.Equal(JsonValueKind.Null, discardedDoc.RootElement.GetProperty("dirty").ValueKind);
    }


    [Fact]
    public async Task Checkpoint_builder_writes_source_generated_metadata_and_git_artifacts()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "hypa-h19-ckpt-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = Path.Combine(root, "ws");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(Path.Combine(workspace, "readme.txt"), "ok");

            const string head = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var builder = new DefaultCheckpointArtifactBuilder(
                new CompleteGitWorkspaceProbe(head, dirty: true));
            Assert.True(builder.TryPinProjectRoot(
                workspace, out var device, out var inode, out var wasSymlink));

            var wsId = new WorkspaceId("ws_json");
            var tabId = new TabId("t_json");
            var paneId = new PaneId("p_json");
            var session = new SessionState
            {
                Id = SessionId.New("h19json"),
                Name = "h19json",
                Placement = "local",
                PlacementGeneration = 2,
                Binding = new AtomicBinding { ProjectRoot = workspace, RunId = "run_json" },
                Governed = true,
                ProtocolVersion = 1,
                ReplayComplete = true,
                Workspaces = new Dictionary<string, WorkspaceState>
                {
                    [wsId.Value] = new WorkspaceState
                    {
                        Id = wsId,
                        Label = "default",
                        Cwd = workspace,
                        Binding = new AtomicBinding { ProjectRoot = workspace },
                    },
                },
                Tabs = new Dictionary<string, TabState>
                {
                    [tabId.Value] = new TabState
                    {
                        Id = tabId,
                        WorkspaceId = wsId,
                        Label = "main",
                        PaneIds = [paneId],
                    },
                },
                Panes = new Dictionary<string, PaneState>
                {
                    [paneId.Value] = new PaneState
                    {
                        Id = paneId,
                        WorkspaceId = wsId,
                        TabId = tabId,
                        Label = "shell",
                        Cwd = workspace,
                        Command = "echo",
                        LifecycleState = PaneLifecycle.Running,
                        OccupantGeneration = 1,
                    },
                },
            };

            var checkpointDir = Path.Combine(root, "checkpoints", "ckpt_json");
            var built = await builder.BuildAsync(new CheckpointArtifactBuildRequest
            {
                Record = new CheckpointRecord
                {
                    CheckpointId = "ckpt_json",
                    SessionId = session.Id.Value,
                    State = CheckpointStates.Prepared,
                    BarrierSeq = 9,
                    NextSeqAtPrepare = 10,
                    SessionFingerprint = "fp_json",
                    IncludeWorkspaceFiles = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ProjectRootDevice = device,
                    ProjectRootInode = inode,
                    ProjectRootWasSymlink = wasSymlink,
                },
                Session = session,
                CheckpointDirectory = checkpointDir,
            });
            Assert.True(built.IsOk, built.IsOk ? null : built.Error.Message);

            var metaPath = Path.Combine(checkpointDir, "artifacts", "metadata.json");
            var gitPath = Path.Combine(checkpointDir, "artifacts", "git_meta.json");
            Assert.True(File.Exists(metaPath));
            Assert.True(File.Exists(gitPath));

            var metaJson = await File.ReadAllTextAsync(metaPath);
            var gitJson = await File.ReadAllTextAsync(gitPath);
            AssertNoCamelCaseKeys(metaJson);
            AssertNoCamelCaseKeys(gitJson);
            Assert.Contains("\"checkpoint_id\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"runtime_session_id\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"session_fingerprint\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"barrier_seq\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"protocol_version\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"replay_complete\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"workspace_id\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"lifecycle_state\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"occupant_generation\"", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"project_root\"", metaJson, StringComparison.Ordinal);
            Assert.DoesNotContain("checkpointId", metaJson, StringComparison.Ordinal);
            Assert.DoesNotContain("ToJsonString", metaJson, StringComparison.Ordinal);
            Assert.Contains("\"head\"", gitJson, StringComparison.Ordinal);
            Assert.Contains("\"dirty\"", gitJson, StringComparison.Ordinal);
            Assert.Contains("\"incomplete\"", gitJson, StringComparison.Ordinal);

            var meta = JsonSerializer.Deserialize(
                metaJson, RuntimeStorageJsonContext.Default.CheckpointMetadataArtifactDto);
            Assert.NotNull(meta);
            Assert.Equal("ckpt_json", meta!.CheckpointId);
            Assert.Equal(session.Id.Value, meta.RuntimeSessionId);
            Assert.Equal("h19json", meta.SessionName);
            Assert.Equal("fp_json", meta.SessionFingerprint);
            Assert.Equal(9, meta.BarrierSeq);
            Assert.Equal(2, meta.PlacementGeneration);
            Assert.True(meta.Governed);
            Assert.Equal(workspace, meta.Binding!.ProjectRoot);
            Assert.Equal("run_json", meta.Binding.RunId);
            Assert.Equal("ws_json", meta.Workspaces.Single().WorkspaceId);
            Assert.Equal("p_json", meta.Panes.Single().PaneId);
            Assert.Equal(PaneLifecycle.Running, meta.Panes.Single().LifecycleState);
            Assert.Equal(1, meta.Panes.Single().OccupantGeneration);

            var git = JsonSerializer.Deserialize(
                gitJson, RuntimeStorageJsonContext.Default.CheckpointGitMetaArtifactDto);
            Assert.NotNull(git);
            Assert.Equal(head, git!.Head);
            Assert.True(git.Dirty);
            Assert.False(git.Incomplete);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }


    private static int FindMatchingParen(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '(')
                depth++;
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static void AssertNoCamelCaseKeys(string json)
    {
        var match = CamelKey.Match(json);
        Assert.False(match.Success, "CamelCase key found in JSON: " + match.Value + " full=" + json);
    }

}
