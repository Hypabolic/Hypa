using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Worktrees;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class WorktreeDialogModelTests
{
    [Fact]
    public void Create_ReplacesDefaultBranchAndBuildsPayload()
    {
        var dialog = new WorktreeDialogModel();
        dialog.ShowCreate("ws-parent", "repo", "/tmp/hypa-worktrees", seed: 0);
        Assert.Equal("worktree/brave-river-0000", dialog.Create!.Branch);
        Assert.Equal("/tmp/hypa-worktrees/repo/worktree-brave-river-0000", dialog.Create.CheckoutPath);
        Assert.True(dialog.InsertText("feature/a"));
        Assert.Equal("feature/a", dialog.Create.Branch);
        Assert.Equal("/tmp/hypa-worktrees/repo/feature-a", dialog.Create.CheckoutPath);
        Assert.True(dialog.TryBeginCreate(out var create));
        Assert.Equal("ws-parent", create!.WorkspaceId);
        Assert.Equal("feature/a", create.Branch);
        Assert.Equal("HEAD", create.Base);
        Assert.True(create.Focus);
        Assert.False(dialog.TryBeginCreate(out _));
        Assert.True(dialog.Create.Creating);
        Assert.False(dialog.Cancel());
    }

    [Fact]
    public void Open_FiltersAndSelectsStableEntries()
    {
        var dialog = new WorktreeDialogModel();
        dialog.ShowOpen("ws-parent",
        [
            Info("/repo", "main", linked: false, openId: "ws-parent"),
            Info("/repo-feat", "worktree/feat", linked: true),
            Info("/repo-bare", "bare", bare: true),
        ]);
        Assert.Equal(2, dialog.Open!.Entries.Count);
        dialog.FocusOpenSearch();
        dialog.InsertText("feat");
        Assert.Equal([1], dialog.Open.FilteredIndices().ToArray());
        Assert.Equal(1, dialog.Open.Selected);
        Assert.True(dialog.TryBeginOpen(out var open));
        Assert.Equal("ws-parent", open!.WorkspaceId);
        Assert.Equal("/repo-feat", open.Path);
        Assert.True(open.Focus);
        Assert.False(dialog.TryBeginOpen(out _));
    }

    [Fact]
    public void Remove_DirtyRefusalNeedsFreshForceConfirm()
    {
        var dialog = new WorktreeDialogModel();
        dialog.ShowRemove("ws-child", [Info("/repo-feat", "worktree/feat", linked: true, openId: "ws-child")]);
        Assert.True(dialog.TryBeginRemove(out var first));
        Assert.False(first!.Force);
        dialog.ApplyRemoveError(WorktreeDialogModel.DirtyRequiresForce, "dirty");
        Assert.True(dialog.Remove!.ForceConfirmation);
        Assert.Null(dialog.Remove.Error);
        Assert.False(dialog.Remove.Removing);
        Assert.True(dialog.Cancel());
        Assert.False(dialog.IsOpen);

        dialog.ShowRemove("ws-child", [Info("/repo-feat", "worktree/feat", linked: true, openId: "ws-child")]);
        dialog.TryBeginRemove(out _);
        dialog.ApplyRemoveError(WorktreeDialogModel.DirtyRequiresForce, "dirty");
        Assert.True(dialog.TryBeginRemove(out var forced));
        Assert.True(forced!.Force);
        Assert.False(dialog.TryBeginRemove(out _));
        dialog.ApplyRemoveError("worktree_remove_failed", "other");
        Assert.Equal("other", dialog.Remove!.Error);
        Assert.True(dialog.Remove.ForceConfirmation);
    }

    [Fact]
    public void Painter_UsesHostCellsAndClipsUnicode()
    {
        var dialog = new WorktreeDialogModel();
        dialog.ShowCreate("ws-parent", "日本語", "/tmp/hypa-worktrees", seed: 0);
        dialog.InsertText("漢字/branch");
        var wide = WorktreeDialogPainter.Paint(dialog, 80, 24);
        Assert.Contains("new worktree", wide, StringComparison.Ordinal);
        Assert.Contains("漢字/branch", wide, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[", wide, StringComparison.Ordinal);

        var narrow = WorktreeDialogPainter.Paint(dialog, 20, 8);
        Assert.Contains("new worktree", narrow, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[", narrow, StringComparison.Ordinal);
        foreach (var line in narrow.Split('\n'))
            Assert.True(line.Length <= 20);
    }

    private static WorktreeInfo Info(
        string path,
        string branch,
        bool linked = false,
        bool bare = false,
        string? openId = null) =>
        new()
        {
            Path = path,
            Branch = branch,
            Label = branch,
            IsLinkedWorktree = linked,
            IsBare = bare,
            OpenWorkspaceId = openId,
        };
}
