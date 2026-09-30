using System.Runtime.InteropServices;
using System.Text.Json;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Concurrent JSONL annotation stores under a caller-supplied state directory.
/// </summary>
public static class AnnotationStore
{
    private static readonly TimeSpan StaleLock = TimeSpan.FromSeconds(30);
    private static readonly UnixFileMode OwnerOnlyFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const int HResultFileExists = unchecked((int)0x80070050);
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultAlreadyExists = unchecked((int)0x800700B7);
    private const int HResultPosixExist = unchecked((int)0x80070011);

    /// <summary>
    /// Present append-ordered annotations with the most recently saved first.
    /// </summary>
    public static IReadOnlyList<Annotation> NewestFirst(IReadOnlyList<Annotation> annotations) =>
        annotations.Reverse().ToArray();

    /// <summary>
    /// Present append-ordered archive sets with the most recently archived first.
    /// </summary>
    public static IReadOnlyList<ArchivedAnnotationSet> NewestFirstArchives(
        IReadOnlyList<ArchivedAnnotationSet> archives) =>
        archives.Reverse().ToArray();

    /// <summary>
    /// Load the complete active store, rejecting malformed records instead of dropping data.
    /// </summary>
    public static Result<IReadOnlyList<Annotation>, string> LoadAnnotations(string stateDirectory) =>
        WithStoreLock(stateDirectory, StoreKind.Annotations, () => LoadAnnotationsUnlocked(stateDirectory));

    /// <summary>
    /// Append one annotation without rewriting existing records.
    /// </summary>
    public static Result<StoreAck, string> AppendAnnotation(string stateDirectory, Annotation annotation) =>
        WithStoreLock(stateDirectory, StoreKind.Annotations, () =>
        {
            var filePath = StorePaths.AnnotationsFile(stateDirectory);
            var append = OpenAppendStream(filePath);
            if (!append.IsOk)
                return Result<StoreAck, string>.Fail(append.Error);

            try
            {
                using var stream = append.Value;
                using var writer = new StreamWriter(stream);
                var record = JsonSerializer.Serialize(annotation, AnnotateJsonContext.Default.Annotation);
                writer.WriteLine(record);
                return Result<StoreAck, string>.Ok(default);
            }
            catch (IOException error)
            {
                return Result<StoreAck, string>.Fail(SafeFileError("Unable to save annotation", error));
            }
        });

    /// <summary>
    /// Load complete, parsed annotation sets from the archive store.
    /// </summary>
    public static Result<IReadOnlyList<ArchivedAnnotationSet>, string> LoadArchivedSets(string stateDirectory) =>
        WithStoreLock(stateDirectory, StoreKind.Archives, () => LoadArchivedSetsUnlocked(stateDirectory));

    /// <summary>
    /// Atomically append one complete set to the archive store.
    /// </summary>
    public static Result<StoreAck, string> AppendArchivedSet(
        string stateDirectory,
        ArchivedAnnotationSet archive) =>
        WithStoreLock(stateDirectory, StoreKind.Archives, () =>
        {
            var loaded = LoadArchivedSetsUnlocked(stateDirectory);
            if (!loaded.IsOk)
                return Result<StoreAck, string>.Fail(loaded.Error);

            var updated = loaded.Value.ToList();
            updated.Add(archive);
            return ReplaceArchivedSetsUnlocked(stateDirectory, updated);
        });

    /// <summary>
    /// Remove selected annotation IDs without racing concurrent saves.
    /// </summary>
    public static Result<StoreAck, string> RemoveAnnotationsById(
        string stateDirectory,
        IReadOnlyList<string> annotationIds) =>
        WithStoreLock(stateDirectory, StoreKind.Annotations, () =>
        {
            var loaded = LoadAnnotationsUnlocked(stateDirectory);
            if (!loaded.IsOk)
                return Result<StoreAck, string>.Fail(loaded.Error);

            var removed = annotationIds.ToHashSet(StringComparer.Ordinal);
            var retained = loaded.Value
                .Where(annotation => !removed.Contains(annotation.Id))
                .ToArray();
            return ReplaceAnnotationsUnlocked(stateDirectory, retained);
        });

