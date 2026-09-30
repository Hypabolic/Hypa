using System.Text.Json;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Logging;

/// <summary>
// / JSON Lines rotating file sink.
// / rotate-before-write.
/// rotation error latches disabled at <c>src/logging.rs:438-440</c>.
/// rename down). Hypa retains three numbered files. Unix mode is 0600. Quote
// / <c>ScrollbackHistoryFile</c> for the mode rule.
/// <c>src/logging.rs:499-507</c> opens create+append on a regular file
/// (<c>S_IFREG</c>) only; Hypa refuses a symlink, a FIFO, and a
/// group/world-writable parent. Rotation also refuses an unrelated
/// <c>path.N</c> node that is not this sink's JSON Lines.
/// </summary>
public sealed class JsonLinesFileLogSink : IProcessLogSink, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ProcessLogLevel _filter;
    private readonly long _maxBytes;
    private readonly int _retainFiles;
    private FileStream? _file;
    private long _currentSize;
    private bool _disabled;
    private bool _disposed;
    private bool _markerWritten;

    public JsonLinesFileLogSink(
        string path,
        ProcessLogLevel filter,
        long maxBytes = ProcessLogPaths.DefaultMaxBytes,
        int retainFiles = ProcessLogPaths.DefaultRetainFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _filter = filter;
        _maxBytes = maxBytes <= 0 ? ProcessLogPaths.DefaultMaxBytes : maxBytes;
        _retainFiles = retainFiles < 0 ? 0 : retainFiles;
        if (_filter == ProcessLogLevel.Off)
            return;

        if (!UnixLogPathSecurity.TryEnsurePrivateParent(_path))
            _disabled = true;
    }

    public bool Disabled
    {
        get
        {
            lock (_gate)
                return _disabled;
        }
    }

    public string? Path => _path;

    public bool IsEnabled(ProcessLogLevel level)
    {
        lock (_gate)
        {
            if (_disabled || _disposed || _filter == ProcessLogLevel.Off)
                return false;
            return ProcessLogFilter.Allows(_filter, level);
        }
    }

    public void Write(ProcessLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsEnabled(record.Level))
            return;

        byte[] line;
        try
        {
            line = ProcessLogJsonWriter.WriteLine(record);
        }
        catch
        {
            LatchDisabled();
            return;
        }

        lock (_gate)
        {
            if (_disabled || _disposed)
                return;
            try
            {
                RotateIfNeeded(line.Length);
                if (_file is null)
                    OpenCurrentFile();
                if (_file is null)
                {
                    LatchDisabledUnlocked();
                    return;
                }

                _file.Write(line, 0, line.Length);
                _file.Flush();
                _currentSize += line.Length;
            }
            catch
            {
                LatchDisabledUnlocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _file?.Dispose();
            }
            catch
            {
            }

            _file = null;
        }
    }

    private void RotateIfNeeded(int incomingLen)
    {
        if (_file is null)
            OpenCurrentFile();
        if (_maxBytes == 0 || _currentSize + incomingLen <= _maxBytes)
            return;
        // Skip when the
        // current file is still empty so path.N is this sink's JSON Lines,
        // not a just-created empty file.
        if (_currentSize == 0)
            return;
        RotateFiles();
        OpenCurrentFile();
    }

    private void OpenCurrentFile()
    {
        _file?.Dispose();
        _file = null;
        if (!UnixLogPathSecurity.TryEnsurePrivateParent(_path)
            || UnixLogPathSecurity.IsSymlink(_path)
            || !UnixLogPathSecurity.TryOpenAppend(_path, out var stream)
            || stream is null)
        {
            return;
        }

        _file = stream;
        if (!VerifyOwnerFile(_path))
        {
            try
            {
                _file.Dispose();
            }
            catch
            {
            }

            _file = null;
            return;
        }

        _currentSize = _file.Length;
    }

    private void RotateFiles()
    {
        _file?.Dispose();
        _file = null;
        if (_retainFiles == 0)
        {
            try
            {
                if (!UnixLogPathSecurity.IsSymlink(_path))
                    File.Delete(_path);
            }
            catch (FileNotFoundException)
            {
            }

            _currentSize = 0;
            return;
        }

        // Before any delete or move, refuse an unrelated path.N file.
        EnsureRetainedFilesAreSinkOwned();

        var oldest = RotatedPath(_retainFiles);
        try
        {
            if (File.Exists(oldest))
            {
                if (!IsSinkOwnedRetainedLog(oldest))
                    throw new IOException("log rotation refused an unrelated retained path");
                File.Delete(oldest);
            }
        }
        catch (FileNotFoundException)
        {
        }

        for (var index = _retainFiles; index >= 1; index--)
        {
            var source = index == 1 ? _path : RotatedPath(index - 1);
            var target = RotatedPath(index);
            if (!File.Exists(source))
                continue;
            if (UnixLogPathSecurity.IsSymlink(source) || UnixLogPathSecurity.IsSymlink(target))
                throw new IOException("log rotation refused a symlink");
            File.Move(source, target, overwrite: false);
            if (!VerifyOwnerFile(target))
                throw new IOException("rotated log is not owner-private");
        }

        _currentSize = 0;
    }

    private string RotatedPath(int index) =>
        _path + "." + index.ToString();

    private void EnsureRetainedFilesAreSinkOwned()
    {
        for (var index = 1; index <= _retainFiles; index++)
        {
            var retained = RotatedPath(index);
            if (UnixLogPathSecurity.IsSymlink(retained))
                throw new IOException("log rotation refused a symlink");
            if (!File.Exists(retained) && !Directory.Exists(retained))
                continue;
            if (!IsSinkOwnedRetainedLog(retained))
                throw new IOException("log rotation refused an unrelated retained path");
        }
    }

    private static bool IsSinkOwnedRetainedLog(string path)
    {
        if (UnixLogPathSecurity.IsSymlink(path))
            return false;
        if (!UnixLogPathSecurity.IsOwnerOnlyFile(path))
            return false;
        if (!UnixLogPathSecurity.TryOpenReadRegular(path, out var stream) || stream is null)
            return false;

        try
        {
            using (stream)
            {
                using var reader = new StreamReader(stream);
                var first = reader.ReadLine();
                if (string.IsNullOrEmpty(first))
                    return false;
                using var doc = JsonDocument.Parse(first);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return false;
                return doc.RootElement.TryGetProperty("event", out var ev)
                    && ev.ValueKind == JsonValueKind.String;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void LatchDisabled()
    {
        lock (_gate)
            LatchDisabledUnlocked();
    }

    private void LatchDisabledUnlocked()
    {
        _disabled = true;
        try
        {
            _file?.Dispose();
        }
        catch
        {
        }

        _file = null;
        WriteDisabledMarker();
    }

    private void WriteDisabledMarker()
    {
        if (_markerWritten)
            return;
        _markerWritten = true;
        try
        {
            var marker = ProcessLogPaths.DisabledMarkerPath(_path);
            if (UnixLogPathSecurity.IsSymlink(marker)
                || !UnixLogPathSecurity.TryEnsurePrivateParent(marker)
                || !UnixLogPathSecurity.TryOpenCreate(marker, out var stream)
                || stream is null)
            {
                return;
            }

            using (stream)
            {
                stream.WriteByte((byte)'1');
            }

            _ = VerifyOwnerFile(marker);
        }
        catch
        {
        }
    }

    private static bool VerifyOwnerFile(string path)
    {
        if (!UnixLogPathSecurity.IsUnix)
            return true;
        if (UnixLogPathSecurity.IsSymlink(path) || !UnixLogPathSecurity.IsOwnerOnlyFile(path))
            return false;
        try
        {
#pragma warning disable CA1416
            File.SetUnixFileMode(path, UnixLogPathSecurity.OwnerFileMode);
#pragma warning restore CA1416
        }
        catch
        {
            return false;
        }

        return UnixLogPathSecurity.IsOwnerOnlyFile(path);
    }
}
