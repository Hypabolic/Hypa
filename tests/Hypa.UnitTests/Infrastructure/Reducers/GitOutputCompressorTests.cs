using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Reducers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Reducers;

public sealed class GitOutputCompressorTests
{
    private static GitOutputCompressor Make() => new(new CharDivTokenCounter());

    private static CommandInvocation GitInvocation(params string[] args) =>
        CommandInvocation.Buffered("git", args, $"git {string.Join(' ', args)}");

    private static CommandOutput Out(string stdout, int exitCode = 0) =>
        CommandOutput.Captured(stdout, string.Empty, exitCode, TimeSpan.Zero);

    // CanHandle

    [Fact]
    public void CanHandle_GitStatus_ReturnsTrue() =>
        Assert.True(Make().CanHandle(GitInvocation("status")));

    [Fact]
    public void CanHandle_GitDiff_ReturnsTrue() =>
        Assert.True(Make().CanHandle(GitInvocation("diff")));

    [Fact]
    public void CanHandle_GitLog_ReturnsTrue() =>
        Assert.True(Make().CanHandle(GitInvocation("log")));

    [Fact]
    public void CanHandle_GitFetch_ReturnsFalse() =>
        Assert.False(Make().CanHandle(GitInvocation("fetch")));

    [Fact]
    public void CanHandle_DotnetBuild_ReturnsFalse() =>
        Assert.False(Make().CanHandle(CommandInvocation.Buffered("dotnet", ["build"], "dotnet build")));

    [Fact]
    public void CanHandle_NoArgs_ReturnsFalse() =>
        Assert.False(Make().CanHandle(CommandInvocation.Buffered("git", [], "git")));

    // git status

    [Fact]
    public void Compress_Status_ReducerId_IsGitStatus()
    {
        var result = Make().Compress(GitInvocation("status"), Out("On branch main\nnothing to commit\n"), CompressionOptions.Default);
        Assert.Equal("git-status", result.ReducerId);
    }

    [Fact]
    public void Compress_Status_PreservesBranchLine()
    {
        var input = "On branch feature/my-branch\nnothing to commit, working tree clean\n";
        var result = Make().Compress(GitInvocation("status"), Out(input), CompressionOptions.Default);
        Assert.Contains("On branch feature/my-branch", result.Text);
    }