    /// <summary>
    /// Merge annotations into the active list without duplicating existing IDs.
    /// </summary>
    public static Result<int, string> MergeAnnotations(
        string stateDirectory,
        IReadOnlyList<Annotation> annotations) =>
        WithStoreLock(stateDirectory, StoreKind.Annotations, () =>
        {
            var loaded = LoadAnnotationsUnlocked(stateDirectory);
            if (!loaded.IsOk)
                return Result<int, string>.Fail(loaded.Error);

            var existingIds = loaded.Value.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var additions = annotations
                .Where(item => !existingIds.Contains(item.Id))
                .ToArray();
            if (additions.Length == 0)
                return Result<int, string>.Ok(0);

            var updated = loaded.Value.Concat(additions).ToArray();
            var replaced = ReplaceAnnotationsUnlocked(stateDirectory, updated);
            return replaced.IsOk
                ? Result<int, string>.Ok(additions.Length)
                : Result<int, string>.Fail(replaced.Error);
        });

    /// <summary>
    /// Permanently remove one archived set by its archive ID.
    /// </summary>
    public static Result<StoreAck, string> RemoveArchivedSet(string stateDirectory, string archiveId) =>
        WithStoreLock(stateDirectory, StoreKind.Archives, () =>
        {
            var loaded = LoadArchivedSetsUnlocked(stateDirectory);
            if (!loaded.IsOk)
                return Result<StoreAck, string>.Fail(loaded.Error);

            var retained = loaded.Value
                .Where(archive => !string.Equals(archive.Id, archiveId, StringComparison.Ordinal))
                .ToArray();
            return ReplaceArchivedSetsUnlocked(stateDirectory, retained);
        });

    private static Result<IReadOnlyList<Annotation>, string> LoadAnnotationsUnlocked(string stateDirectory) =>
        LoadJsonLines<Annotation>(
            StorePaths.AnnotationsFile(stateDirectory),
            "annotations",
            AnnotationParser.TryParseAnnotation);

    private static Result<IReadOnlyList<ArchivedAnnotationSet>, string> LoadArchivedSetsUnlocked(
        string stateDirectory) =>
        LoadJsonLines<ArchivedAnnotationSet>(
            StorePaths.ArchivesFile(stateDirectory),
            "archives",
            AnnotationParser.TryParseArchivedSet);

    private static Result<StoreAck, string> ReplaceAnnotationsUnlocked(
        string stateDirectory,
        IReadOnlyList<Annotation> annotations) =>
        ReplaceJsonLines(
            stateDirectory,
            StorePaths.AnnotationsFile(stateDirectory),
            "annotations",
            annotations,
            "Unable to update annotations");

    private static Result<StoreAck, string> ReplaceArchivedSetsUnlocked(
        string stateDirectory,
        IReadOnlyList<ArchivedAnnotationSet> archives) =>
        ReplaceJsonLines(
            stateDirectory,
            StorePaths.ArchivesFile(stateDirectory),
            "archives",
            archives,
            "Unable to update archives");

