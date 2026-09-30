using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotationStoreTests
{
    private static int _nextDirectory;

    private static readonly UnixFileMode PermissionBits =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [Fact]
    public void AppendThenLoad_roundTripsAnnotation()
    {
        var directory = CreateTemporaryDirectory();
        var expected = SampleAnnotation("one");

        var append = AnnotationStore.AppendAnnotation(directory, expected);
        var load = AnnotationStore.LoadAnnotations(directory);

        Assert.True(append.IsOk);
        Assert.True(load.IsOk);
        Assert.Equal([expected], load.Value);
    }

    [Fact]
    public void Load_corruptStore_returnsErrorWithoutThrowing()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "annotations.jsonl"), "{broken");

        var load = AnnotationStore.LoadAnnotations(directory);

        Assert.False(load.IsOk);
        Assert.Equal("Unable to read annotations (invalid data)", load.Error);
    }

    [Fact]
    public void Load_afterAppend_ordersNewestFirstForPresentation()
    {
        var directory = CreateTemporaryDirectory();
        Assert.True(AnnotationStore.AppendAnnotation(directory, SampleAnnotation("one")).IsOk);
        Assert.True(AnnotationStore.AppendAnnotation(directory, SampleAnnotation("two")).IsOk);

        var load = AnnotationStore.LoadAnnotations(directory);

        Assert.True(load.IsOk);
        Assert.Equal(["two", "one"], AnnotationStore.NewestFirst(load.Value).Select(item => item.Id));
    }

    [Fact]
    public void NewestFirst_returnsMostRecentlySavedFirst()
    {
        var stored = new[]
        {
            SampleAnnotation("one"),
            SampleAnnotation("two"),
            SampleAnnotation("three"),
        };

        var newest = AnnotationStore.NewestFirst(stored);

        Assert.Equal(["three", "two", "one"], newest.Select(item => item.Id));
        Assert.Equal(["one", "two", "three"], stored.Select(item => item.Id));
    }

    [Fact]
    public void AppendArchivedSet_writesAndReadsArchive()
    {
        var directory = CreateTemporaryDirectory();
        var first = SampleArchive("one", ["annotation-one"]);
        var second = SampleArchive("two", ["annotation-two", "annotation-three"]);

        Assert.True(AnnotationStore.AppendArchivedSet(directory, first).IsOk);
        Assert.True(AnnotationStore.AppendArchivedSet(directory, second).IsOk);

        var load = AnnotationStore.LoadArchivedSets(directory);

        Assert.True(load.IsOk);
        Assert.Equal(["one", "two"], load.Value.Select(archive => archive.Id));
        Assert.Equal(
            ["annotation-one"],
            load.Value[0].Annotations.Select(annotation => annotation.Id));
        Assert.Equal(
            ["annotation-two", "annotation-three"],
            load.Value[1].Annotations.Select(annotation => annotation.Id));
        Assert.Equal("two", AnnotationStore.NewestFirstArchives(load.Value).First().Id);
    }

    [Fact]
    public void Load_corruptArchiveStore_returnsErrorWithoutThrowing()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "archives.jsonl"), "{broken");

        var load = AnnotationStore.LoadArchivedSets(directory);

        Assert.False(load.IsOk);
        Assert.Equal("Unable to read archives (invalid data)", load.Error);
    }

    [Fact]
    public void Append_whenLockHeld_returnsBusyWithoutTakingLock()
    {
        var directory = CreateTemporaryDirectory();
        var lockPath = Path.Combine(directory, ".annotations.lock");
        Directory.CreateDirectory(lockPath);

        var append = AnnotationStore.AppendAnnotation(directory, SampleAnnotation("one"));

        Assert.False(append.IsOk);
        Assert.Equal("Annotations are busy; try again.", append.Error);
        Assert.True(Directory.Exists(lockPath));
        Assert.False(File.Exists(Path.Combine(directory, "annotations.jsonl")));
    }

    [Fact]
    public void Append_whenLockStale_recoversAndWrites()
    {
        var directory = CreateTemporaryDirectory();
        var lockPath = Path.Combine(directory, ".annotations.lock");
        Directory.CreateDirectory(lockPath);
        Directory.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromSeconds(31));

        var expected = SampleAnnotation("one");
        var append = AnnotationStore.AppendAnnotation(directory, expected);
        var load = AnnotationStore.LoadAnnotations(directory);

        Assert.True(append.IsOk);
        Assert.True(load.IsOk);
        Assert.Equal([expected], load.Value);
        Assert.False(Directory.Exists(lockPath));
    }

    [Fact]
    public void Load_archiveVersionOnePointZero_readsArchive()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "archives.jsonl"),
            """{"version": 1.0, "id": "archive-one", "archivedAt": "2026-08-26T23:32:00Z", "annotations": [{"id": "one", "selectedText": "selection one", "comment": "comment one", "capturedAt": "2026-08-08T00:00:00Z", "createdAt": "2026-08-08T00:00:01Z", "context": {}}]}""" + "\n");

        var load = AnnotationStore.LoadArchivedSets(directory);

        Assert.True(load.IsOk);
        Assert.Equal("archive-one", load.Value.Single().Id);
        Assert.Equal("one", load.Value.Single().Annotations.Single().Id);
    }

    [Fact]
    public void StoreCreation_usesOwnerOnlyUnixModes()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var directory = CreateTemporaryDirectory();
        Assert.True(AnnotationStore.AppendAnnotation(directory, SampleAnnotation("one")).IsOk);
        Assert.True(AnnotationStore.AppendArchivedSet(directory, SampleArchive("one", ["annotation-one"])).IsOk);

        AssertOwnerOnlyFile(Path.Combine(directory, "annotations.jsonl"));
        AssertOwnerOnlyFile(Path.Combine(directory, "archives.jsonl"));

        var lockPath = Path.Combine(directory, "mode-lock");
        AnnotationStore.CreateExclusivePrivateDirectory(lockPath);
        AssertOwnerOnlyDirectory(lockPath);
        Assert.Throws<IOException>(() => AnnotationStore.CreateExclusivePrivateDirectory(lockPath));
    }

    private static string CreateTemporaryDirectory()
    {
        var sequence = Interlocked.Increment(ref _nextDirectory);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"hypa-annotate-store-{Environment.ProcessId}-{sequence}");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Annotation SampleAnnotation(string id) =>
        new()
        {
            SelectedText = $"selection {id}",
            CapturedAt = "2026-08-08T00:00:00Z",
            Context = new CaptureContext(),
            Id = id,
            Comment = $"comment {id}",
            CreatedAt = "2026-08-08T00:00:01Z",
        };

    private static ArchivedAnnotationSet SampleArchive(string id, string[] annotationIds) =>
        new()
        {
            Id = id,
            ArchivedAt = $"2026-08-26T23:32:0{id.Length}Z",
            Annotations = annotationIds.Select(SampleAnnotation).ToArray(),
        };

    private static void AssertOwnerOnlyFile(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var mode = File.GetUnixFileMode(path) & PermissionBits;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    private static void AssertOwnerOnlyDirectory(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var mode = File.GetUnixFileMode(path) & PermissionBits;
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            mode);
    }
}
