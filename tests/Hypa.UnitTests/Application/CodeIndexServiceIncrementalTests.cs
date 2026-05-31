using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class CodeIndexServiceIncrementalTests
{
    // ── IndexIncrementalAsync ────────────────────────────────────────────────

    [Fact]
    public async Task IndexIncrementalAsync_WhenBlobOidUnchanged_SkipsFile()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "code.cs"));
            await File.WriteAllTextAsync(filePath, "public class Code {}");
            var relativePath = "code.cs";
            const string oid = "oid_abc";

            var repo = new TrackingRepository();
            repo.FileStates[filePath] = new FileIndexState
            {
                AbsolutePath = filePath,
                GitBlobOid = oid,
                MTimeMs = 0,
                SizeBytes = new FileInfo(filePath).Length,
            };

            var git = new FakeGitFileStateProvider
            {
                BlobOids = new Dictionary<string, string> { [relativePath] = oid },
            };
            var service = MakeService(dir, repo, git);

            var result = await service.IndexIncrementalAsync(dir, CancellationToken.None);

            Assert.Empty(repo.SavedDocuments);
            Assert.Equal(0, result.FilesIndexed);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task IndexIncrementalAsync_WhenBlobOidChanged_ReIndexesFile()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "code.cs"));
            await File.WriteAllTextAsync(filePath, "public class Code {}");
            var relativePath = "code.cs";

            var repo = new TrackingRepository();
            repo.FileStates[filePath] = new FileIndexState
            {
                AbsolutePath = filePath,
                GitBlobOid = "old_oid",
                MTimeMs = 0,
                SizeBytes = new FileInfo(filePath).Length,
            };

            var git = new FakeGitFileStateProvider
            {
                BlobOids = new Dictionary<string, string> { [relativePath] = "new_oid" },
            };
            var service = MakeService(dir, repo, git);

            var result = await service.IndexIncrementalAsync(dir, CancellationToken.None);

            Assert.Equal(1, result.FilesIndexed);
            Assert.Single(repo.SavedDocuments);
            Assert.Equal("new_oid", repo.SavedDocuments[0].File.GitBlobOid);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task IndexIncrementalAsync_WhenFileNew_Indexes()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "new.cs"));
            await File.WriteAllTextAsync(filePath, "public class New {}");
            var relativePath = "new.cs";

            var repo = new TrackingRepository(); // empty manifest
            var git = new FakeGitFileStateProvider
            {
                BlobOids = new Dictionary<string, string> { [relativePath] = "oid_new" },
            };
            var service = MakeService(dir, repo, git);

            var result = await service.IndexIncrementalAsync(dir, CancellationToken.None);

            Assert.Equal(1, result.FilesIndexed);
            Assert.Single(repo.SavedDocuments);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task IndexIncrementalAsync_WhenFileDeleted_RemovesFromDb()
    {
        var dir = TempDir();
        try
        {
            // Path is under the project root but the file does not exist on disk
            var ghostPath = Path.GetFullPath(Path.Combine(dir, "ghost.cs"));

            var repo = new TrackingRepository();
            repo.FileStates[ghostPath] = new FileIndexState
            {
                AbsolutePath = ghostPath,
                GitBlobOid = null,
                MTimeMs = 999,
                SizeBytes = 42,
            };

            var git = new FakeGitFileStateProvider { BlobOids = null };
            var service = MakeService(dir, repo, git);

            var result = await service.IndexIncrementalAsync(dir, CancellationToken.None);

            Assert.Equal(1, result.FilesDeleted);
            Assert.Contains(ghostPath, repo.DeletedFiles);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task IndexIncrementalAsync_WhenNonGitProject_UsesMtimeForAll()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "code.cs"));
            await File.WriteAllTextAsync(filePath, "public class Code {}");
            var info = new FileInfo(filePath);
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

            var repo = new TrackingRepository();
            repo.FileStates[filePath] = new FileIndexState
            {
                AbsolutePath = filePath,
                GitBlobOid = null,
                MTimeMs = mtime,
                SizeBytes = info.Length,
            };

            // git unavailable
            var git = new FakeGitFileStateProvider { BlobOids = null };
            var service = MakeService(dir, repo, git);

            var result = await service.IndexIncrementalAsync(dir, CancellationToken.None);

            Assert.Empty(repo.SavedDocuments);
            Assert.Equal(0, result.FilesIndexed);
        }
        finally { DeleteDir(dir); }
    }

    // ── EnsureFreshAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureFreshAsync_WhenTrackedCleanAndOidMatches_IsNoOp()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "code.cs"));
            await File.WriteAllTextAsync(filePath, "public class Code {}");
            const string oid = "oid_match";

            var repo = new TrackingRepository();
            repo.FileStates[filePath] = new FileIndexState
            {
                AbsolutePath = filePath,
                GitBlobOid = oid,
                MTimeMs = 0,
                SizeBytes = 1,
            };

            var git = new FakeGitFileStateProvider { SingleBlobOid = oid };
            var service = MakeService(dir, repo, git);

            await service.EnsureFreshAsync(filePath, CancellationToken.None);

            Assert.Empty(repo.SavedDocuments);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task EnsureFreshAsync_WhenTrackedCleanAndOidDiffers_ReIndexes()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "code.cs"));
            await File.WriteAllTextAsync(filePath, "public class Code {}");

            var repo = new TrackingRepository();
            repo.FileStates[filePath] = new FileIndexState
            {
                AbsolutePath = filePath,
                GitBlobOid = "old_oid",
                MTimeMs = 0,
                SizeBytes = 1,
            };

            var git = new FakeGitFileStateProvider { SingleBlobOid = "new_oid" };
            var service = MakeService(dir, repo, git);

            await service.EnsureFreshAsync(filePath, CancellationToken.None);

            Assert.NotEmpty(repo.SavedDocuments);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task EnsureFreshAsync_WhenFileDoesNotExist_IsNoOp()
    {
        var dir = TempDir();
        try
        {
            const string missingPath = "/nonexistent/phantom.cs";
            var repo = new TrackingRepository();
            var git = new FakeGitFileStateProvider { SingleBlobOid = "oid_x" };
            var service = MakeService(dir, repo, git);

            await service.EnsureFreshAsync(missingPath, CancellationToken.None);

            Assert.Empty(repo.SavedDocuments);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task EnsureFreshAsync_WhenNotIndexed_IndexesFile()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "code.cs"));
            await File.WriteAllTextAsync(filePath, "public class Code {}");

            var repo = new TrackingRepository(); // empty — no stored state
            var git = new FakeGitFileStateProvider { SingleBlobOid = null }; // untracked
            var service = MakeService(dir, repo, git);

            await service.EnsureFreshAsync(filePath, CancellationToken.None);

            Assert.NotEmpty(repo.SavedDocuments);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task IndexFullAsync_PersistsGitBlobOid_SoSubsequentIncrementalIsNoOp()
    {
        // Regression: IndexFullAsync must write GitBlobOid so that a subsequent
        // IndexIncrementalAsync does not treat every file as stale.
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "code.cs"));
            await File.WriteAllTextAsync(filePath, "public class Code {}");
            const string oid = "oid_full";

            var repo = new TrackingRepository();
            var git = new FakeGitFileStateProvider
            {
                BlobOids = new Dictionary<string, string> { ["code.cs"] = oid },
                SingleBlobOid = oid,
            };
            var service = MakeService(dir, repo, git);

            await service.IndexFullAsync(dir, CancellationToken.None);
            Assert.Equal(oid, repo.SavedDocuments[0].File.GitBlobOid);

            repo.SavedDocuments.Clear();
            var result = await service.IndexIncrementalAsync(dir, CancellationToken.None);

            Assert.Equal(0, result.FilesIndexed);
            Assert.Empty(repo.SavedDocuments);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task EnsureFreshAsync_AfterReIndex_SecondCallIsNoOp()
    {
        // Regression: ReIndexFileAsync must persist GitBlobOid so the next
        // EnsureFreshAsync call for a tracked+clean file does not re-index again.
        var dir = TempDir();
        try
        {
            var filePath = Path.GetFullPath(Path.Combine(dir, "code.cs"));
            await File.WriteAllTextAsync(filePath, "public class Code {}");
            const string oid = "stable_oid";

            var repo = new TrackingRepository(); // no stored state
            var git = new FakeGitFileStateProvider { SingleBlobOid = oid };
            var service = MakeService(dir, repo, git);

            await service.EnsureFreshAsync(filePath, CancellationToken.None); // first call — re-indexes
            var savedAfterFirst = repo.SavedDocuments.Count;
            Assert.Equal(1, savedAfterFirst);
            Assert.Equal(oid, repo.SavedDocuments[0].File.GitBlobOid);

            await service.EnsureFreshAsync(filePath, CancellationToken.None); // second call — must be no-op
            Assert.Equal(savedAfterFirst, repo.SavedDocuments.Count);
        }
        finally { DeleteDir(dir); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CodeIndexService MakeService(string root, TrackingRepository repo, FakeGitFileStateProvider git)
    {
        var rootDetector = new FakeProjectRootDetector(root);
        var registry = new CodeStructureProviderRegistry([new RegexFallbackCodeStructureProvider()]);
        return new CodeIndexService(rootDetector, registry, repo, git);
    }

    private static string TempDir()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"hypa-incr-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeProjectRootDetector(string root) : IProjectRootDetector
    {
        public string? Detect(string startPath) => root;
    }

    private sealed class FakeGitFileStateProvider : IGitFileStateProvider
    {
        public IReadOnlyDictionary<string, string>? BlobOids { get; set; }
        public string? SingleBlobOid { get; set; }

        public Task<IReadOnlyDictionary<string, string>?> GetCleanBlobOidsAsync(string root, CancellationToken ct) =>
            Task.FromResult(BlobOids);

        public Task<string?> GetCleanBlobOidAsync(string absolutePath, string projectRoot, CancellationToken ct) =>
            Task.FromResult(SingleBlobOid);
    }

    private sealed class TrackingRepository : ICodeIndexRepository
    {
        public Dictionary<string, FileIndexState> FileStates { get; } = [];
        public List<CodeStructureDocument> SavedDocuments { get; } = [];
        public List<string> DeletedFiles { get; } = [];

        public Task SaveDocumentsAsync(IReadOnlyList<CodeStructureDocument> documents, CancellationToken ct)
        {
            SavedDocuments.AddRange(documents);
            foreach (var doc in documents)
            {
                FileStates[doc.File.Path] = new FileIndexState
                {
                    AbsolutePath = doc.File.Path,
                    GitBlobOid = doc.File.GitBlobOid,
                    MTimeMs = doc.File.MTimeMs,
                    SizeBytes = doc.File.SizeBytes,
                };
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, FileIndexState>> QueryFileStatesAsync(string projectRoot, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, FileIndexState>>(new Dictionary<string, FileIndexState>(FileStates));

        public Task<FileIndexState?> QueryFileStateAsync(string absolutePath, CancellationToken ct) =>
            Task.FromResult(FileStates.GetValueOrDefault(absolutePath));

        public Task DeleteFileAsync(string absolutePath, CancellationToken ct)
        {
            DeletedFiles.Add(absolutePath);
            FileStates.Remove(absolutePath);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CodeSymbol>> QuerySymbolsAsync(CodeSymbolQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodeSymbol>>([]);

        public Task<CodeGraphResult> QueryGraphAsync(CodeGraphQuery query, CancellationToken ct) =>
            Task.FromResult(new CodeGraphResult());

        public Task<IReadOnlyList<CodeDiagnostic>> QueryDiagnosticsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodeDiagnostic>>([]);

        public Task<CodeStructureDocument?> QueryMarkdownAsync(string filePath, CancellationToken ct) =>
            Task.FromResult<CodeStructureDocument?>(null);

        public Task<IReadOnlyList<MarkdownSection>> QueryMarkdownSectionsAsync(string filePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MarkdownSection>>([]);

        public Task<IReadOnlyList<CodeReference>> QueryReferencesAsync(string filePath, string kind, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodeReference>>([]);

        public Task SaveProviderHealthAsync(IReadOnlyList<CodeProviderHealth> health, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CodeProviderHealth>> GetProviderHealthAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodeProviderHealth>>([]);
    }
}