    private static Result<IReadOnlyList<T>, string> LoadJsonLines<T>(
        string filePath,
        string label,
        TryParseLine<T> parse)
    {
        if (!File.Exists(filePath))
            return Result<IReadOnlyList<T>, string>.Ok([]);

        try
        {
            var records = new List<T>();
            foreach (var line in File.ReadLines(filePath))
            {
                if (line.Length == 0)
                    continue;

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    return Result<IReadOnlyList<T>, string>.Fail($"Unable to read {label} (invalid data)");
                }

                using (document)
                {
                    if (!parse(document.RootElement, out var record) || record is null)
                        return Result<IReadOnlyList<T>, string>.Fail($"Unable to read {label} (invalid data)");
                    records.Add(record);
                }
            }

            return Result<IReadOnlyList<T>, string>.Ok(records);
        }
        catch (IOException error)
        {
            return Result<IReadOnlyList<T>, string>.Fail(SafeFileError($"Unable to read {label}", error));
        }
    }

    private static Result<StoreAck, string> ReplaceJsonLines<T>(
        string stateDirectory,
        string filePath,
        string temporaryLabel,
        IReadOnlyList<T> records,
        string errorMessage)
    {
        var temporary = Path.Combine(
            stateDirectory,
            $".{temporaryLabel}-{Environment.ProcessId}-{Environment.TickCount64}.tmp");

        try
        {
            Directory.CreateDirectory(stateDirectory);
            using (var output = OpenPrivateFile(temporary, append: false))
            using (var writer = new StreamWriter(output, leaveOpen: true))
            {
                foreach (var record in records)
                {
                    var json = JsonSerializer.Serialize(record, typeof(T), AnnotateJsonContext.Default);
                    writer.WriteLine(json);
                }

                writer.Flush();
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, filePath, overwrite: true);
            return Result<StoreAck, string>.Ok(default);
        }
        catch (IOException error)
        {
            TryDeleteFile(temporary);
            return Result<StoreAck, string>.Fail(SafeFileError(errorMessage, error));
        }
    }

    private static Result<T, string> WithStoreLock<T>(
        string stateDirectory,
        StoreKind store,
        Func<Result<T, string>> operation)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
        }
        catch (IOException error)
        {
            return Result<T, string>.Fail(SafeFileError($"Unable to access {StoreLowerName(store)}", error));
        }

        var lease = AcquireStoreLock(StoreLockPath(store, stateDirectory), store);
        if (!lease.IsOk)
            return Result<T, string>.Fail(lease.Error);

        using (lease.Value)
        {
            return operation();
        }
    }

    private static Result<StoreLockLease, string> AcquireStoreLock(string lockPath, StoreKind store)
    {
        var owner = $"{Environment.ProcessId}:{Guid.NewGuid():N}";
        var created = TryCreateStoreLock(lockPath, owner);
        if (created == LockCreateOutcome.Created)
            return Result<StoreLockLease, string>.Ok(new StoreLockLease(lockPath, owner));

        if (created != LockCreateOutcome.Exists)
            return Result<StoreLockLease, string>.Fail($"Unable to lock {StoreLowerName(store)}");

        if (!IsStaleLock(lockPath))
            return Result<StoreLockLease, string>.Fail($"{StoreCapitalizedName(store)} are busy; try again.");

        try
        {
            Directory.Delete(lockPath, recursive: true);
        }
        catch (IOException error)
        {
            return Result<StoreLockLease, string>.Fail(SafeFileError($"Unable to lock {StoreLowerName(store)}", error));
        }

        return TryCreateStoreLock(lockPath, owner) switch
        {
            LockCreateOutcome.Created => Result<StoreLockLease, string>.Ok(new StoreLockLease(lockPath, owner)),
            LockCreateOutcome.Exists =>
                Result<StoreLockLease, string>.Fail($"{StoreCapitalizedName(store)} are busy; try again."),
            _ => Result<StoreLockLease, string>.Fail($"Unable to lock {StoreLowerName(store)}"),
        };
    }

    private static LockCreateOutcome TryCreateStoreLock(string lockPath, string owner)
    {
        try
        {
            CreateExclusivePrivateDirectory(lockPath);
            var ownerPath = Path.Combine(lockPath, "owner");
            try
            {
                using var ownerFile = OpenPrivateFile(ownerPath, append: false);
                using var writer = new StreamWriter(ownerFile, leaveOpen: true);
                writer.Write(owner);
                writer.Write('\n');
                writer.Flush();
            }
            catch (IOException)
            {
                TryDeleteDirectory(lockPath);
                throw;
            }

            return LockCreateOutcome.Created;
        }
        catch (IOException error) when (IsAlreadyExists(error))
        {
            return LockCreateOutcome.Exists;
        }
        catch (IOException)
        {
            return LockCreateOutcome.Failed;
        }
    }

    private static bool IsStaleLock(string lockPath)
    {
        try
        {
            var modified = Directory.GetLastWriteTimeUtc(lockPath);
            return DateTime.UtcNow - modified >= StaleLock;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static Result<FileStream, string> OpenAppendStream(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            return Result<FileStream, string>.Ok(OpenPrivateFile(filePath, append: true));
        }
        catch (IOException error)
        {
            return Result<FileStream, string>.Fail(SafeFileError("Unable to save annotation", error));
        }
    }

    private static FileStream OpenPrivateFile(string path, bool append)
    {
        var options = new FileStreamOptions
        {
            Mode = append ? FileMode.Append : FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            options.UnixCreateMode = OwnerOnlyFile;

        return new FileStream(path, options);
    }

    internal static void CreateExclusivePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!NativeMethods.CreateDirectoryW(path, 0))
                throw CreateLastIoException();
            return;
        }

        if (NativeMethods.MkDir(path, (uint)OwnerOnlyDirectory) == 0)
            return;

        throw CreateLastIoException();
    }

    private static IOException CreateLastIoException()
    {
        var error = Marshal.GetLastPInvokeError();
        var hresult = unchecked((int)0x80070000) | (error & 0xFFFF);
        return new IOException("Unable to create directory", hresult);
    }

    private static bool IsAlreadyExists(IOException error) =>
        error.HResult is HResultFileExists
            or HResultSharingViolation
            or HResultAlreadyExists
            or HResultPosixExist;

    private static string SafeFileError(string prefix, IOException error) =>
        error.HResult switch
        {
            unchecked((int)0x80070002) => $"{prefix} (ENOENT)",
            unchecked((int)0x80070005) => $"{prefix} (EACCES)",
            HResultFileExists => $"{prefix} (EEXIST)",
            HResultSharingViolation => $"{prefix} (EEXIST)",
            HResultAlreadyExists => $"{prefix} (EEXIST)",
            HResultPosixExist => $"{prefix} (EEXIST)",
            unchecked((int)0x8007000D) => $"{prefix} (EACCES)",
            unchecked((int)0x80070070) => $"{prefix} (ENOSPC)",
            unchecked((int)0x8007001E) => $"{prefix} (EROFS)",
            _ => prefix,
        };

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private delegate bool TryParseLine<T>(JsonElement element, out T? record);

    private enum LockCreateOutcome
    {
        Created,
        Exists,
        Failed,
    }

    private static class NativeMethods
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "mkdir")]
        internal static extern int MkDir(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname,
            uint mode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDirectoryW(string lpPathName, nint lpSecurityAttributes);
    }

    private enum StoreKind
    {
        Annotations,
        Archives,
    }

    private static string StoreLowerName(StoreKind store) =>
        store switch
        {
            StoreKind.Annotations => "annotations",
            StoreKind.Archives => "archives",
            _ => throw new ArgumentOutOfRangeException(nameof(store), store, null),
        };

    private static string StoreCapitalizedName(StoreKind store) =>
        store switch
        {
            StoreKind.Annotations => "Annotations",
            StoreKind.Archives => "Archives",
            _ => throw new ArgumentOutOfRangeException(nameof(store), store, null),
        };

    private static string StoreLockPath(StoreKind store, string stateDirectory) =>
        store switch
        {
            StoreKind.Annotations => StorePaths.AnnotationsLock(stateDirectory),
            StoreKind.Archives => StorePaths.ArchivesLock(stateDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(store), store, null),
        };

    private sealed class StoreLockLease : IDisposable
    {
        private readonly string _lockPath;
        private readonly string _owner;

        internal StoreLockLease(string lockPath, string owner)
        {
            _lockPath = lockPath;
            _owner = owner;
        }

        public void Dispose()
        {
            var ownerPath = Path.Combine(_lockPath, "owner");
            try
            {
                if (!File.Exists(ownerPath))
                    return;

                var currentOwner = File.ReadAllText(ownerPath).Trim();
                if (currentOwner == _owner.Trim())
                    Directory.Delete(_lockPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