    [Fact]
    public void Compress_Status_PreservesModifiedFile()
    {
        var input = "On branch main\nChanges not staged for commit:\n\tmodified:   src/Foo.cs\n";
        var result = Make().Compress(GitInvocation("status"), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("src/Foo.cs", result.Text);
        Assert.Contains("modified:", result.Text);
    }

    [Fact]
    public void Compress_Status_PreservesAheadBehind()
    {
        var input = "On branch main\nYour branch is ahead of 'origin/main' by 2 commits.\n";
        var result = Make().Compress(GitInvocation("status"), Out(input), CompressionOptions.Default);
        Assert.Contains("ahead", result.Text);
    }

    [Fact]
    public void Compress_Status_PreservesConflictState()
    {
        var input = "On branch main\nYou have unmerged paths.\n\tboth modified:   src/Foo.cs\n";
        var result = Make().Compress(GitInvocation("status"), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("unmerged", result.Text);
    }

    [Fact]
    public void Compress_Status_DropsBuildBoilerplate()
    {
        var input = "On branch main\n  (use \"git add <file>...\" to update what will be committed)\n  (use \"git restore <file>...\" to discard changes)\n\tmodified:   src/Foo.cs\n";
        var result = Make().Compress(GitInvocation("status"), Out(input, 1), CompressionOptions.Default);
        Assert.DoesNotContain("use \"git add", result.Text);
        Assert.DoesNotContain("use \"git restore", result.Text);
        Assert.Contains("src/Foo.cs", result.Text);
    }

    [Fact]
    public void Compress_Status_PreservesSectionHeaders()
    {
        var input = "On branch main\nChanges to be committed:\n\tnew file:   src/New.cs\nChanges not staged for commit:\n\tmodified:   src/Old.cs\n";
        var result = Make().Compress(GitInvocation("status"), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("Changes to be committed:", result.Text);
        Assert.Contains("Changes not staged for commit:", result.Text);
    }

    // git log

    [Fact]
    public void Compress_Log_ReducerId_IsGitLog()
    {
        var input = "commit abc1234\nAuthor: Dev <dev@example.com>\nDate:   Mon Jan 1 00:00:00 2024 +0000\n\n    Add feature\n";
        var result = Make().Compress(GitInvocation("log"), Out(input), CompressionOptions.Default);
        Assert.Equal("git-log", result.ReducerId);
    }

    [Fact]
    public void Compress_Log_PreservesHashAuthorDateSubject()
    {
        var input = "commit abc1234\nAuthor: Dev <dev@example.com>\nDate:   Mon Jan 1 00:00:00 2024 +0000\n\n    Add feature\n\n    Longer body paragraph\n    that spans two lines\n";
        var result = Make().Compress(GitInvocation("log"), Out(input), CompressionOptions.Default);
        Assert.Contains("commit abc1234", result.Text);
        Assert.Contains("Author:", result.Text);
        Assert.Contains("Date:", result.Text);
        Assert.Contains("Add feature", result.Text);
    }

    [Fact]
    public void Compress_Log_DropsBodyParagraphs()
    {
        var input = "commit abc1234\nAuthor: Dev <dev@example.com>\nDate:   Mon Jan 1 00:00:00 2024 +0000\n\n    Add feature\n\n    This is the long body paragraph\n    that should be dropped\n";
        var result = Make().Compress(GitInvocation("log"), Out(input), CompressionOptions.Default);
        Assert.DoesNotContain("long body paragraph", result.Text);
    }

    // git diff

    [Fact]
    public void Compress_Diff_ReducerId_IsGitDiff()
    {
        var input = "diff --git a/src/Foo.cs b/src/Foo.cs\n--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1,3 +1,4 @@\n context\n+added\n-removed\n";
        var result = Make().Compress(GitInvocation("diff"), Out(input), CompressionOptions.Default);
        Assert.Equal("git-diff", result.ReducerId);
    }

    [Fact]
    public void Compress_Diff_PreservesHunkHeaders()
    {
        var input = "diff --git a/foo b/foo\n--- a/foo\n+++ b/foo\n@@ -1,2 +1,3 @@\n+new line\n";
        var result = Make().Compress(GitInvocation("diff"), Out(input), CompressionOptions.Default);
        Assert.Contains("@@ -1,2 +1,3 @@", result.Text);
    }

    [Fact]
    public void Compress_DiffStat_ReducerId_IsGitDiffStat()
    {
        var input = " src/Foo.cs | 3 +++\n 1 file changed, 3 insertions(+)\n";
        var result = Make().Compress(GitInvocation("diff", "--stat"), Out(input), CompressionOptions.Default);
        Assert.Equal("git-diff-stat", result.ReducerId);
    }

    [Fact]
    public void Compress_DiffStat_PreservesAllLines()
    {
        var input = " src/Foo.cs | 3 +++\n 1 file changed, 3 insertions(+)\n";
        var result = Make().Compress(GitInvocation("diff", "--stat"), Out(input), CompressionOptions.Default);
        Assert.Contains("src/Foo.cs", result.Text);
        Assert.Contains("1 file changed", result.Text);
    }

    [Fact]
    public void Compress_CompressedNotLargerThanOriginal()
    {
        var lines = Enumerable.Range(1, 200).Select(i =>
            i % 4 == 0 ? $"+added line {i}" :
            i % 4 == 1 ? $"-removed line {i}" :
            $" context line {i}").ToList();
        lines.Insert(0, "diff --git a/foo b/foo\n--- a/foo\n+++ b/foo\n@@ -1,200 +1,200 @@");
        var result = Make().Compress(GitInvocation("diff"), Out(string.Join('\n', lines)), CompressionOptions.Default);
        Assert.True(result.CompressedTokens <= result.OriginalTokens);
    }
}
