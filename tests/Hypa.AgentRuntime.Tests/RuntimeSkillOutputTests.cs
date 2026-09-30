using Hypa.AgentRuntime.Tests.Support;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class RuntimeSkillOutputTests
{
    [Fact]
    public async Task Skill_flag_matches_repository_file_byte_for_byte()
    {
        var path = Path.Combine(VtRecordingReplay.FindRepoRoot(), "skills", "hypa-runtime", "SKILL.md");
        var expected = await File.ReadAllBytesAsync(path);
        var (code, stdout, stderr) = await HypaCliProcess.RunBytesAsync("--skill");

        Assert.True(code == 0, stderr);
        Assert.True(
            expected.AsSpan().SequenceEqual(stdout),
            DescribeMismatch(expected, stdout));
    }

    private static string DescribeMismatch(byte[] expected, byte[] actual)
    {
        var limit = Math.Min(expected.Length, actual.Length);
        var index = 0;
        while (index < limit && expected[index] == actual[index])
            index++;

        return "skill bytes differ at "
            + index
            + " expectedLength="
            + expected.Length
            + " actualLength="
            + actual.Length;
    }
}
