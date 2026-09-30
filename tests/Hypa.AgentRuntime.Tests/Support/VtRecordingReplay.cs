using System.Text;
using System.Text.Json;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;

namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// Replay script.ndjson recordings into GhosttyVtEngine.
/// Detection input is <c>GetRecentText(80)</c> (same as <c>PaneRuntime.ReadDetectionText</c>).
/// </summary>
internal static class VtRecordingReplay
{
    public const int DetectionMaxLines = 80;
    public const string DetectionSource = "GetRecentText(80)";
    public const string ExpectedGhosttyCommit = "c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3";
    public const string ExpectedZigVersion = "0.15.2";

    public sealed record ReplayResult(
        VtStructuredSnapshot Snapshot,
        string CanonicalSnapshotJson,
        string DetectionText);

    public sealed record ScriptEvent(
        string Type,
        string? Encoding,
        string? Data,
        int? Cols,
        int? Rows,
        string? Name);

    public static ReplayResult ReplayToCheckpoint(
        string scriptPath,
        int cols,
        int rows,
        string? libraryPath,
        string? checkpointName = null)
    {
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: libraryPath);
        ApplyEvents(vt, ReadScript(scriptPath), checkpointName);
        var snapshot = vt.CaptureSnapshot();
        return new ReplayResult(
            snapshot,
            VtSnapshotNormalizer.ToCanonicalJson(snapshot),
            vt.GetRecentText(DetectionMaxLines));
    }

    public static void ApplyEvents(
        IVtEngine vt,
        IReadOnlyList<ScriptEvent> events,
        string? stopAtCheckpoint = null)
    {
        var seen = false;
        foreach (var ev in events)
        {
            switch (ev.Type)
            {
                case "feed":
                    vt.Feed(DecodeFeed(ev));
                    break;
                case "resize":
                    if (ev.Cols is null || ev.Rows is null)
                        throw new InvalidOperationException("resize event requires cols and rows");
                    vt.Resize(ev.Cols.Value, ev.Rows.Value);
                    break;
                case "checkpoint":
                    if (stopAtCheckpoint is null ||
                        string.Equals(ev.Name, stopAtCheckpoint, StringComparison.Ordinal))
                    {
                        seen = true;
                        return;
                    }

                    break;
                default:
                    throw new InvalidOperationException("Unknown script event type: " + ev.Type);
            }
        }

        if (stopAtCheckpoint is not null && !seen)
            throw new InvalidOperationException("Checkpoint not found: " + stopAtCheckpoint);
    }

    public static IReadOnlyList<ScriptEvent> ReadScript(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing VT script", path);

        var events = new List<ScriptEvent>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            events.Add(new ScriptEvent(
                root.GetProperty("type").GetString() ?? "",
                root.TryGetProperty("encoding", out var enc) ? enc.GetString() : null,
                root.TryGetProperty("data", out var data) ? data.GetString() : null,
                root.TryGetProperty("cols", out var cols) ? cols.GetInt32() : null,
                root.TryGetProperty("rows", out var rows) ? rows.GetInt32() : null,
                root.TryGetProperty("name", out var name) ? name.GetString() : null));
        }

        return events;
    }

    public static byte[] DecodeFeed(ScriptEvent ev)
    {
        if (!string.Equals(ev.Encoding, "base64", StringComparison.Ordinal))
            throw new InvalidOperationException("feed encoding must be base64");
        if (string.IsNullOrEmpty(ev.Data))
            return [];
        return Convert.FromBase64String(ev.Data);
    }

    public static string EncodeFeedLine(ReadOnlySpan<byte> bytes)
    {
        var payload = Convert.ToBase64String(bytes);
        return "{\"type\":\"feed\",\"encoding\":\"base64\",\"data\":\"" + payload + "\"}";
    }

    public static string EncodeFeedLine(string text) =>
        EncodeFeedLine(Encoding.UTF8.GetBytes(text));

    public static string ResizeLine(int cols, int rows) =>
        "{\"type\":\"resize\",\"cols\":" + cols + ",\"rows\":" + rows + "}";

    public static string CheckpointLine(string name) =>
        "{\"type\":\"checkpoint\",\"name\":\"" + name + "\"}";

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx"))
                && File.Exists(Path.Combine(dir.FullName, "skills", "hypa-runtime", "SKILL.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    public static string ResolveFixtureDir(string suite)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vt", suite),
            Path.Combine(FindRepoRoot(), "tests", "Hypa.AgentRuntime.Tests", "Fixtures", "vt", suite),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "meta.json")))
                return c;
        }

        throw new InvalidOperationException("Could not locate fixtures for " + suite);
    }

    public static string? ResolveNativeLibraryPath() =>
        GhosttyTestRequire.TryResolveNativeLibraryPath();

    public static string ReadPinnedGhosttyCommit()
    {
        var pin = File.ReadAllText(Path.Combine(FindRepoRoot(), "native", "ghostty", "PIN.md"));
        const string token = "GHOSTTY_COMMIT=";
        var idx = pin.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0)
            throw new InvalidOperationException("PIN.md missing GHOSTTY_COMMIT");
        var start = idx + token.Length;
        var end = start;
        while (end < pin.Length && !char.IsWhiteSpace(pin[end]))
            end++;
        return pin[start..end].Trim();
    }

    public static JsonElement ReadMeta(string fixtureDir)
    {
        var path = Path.Combine(fixtureDir, "meta.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing meta.json", path);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }
}
