using System.Text.Json;

namespace Hypa.Continuity.Application;

/// <summary>
/// Reads tool-call file paths from a session JSONL file.
/// Header <c>cwd</c> rewrite is not a tool path.
/// </summary>
public sealed class SessionToolPathOracle
{
    private static readonly HashSet<string> PathKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "file",
        "filename",
        "file_path",
        "filepath",
        "target_file",
        "dest_path",
        "source_path",
    };

    public IReadOnlyList<SessionToolPath> Read(string sessionFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionFile);
        if (!File.Exists(sessionFile))
            return [];

        var found = new List<SessionToolPath>();
        var lines = File.ReadAllLines(sessionFile);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (IsSessionHeader(root))
                    continue;

                Collect(root, i + 1, found);
            }
            catch (JsonException)
            {
                // Leave unreadable lines out of the path set.
            }
        }

        return found;
    }

    public IReadOnlyList<SessionToolPath> ReadFromLine(string sessionFile, int startLineNumber)
    {
        if (startLineNumber <= 1)
            return Read(sessionFile);

        return Read(sessionFile)
            .Where(p => p.LineNumber >= startLineNumber)
            .ToArray();
    }

    public static bool IsUnderWorkspace(string path, string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(workspace);
        if (string.Equals(fullPath, fullRoot, StringComparison.Ordinal))
            return true;

        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static bool NamesFile(string path, string relativeFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeFile);

        var name = Path.GetFileName(relativeFile.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith('/' + name, StringComparison.Ordinal))
            return true;
        return string.Equals(Path.GetFileName(normalized), name, StringComparison.Ordinal);
    }

    private static bool IsSessionHeader(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "session", StringComparison.Ordinal);
    }

    private static void Collect(JsonElement element, int lineNumber, List<SessionToolPath> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (PathKeys.Contains(prop.Name)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            found.Add(new SessionToolPath { LineNumber = lineNumber, Path = value });
                        continue;
                    }

                    Collect(prop.Value, lineNumber, found);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, lineNumber, found);
                break;
        }
    }
}

/// <summary>One tool-call file path from a session JSONL line.</summary>
public sealed record SessionToolPath
{
    public required int LineNumber { get; init; }
    public required string Path { get; init; }
}
