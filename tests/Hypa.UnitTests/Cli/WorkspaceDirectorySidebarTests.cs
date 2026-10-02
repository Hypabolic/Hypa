using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class WorkspaceDirectorySidebarTests
{
    [Fact]
    public void Remote_host_directory_and_git_metadata_override_local_seed_and_cache()
    {
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            workspaces = new[] { new { workspace_id = "remote-ws", label = "remote-repo", cwd = "/seed",
                resolved_cwd = "/remote/repo/nested", branch = "remote-branch", git_status = "*",
                repository_name = "remote-repo", custom_label = false } },
        });
        var input = SidebarLiveModel.FromSnapshot(snapshot, new AttachUiConfig(), true, 30,
            git: new MapSidebarGitStatus(new Dictionary<string, SidebarGitInfo>
            {
                ["/seed"] = new("wrong-seed-branch"),
                ["/remote/repo/nested"] = new("wrong-local-branch"),
            }));
        var workspace = Assert.Single(input.Workspaces);
        Assert.Equal("/remote/repo/nested", workspace.Cwd);
        Assert.Equal("remote-repo", workspace.Label);
        Assert.Equal("remote-branch", workspace.Git!.Branch);
        Assert.Equal("*", workspace.Git.Status);
        var section = SidebarSectionComposer.ResolveSections(input.Ui)
            .Single(section => section.Id == SidebarTokenGrammar.SpacesId);
        var view = new SpacesChromeSectionStrategy().Compose(input, section, false, true, 40,
            SidebarCollapseDisplay.Expanded);
        Assert.Contains(view.Rows, row => row.Label.Contains("remote-branch", StringComparison.Ordinal));
        Assert.DoesNotContain(view.Rows, row => row.Label.Contains("wrong-", StringComparison.Ordinal));
    }

    [Fact]
    public void Old_host_without_directory_metadata_keeps_client_probe_compatibility()
    {
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            workspaces = new[] { new { workspace_id = "old-ws", label = "Old", cwd = "/old" } },
        });
        var input = SidebarLiveModel.FromSnapshot(snapshot, new AttachUiConfig(), true, 30);
        var workspace = Assert.Single(input.Workspaces);
        Assert.Equal("/old", workspace.Cwd);
        Assert.Null(workspace.Git);
    }
}
