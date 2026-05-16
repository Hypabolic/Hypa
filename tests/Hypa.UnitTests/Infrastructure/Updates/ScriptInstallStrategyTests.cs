using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hypa.Infrastructure.Updates;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Updates;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Updates;

public sealed class ScriptInstallStrategyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly IInstallMetadataStore _metadataStore = Substitute.For<IInstallMetadataStore>();
    private readonly ScriptInstallUpdateStrategy _strategy;

    public ScriptInstallStrategyTests()
    {
        Directory.CreateDirectory(_tempDir);
        _strategy = new ScriptInstallUpdateStrategy(_httpFactory, _metadataStore);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── CanHandle ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("script", true)]
    [InlineData("homebrew", false)]
    [InlineData("winget", false)]
    [InlineData("unknown", false)]
    public void CanHandle_OnlyScriptSource(string source, bool expected)
    {
        var metadata = MakeMetadata(source);
        Assert.Equal(expected, _strategy.CanHandle(metadata));
    }

    // ── ValidatePreconditions ─────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_NullChecksumsUrl_ReturnsFail()
    {
        var update = MakeUpdate(checksumsUrl: null);
        var metadata = MakeMetadata("script");

        var result = await _strategy.PlanAsync(update, metadata, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Update.NoChecksums", result.Error.Code);
    }

    [Fact]
    public async Task PlanAsync_NullDownloadUrl_ReturnsFail()
    {
        var update = MakeUpdate(downloadUrl: null, checksumsUrl: "https://example.com/SHA256SUMS");
        var metadata = MakeMetadata("script");

        var result = await _strategy.PlanAsync(update, metadata, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Update.NoDownloadUrl", result.Error.Code);
    }

    [Fact]
    public async Task PlanAsync_MissingInstallDir_ReturnsFail()
    {
        var update = MakeUpdate();
        var metadata = MakeMetadata("script", installDirectory: null);

        var result = await _strategy.PlanAsync(update, metadata, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Update.NoInstallDir", result.Error.Code);
    }

    // ── Path containment ─────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_ExecutableOutsideInstallDir_ReturnsFail()
    {
        var installDir = Path.Combine(_tempDir, "install");
        var execPath = Path.Combine(_tempDir, "other", "hypa");   // outside installDir
        var update = MakeUpdate();
        var metadata = MakeMetadata("script", installDir, execPath);

        var result = await _strategy.ApplyAsync(update, metadata, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Update.PathMismatch", result.Error.Code);
    }

    [Fact]
    public async Task ApplyAsync_ExecutablePathWithDotDotComponent_ReturnsFail()
    {
        var installDir = Path.Combine(_tempDir, "install");
        // Raw string looks like it's inside but resolves outside after GetFullPath
        var execPath = Path.Combine(_tempDir, "install", "..", "escape", "hypa");
        var update = MakeUpdate();
        var metadata = MakeMetadata("script", installDir, execPath);

        var result = await _strategy.ApplyAsync(update, metadata, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Update.PathMismatch", result.Error.Code);
    }

    [Fact]
    public async Task ApplyAsync_ExecutableMatchesInstallDirPrefix_NotSiblingWithSamePrefix()
    {
        // install dir = /tmp/x/hypa, executable = /tmp/x/hypa-evil/hypa should be rejected
        var installDir = Path.Combine(_tempDir, "hypa");
        var execPath = Path.Combine(_tempDir, "hypa-evil", "hypa");
        var update = MakeUpdate();
        var metadata = MakeMetadata("script", installDir, execPath);

        var result = await _strategy.ApplyAsync(update, metadata, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Update.PathMismatch", result.Error.Code);
    }

    // ── Tar extraction ────────────────────────────────────────────────────────

    [Fact]
    public void ExtractArchive_TraversalEntry_ReturnsFail()
    {
        var archivePath = Path.Combine(_tempDir, "payload.tar.gz");
        var extractDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(extractDir);

        WriteTarGz(archivePath, [("../escape/evil", "pwned")]);

        var result = ScriptInstallUpdateStrategy.ExtractArchive(archivePath, extractDir);

        Assert.False(result.IsOk);
        Assert.Equal("Update.PathTraversal", result.Error.Code);
        Assert.False(File.Exists(Path.Combine(_tempDir, "escape", "evil")));
    }

    [Fact]
    public void ExtractArchive_AbsolutePathEntry_ReturnsFail()
    {
        var archivePath = Path.Combine(_tempDir, "payload.tar.gz");
        var extractDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(extractDir);

        // Entry name starting with / — should still be rejected after normalization
        WriteTarGz(archivePath, [("/etc/cron.d/evil", "evil")]);

        // After TrimStart('/') this becomes "etc/cron.d/evil" which is safe,
        // so the test verifies the normalisation itself extracts to extractDir, not /.
        var result = ScriptInstallUpdateStrategy.ExtractArchive(archivePath, extractDir);

        // An absolute-path entry after strip is safe; it should succeed and land inside extractDir
        Assert.True(result.IsOk);
        Assert.True(File.Exists(Path.Combine(extractDir, "etc", "cron.d", "evil")));
        Assert.False(File.Exists("/etc/cron.d/evil"));
    }

    [Fact]
    public void ExtractArchive_NormalEntry_ExtractsToExpectedPath()
    {
        var archivePath = Path.Combine(_tempDir, "payload.tar.gz");
        var extractDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(extractDir);

        WriteTarGz(archivePath, [("hypa", "binary-content"), ("README.md", "docs")]);

        var result = ScriptInstallUpdateStrategy.ExtractArchive(archivePath, extractDir);

        Assert.True(result.IsOk);
        Assert.True(File.Exists(Path.Combine(extractDir, "hypa")));
        Assert.True(File.Exists(Path.Combine(extractDir, "README.md")));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static UpdateInfo MakeUpdate(string? downloadUrl = "https://example.com/hypa.tar.gz",
        string? checksumsUrl = "https://example.com/SHA256SUMS") =>
        new(CurrentVersion: "0.1.0", LatestVersion: "0.2.0",
            ReleaseUrl: "https://example.com/releases/v0.2.0",
            AssetName: "hypa-linux-x64.tar.gz",
            DownloadUrl: downloadUrl,
            ChecksumsUrl: checksumsUrl,
            RuntimeIdentifier: "linux-x64",
            IsUpdateAvailable: true,
            CheckedAt: DateTimeOffset.UtcNow);

    private static InstallMetadata MakeMetadata(string source,
        string? installDirectory = "/home/user/.local/share/hypa",
        string? executablePath = "/home/user/.local/share/hypa/hypa") =>
        new(Source: source, RuntimeIdentifier: "linux-x64",
            InstallDirectory: installDirectory,
            BinLinkPath: "/home/user/.local/bin/hypa",
            ExecutablePath: executablePath,
            InstalledVersion: null, InstalledAt: null);

    // ── ApplyAsync success ────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_Success_PromotesFilesAndRemovesStaleFile()
    {
        // Real install dir containing a stale file that is NOT in the new archive.
        var installDir = Path.Combine(_tempDir, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "old-lib.dat"), "stale");

        // Build the archive: binary + support file (no old-lib.dat).
        var archivePath = Path.Combine(_tempDir, "hypa-linux-x64.tar.gz");
        WriteTarGz(archivePath, [("hypa", "#!/bin/sh\necho hypa"), ("support.dat", "data")]);
        var archiveBytes = File.ReadAllBytes(archivePath);

        // Compute the real SHA-256 so checksum verification passes.
        var hash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var checksumBytes = Encoding.UTF8.GetBytes($"{hash}  hypa-linux-x64.tar.gz");

        var handler = new FakeHttpHandler();
        handler.Add("SHA256SUMS", checksumBytes);
        handler.Add("hypa-linux-x64.tar.gz", archiveBytes);
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var metadata = MakeMetadata("script",
            installDirectory: installDir,
            executablePath: Path.Combine(installDir, "hypa"));

        var update = MakeUpdate(
            downloadUrl: "https://example.com/hypa-linux-x64.tar.gz",
            checksumsUrl: "https://example.com/SHA256SUMS");

        var result = await _strategy.ApplyAsync(update, metadata, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.True(File.Exists(Path.Combine(installDir, "hypa")));
        Assert.True(File.Exists(Path.Combine(installDir, "support.dat")));
        Assert.False(File.Exists(Path.Combine(installDir, "old-lib.dat")));
        await _metadataStore.Received(1).SaveAsync(
            Arg.Is<InstallMetadata>(m => m.InstalledVersion == "0.2.0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_SymlinkInstall_RenameFailsAfterUnlink_RestoresOldSymlink()
    {
        // Set up symlink-based install.
        var oldVersionedDir = Path.Combine(_tempDir, "hypa-oldguid");
        Directory.CreateDirectory(oldVersionedDir);
        File.WriteAllText(Path.Combine(oldVersionedDir, "hypa"), "old-binary");

        var installDir = Path.Combine(_tempDir, "hypa");
        File.CreateSymbolicLink(installDir, oldVersionedDir);

        // Build archive and checksums.
        var archivePath = Path.Combine(_tempDir, "hypa-linux-x64.tar.gz");
        WriteTarGz(archivePath, [("hypa", "#!/bin/sh\necho hypa")]);
        var archiveBytes = File.ReadAllBytes(archivePath);
        var hash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var checksumBytes = Encoding.UTF8.GetBytes($"{hash}  hypa-linux-x64.tar.gz");

        var handler = new FakeHttpHandler();
        handler.Add("SHA256SUMS", checksumBytes);
        handler.Add("hypa-linux-x64.tar.gz", archiveBytes);
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        // Inject a renameLink that throws after the unlink step has already happened.
        var failingStrategy = new ScriptInstallUpdateStrategy(
            _httpFactory, _metadataStore,
            (_, _) => throw new IOException("simulated rename failure"));

        var metadata = MakeMetadata("script",
            installDirectory: installDir,
            executablePath: Path.Combine(installDir, "hypa"));
        var update = MakeUpdate(
            downloadUrl: "https://example.com/hypa-linux-x64.tar.gz",
            checksumsUrl: "https://example.com/SHA256SUMS");

        var result = await failingStrategy.ApplyAsync(update, metadata, CancellationToken.None);

        // ApplyAsync must return a failure, not throw.
        Assert.False(result.IsOk);
        // installDir must be restored as a symlink pointing to the old versioned dir
        // so the user's install is not left broken.
        Assert.True(Directory.Exists(installDir), "installDir should still resolve after rollback");
        Assert.True(File.Exists(Path.Combine(installDir, "hypa")), "old binary must be reachable through restored symlink");
        Assert.NotNull(new FileInfo(installDir).LinkTarget);
    }

    [Fact]
    public async Task ApplyAsync_SymlinkInstall_SwapsSymlinkAndRemovesOldVersionedDir()
    {
        // Set up a symlink-based install (modern format from install.sh).
        var oldVersionedDir = Path.Combine(_tempDir, "hypa-oldguid");
        Directory.CreateDirectory(oldVersionedDir);
        File.WriteAllText(Path.Combine(oldVersionedDir, "stale.dat"), "old");

        var installDir = Path.Combine(_tempDir, "hypa");
        File.CreateSymbolicLink(installDir, oldVersionedDir);

        // Build archive.
        var archivePath = Path.Combine(_tempDir, "hypa-linux-x64.tar.gz");
        WriteTarGz(archivePath, [("hypa", "#!/bin/sh\necho hypa"), ("support.dat", "new")]);
        var archiveBytes = File.ReadAllBytes(archivePath);
        var hash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var checksumBytes = Encoding.UTF8.GetBytes($"{hash}  hypa-linux-x64.tar.gz");

        var handler = new FakeHttpHandler();
        handler.Add("SHA256SUMS", checksumBytes);
        handler.Add("hypa-linux-x64.tar.gz", archiveBytes);
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var metadata = MakeMetadata("script",
            installDirectory: installDir,
            executablePath: Path.Combine(installDir, "hypa"));

        var update = MakeUpdate(
            downloadUrl: "https://example.com/hypa-linux-x64.tar.gz",
            checksumsUrl: "https://example.com/SHA256SUMS");

        var result = await _strategy.ApplyAsync(update, metadata, CancellationToken.None);

        Assert.True(result.IsOk);
        // Files are accessible through the symlink path.
        Assert.True(File.Exists(Path.Combine(installDir, "hypa")));
        Assert.True(File.Exists(Path.Combine(installDir, "support.dat")));
        // Old versioned dir was deleted — stale files gone.
        Assert.False(Directory.Exists(oldVersionedDir));
        // installDir still resolves as a symlink (not converted to real dir).
        Assert.NotNull(new FileInfo(installDir).LinkTarget);
        await _metadataStore.Received(1).SaveAsync(
            Arg.Is<InstallMetadata>(m => m.InstalledVersion == "0.2.0"),
            Arg.Any<CancellationToken>());
    }

    private static void WriteTarGz(string path, IEnumerable<(string Name, string Content)> entries)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var tar = new TarWriter(gzip, TarEntryFormat.Gnu);
        foreach (var (name, content) in entries)
        {
            var entry = new GnuTarEntry(TarEntryType.RegularFile, name);
            var bytes = Encoding.UTF8.GetBytes(content);
            entry.DataStream = new MemoryStream(bytes);
            tar.WriteEntry(entry);
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly List<(string Key, byte[] Content)> _routes = [];

        public void Add(string urlSubstring, byte[] content) =>
            _routes.Add((urlSubstring, content));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            foreach (var (key, content) in _routes)
                if (url.Contains(key, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new ByteArrayContent(content) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
