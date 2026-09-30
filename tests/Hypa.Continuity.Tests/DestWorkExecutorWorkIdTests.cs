using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypa.Continuity.Harnesses.Fake;
using Hypa.Continuity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class DestWorkExecutorWorkIdTests
{
    [Fact]
    public async Task Apply_rejects_path_like_work_id()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-dest-pathid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var spoolDir = Path.Combine(root, "spool");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var packPath = Path.Combine(root, "src.workpack");
            File.WriteAllBytes(packPath, [1, 2, 3, 4]);

            await using var store = new SqliteContinuityStore(Path.Combine(root, "store"));
            var executor = new InProcessDestWorkExecutor(
                new DestApplyService(
                    new WorkService(store),
                    new GitWorkspacePacker(),
                    new WorkPackCodec()),
                new DestApplyHostContext
                {
                    DestHome = Path.Combine(root, "home"),
                    DestWorkspace = Path.Combine(root, "ws"),
                    DestMuxEndpoint = "unix:" + Path.Combine(root, "hypa.sock"),
                    Harness = new FakeHarnessAdapter(),
                },
                new LocalWorkPackSpool(spoolDir));

            var result = await executor.ApplyAsync(
                new DestApplyInvocation
                {
                    PackPath = packPath,
                    Request = new DestApplyRequest
                    {
                        AttemptId = "att_path01xx",
                        WorkId = "../outside",
                        DestPlacementId = "plc_path01xx",
                    },
                });

            Assert.False(result.Ok);
            Assert.Equal(ContinuityReasons.Internal, result.Reason);
            Assert.Contains("work id", result.Detail, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(outside));
            Assert.False(File.Exists(Path.GetFullPath(Path.Combine(spoolDir, "../outside.workpack"))));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
