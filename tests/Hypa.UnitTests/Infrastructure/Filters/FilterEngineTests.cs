using System.Text.RegularExpressions;
using Hypa.Infrastructure.Filters;
using Hypa.Runtime.Domain.Filters;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Filters;

public sealed class FilterEngineTests
{
    private static readonly FilterEngine Engine = new();

    private static CompiledFilterDefinition SingleStage(FilterStage stage) =>
        new()
        {
            Id = "test",
            Stages =
            [
                new CompiledFilterStage
                {
                    Stage = stage,
                    CompiledRegex = stage.Pattern is not null ? new Regex(stage.Pattern) : null,
                    CompiledGuard = stage.Guard is not null ? new Regex(stage.Guard) : null,
                },
            ],
        };

    [Fact]
    public void Apply_StripAnsi_RemovesEscapeCodes()
    {
        var input = "\x1B[31mhello\x1B[0m world";
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.StripAnsi });
        var result = Engine.Apply(filter, input);
        Assert.Equal("hello world", result.Text);
        Assert.Equal(1, result.StagesApplied);
    }

    [Fact]
    public void Apply_KeepLines_DropsNonMatchingLines()
    {
        var input = "error: something bad\ninfo: all good\nerror: another bad";
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.KeepLines, Pattern = "^error:" });
        var result = Engine.Apply(filter, input);
        Assert.DoesNotContain("info:", result.Text);
        Assert.Contains("error: something bad", result.Text);
        Assert.Contains("error: another bad", result.Text);
    }

    [Fact]
    public void Apply_StripLines_DropsMatchingLines()
    {
        var input = "progress: 10%\nresult: done\nprogress: 50%";
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.StripLines, Pattern = "^progress:" });
        var result = Engine.Apply(filter, input);
        Assert.DoesNotContain("progress:", result.Text);
        Assert.Contains("result: done", result.Text);
    }

    [Fact]
    public void Apply_Replace_SubstitutesPattern()
    {
        var input = "foo bar foo";
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.Replace, Pattern = "foo", Replacement = "baz" });
        var result = Engine.Apply(filter, input);
        Assert.Equal("baz bar baz", result.Text);
    }

    [Fact]
    public void Apply_HeadLines_TruncatesToCount()
    {
        var input = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.HeadLines, Count = 3 });
        var result = Engine.Apply(filter, input);
        var lines = result.Text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("line 1", lines[0]);
        Assert.Equal("line 3", lines[2]);
    }

    [Fact]
    public void Apply_TailLines_PreservesLastNLines()
    {
        var input = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.TailLines, Count = 3 });
        var result = Engine.Apply(filter, input);
        var lines = result.Text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("line 10", lines[2]);
    }

    [Fact]
    public void Apply_MaxLines_CapsOutput()
    {
        var input = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.MaxLines, Count = 4 });
        var result = Engine.Apply(filter, input);
        Assert.Contains("omitted", result.Text);
    }

    [Fact]
    public void Apply_MaxLines_WithOddCount_ReportsActualOmittedCount()
    {
        var input = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.MaxLines, Count = 9 });

        var result = Engine.Apply(filter, input);
        var lines = result.Text.Split('\n');

        Assert.Equal(10, lines.Length);
        Assert.Equal("line 1", lines[0]);
        Assert.Equal("line 5", lines[4]);
        Assert.Equal("... (1 lines omitted) ...", lines[5]);
        Assert.Equal("line 7", lines[6]);
        Assert.Equal("line 10", lines[9]);
    }

    [Fact]
    public void Apply_OnEmpty_ReturnsDefaultWhenBlank()
    {
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "(no output)" });
        var result = Engine.Apply(filter, "   ");
        Assert.Equal("(no output)", result.Text);
    }

    [Fact]
    public void Apply_OnEmpty_ReturnsOriginalWhenNotBlank()
    {
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "(no output)" });
        var result = Engine.Apply(filter, "hello");
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public void Apply_MultiStage_ChainsCorrectly()
    {
        var input = "\x1B[31merror: bad\x1B[0m\ninfo: good";
        var filter = new CompiledFilterDefinition
        {
            Id = "test",
            Stages =
            [
                new CompiledFilterStage { Stage = new FilterStage { Kind = FilterStageKind.StripAnsi } },
                new CompiledFilterStage
                {
                    Stage = new FilterStage { Kind = FilterStageKind.KeepLines, Pattern = "^error:" },
                    CompiledRegex = new Regex("^error:"),
                },
            ],
        };
        var result = Engine.Apply(filter, input);
        Assert.Equal("error: bad", result.Text);
        Assert.Equal(2, result.StagesApplied);
    }

    [Fact]
    public void Apply_NoStagesApplied_WhenTextUnchanged()
    {
        var input = "no ansi here";
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.StripAnsi });
        var result = Engine.Apply(filter, input);
        Assert.Equal(0, result.StagesApplied);
    }

    [Fact]
    public void Apply_TruncateLinesAt_CapsWith_TruncationNote()
    {
        var input = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 3 });
        var result = Engine.Apply(filter, input);
        Assert.Contains("truncated at 3", result.Text);
    }

    [Fact]
    public void Apply_MatchOutput_ShortCircuitsAndReturnsReplacement()
    {
        var filter = SingleStage(new FilterStage
        {
            Kind = FilterStageKind.MatchOutput,
            Pattern = "Build complete!",
            Replacement = "ok (build complete)",
        });

        var result = Engine.Apply(filter, "Build complete!\n0 warnings");

        Assert.Equal("ok (build complete)", result.Text);
        Assert.Equal(1, result.StagesApplied);
    }

    [Fact]
    public void Apply_MatchOutput_GuardSuppressesShortCircuit()
    {
        var filter = new CompiledFilterDefinition
        {
            Id = "test",
            Stages =
            [
                new CompiledFilterStage
                {
                    Stage = new FilterStage
                    {
                        Kind = FilterStageKind.MatchOutput,
                        Pattern = "Build complete!",
                        Replacement = "ok (build complete)",
                        Guard = "warning|error",
                    },
                    CompiledRegex = new Regex("Build complete!"),
                    CompiledGuard = new Regex("warning|error"),
                },
                new CompiledFilterStage
                {
                    Stage = new FilterStage { Kind = FilterStageKind.KeepLines, Pattern = "^Build" },
                    CompiledRegex = new Regex("^Build"),
                },
            ],
        };

        var result = Engine.Apply(filter, "Build complete!\nwarning: unused");

        Assert.Equal("Build complete!", result.Text);
        Assert.Equal(1, result.StagesApplied);
    }

    [Fact]
    public void Apply_MatchOutput_DoesNotFireWhenPatternMisses()
    {
        var filter = SingleStage(new FilterStage
        {
            Kind = FilterStageKind.MatchOutput,
            Pattern = "Build complete!",
            Replacement = "ok (build complete)",
        });

        var result = Engine.Apply(filter, "Build failed");

        Assert.Equal("Build failed", result.Text);
        Assert.Equal(0, result.StagesApplied);
    }

    [Fact]
    public void Apply_MatchOutput_PipelineContinuesAfterNonFiringStage()
    {
        var filter = new CompiledFilterDefinition
        {
            Id = "test",
            Stages =
            [
                new CompiledFilterStage
                {
                    Stage = new FilterStage
                    {
                        Kind = FilterStageKind.MatchOutput,
                        Pattern = "Build complete!",
                        Replacement = "ok (build complete)",
                    },
                    CompiledRegex = new Regex("Build complete!"),
                },
                new CompiledFilterStage
                {
                    Stage = new FilterStage { Kind = FilterStageKind.KeepLines, Pattern = "^error:" },
                    CompiledRegex = new Regex("^error:"),
                },
            ],
        };

        var result = Engine.Apply(filter, "info: starting\nerror: failed");

        Assert.Equal("error: failed", result.Text);
        Assert.Equal(1, result.StagesApplied);
    }

    [Fact]
    public void Apply_MatchOutput_MultipleCandidates_FirstMatchWins()
    {
        var filter = new CompiledFilterDefinition
        {
            Id = "test",
            Stages =
            [
                new CompiledFilterStage
                {
                    Stage = new FilterStage { Kind = FilterStageKind.MatchOutput, Pattern = "ok", Replacement = "first" },
                    CompiledRegex = new Regex("ok"),
                },
                new CompiledFilterStage
                {
                    Stage = new FilterStage { Kind = FilterStageKind.MatchOutput, Pattern = "ok", Replacement = "second" },
                    CompiledRegex = new Regex("ok"),
                },
            ],
        };

        var result = Engine.Apply(filter, "ok");

        Assert.Equal("first", result.Text);
        Assert.Equal(1, result.StagesApplied);
    }

    [Fact]
    public void Apply_MatchOutput_ReturnsEmptyStringWhenReplacementIsNull()
    {
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.MatchOutput, Pattern = "ok" });

        var result = Engine.Apply(filter, "ok");

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(1, result.StagesApplied);
    }

    [Fact]
    public void Apply_NativeTransform_CompactsGitDiffContext()
    {
        var input = """
diff --git a/app.cs b/app.cs
index 111..222 100644
--- a/app.cs
+++ b/app.cs
@@ -1,8 +1,8 @@
 context 1
 context 2
 context 3
 context 4
-old
+new
 context 5
""";
        var filter = SingleStage(new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "git.diff" });

        var result = Engine.Apply(filter, input);

        Assert.DoesNotContain("index 111", result.Text);
        Assert.DoesNotContain("context 4", result.Text);
        Assert.Contains("-old", result.Text);
        Assert.Contains("+new", result.Text);
        Assert.Equal(1, result.StagesApplied);
    }
}
