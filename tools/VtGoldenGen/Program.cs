using System.Text;
using System.Text.Json;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using VtGoldenGen;

// Developer generator for VT goldens.
// Usage:
//   dotnet run --project tools/VtGoldenGen -- <g-vt-alt-dir>
//   dotnet run --project tools/VtGoldenGen -- --h15
//   dotnet run --project tools/VtGoldenGen -- --replay <suite-dir>
//   dotnet run --project tools/VtGoldenGen -- --compare <suite-dir>
//   dotnet run --project tools/VtGoldenGen -- --compare-h15

if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
{
    Console.WriteLine("VtGoldenGen --h15 | --from-bin <bin> <dir> <checkpoint> | --record-pty | --replay <dir> | --compare <dir> | --compare-h15 | --sanitize-recorded | <g-vt-alt-dir>");
    return 0;
}

if (args.Length > 0 && args[0] == "--record-pty")
    return PtyRecorder.RunAsync(args).GetAwaiter().GetResult();

if (args.Length > 3 && args[0] == "--from-bin")
    return ImportBin(args[1], args[2], args[3]);

if (args.Length > 0 && args[0] == "--h15")
    return EmitAndReplayH15();

if (args.Length > 0 && args[0] == "--compare-h15")
    return CompareH15();

if (args.Length > 0 && args[0] == "--sanitize-recorded")
    return SanitizeRecorded();

if (args.Length > 1 && args[0] == "--compare")
    return CompareSuite(args[1]);

if (args.Length > 1 && args[0] == "--replay")
    return ReplaySuite(args[1], writeExpected: true);

return GenerateGhosttyAlt(args);

static int GenerateGhosttyAlt(string[] args)
{
    var fixtureDir = args.Length > 0
        ? args[0]
        : Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tests", "Hypa.AgentRuntime.Tests", "Fixtures", "vt", "g-vt-alt-ghostty"));

    if (!Directory.Exists(fixtureDir))
    {
        Console.Error.WriteLine("Fixture dir not found: " + fixtureDir);
        return 1;
    }

    var root = FindRepoRoot();
    var lib = ResolveLib(root);
    if (lib is null)
    {
        Console.Error.WriteLine("libghostty-vt not found. Build it or set HYPA_GHOSTTY_VT.");
        return 1;
    }

    var metaPath = Path.Combine(fixtureDir, "meta.json");
    var metaJson = File.ReadAllText(metaPath);
    using var metaDoc = JsonDocument.Parse(metaJson);
    var cols = metaDoc.RootElement.GetProperty("cols").GetInt32();
    var rows = metaDoc.RootElement.GetProperty("rows").GetInt32();

    foreach (var inputPath in Directory.GetFiles(fixtureDir, "*.input.txt").OrderBy(p => p, StringComparer.Ordinal))
    {
        var name = Path.GetFileName(inputPath).Replace(".input.txt", "", StringComparison.Ordinal);
        var outPath = Path.Combine(fixtureDir, name + ".snapshot.expected.json");
        var input = File.ReadAllText(inputPath);
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: lib);
        vt.Feed(input);
        var json = VtSnapshotNormalizer.ToCanonicalJson(vt.CaptureSnapshot());
        File.WriteAllText(outPath, json + "\n");
        Console.WriteLine("Wrote " + outPath);
    }

    return 0;
}

static int EmitAndReplayH15()
{
    var root = FindRepoRoot();
    var lib = ResolveLib(root);
    if (lib is null)
    {
        Console.Error.WriteLine("libghostty-vt not found. Build it or set HYPA_GHOSTTY_VT.");
        return 1;
    }

    var vtRoot = Path.Combine(root, "tests", "Hypa.AgentRuntime.Tests", "Fixtures", "vt");
    WriteCodexSuite(Path.Combine(vtRoot, "g-agent-codex"));
    WriteClaudeSuite(Path.Combine(vtRoot, "g-agent-claude"));
    WriteDetectSuite(Path.Combine(vtRoot, "g-detect"));
    ReplaySuite(Path.Combine(vtRoot, "g-agent-codex"), writeExpected: true);
    ReplaySuite(Path.Combine(vtRoot, "g-agent-claude"), writeExpected: true);
    ReplaySuite(Path.Combine(vtRoot, "g-detect"), writeExpected: true);
    Console.WriteLine("H-15 fixtures emitted and replayed.");
    return 0;
}

static int CompareH15()
{
    var root = FindRepoRoot();
    var vtRoot = Path.Combine(root, "tests", "Hypa.AgentRuntime.Tests", "Fixtures", "vt");
    var status = 0;
    status |= CompareSuite(Path.Combine(vtRoot, "g-agent-codex"));
    status |= CompareSuite(Path.Combine(vtRoot, "g-agent-claude"));
    status |= CompareSuite(Path.Combine(vtRoot, "g-detect"));
    status |= CompareSuite(Path.Combine(vtRoot, "g-agent-codex-recorded"));
    status |= CompareSuite(Path.Combine(vtRoot, "g-agent-claude-recorded"));
    if (status == 0)
        Console.WriteLine("H-15 AOT compare passed.");
    return status;
}

static int ImportBin(string binPath, string outDir, string name)
{
    if (!File.Exists(binPath))
    {
        Console.Error.WriteLine("Missing recording: " + binPath);
        return 1;
    }

    Directory.CreateDirectory(outDir);
    WriteScriptFromBytes(outDir, name, File.ReadAllBytes(binPath));
    Console.WriteLine("Wrote " + Path.Combine(outDir, name + ".script.ndjson"));
    return 0;
}

static int CompareSuite(string fixtureDir)
{
    fixtureDir = Path.GetFullPath(fixtureDir);
    if (!Directory.Exists(fixtureDir))
    {
        Console.Error.WriteLine("Fixture dir not found: " + fixtureDir);
        return 1;
    }

    var root = FindRepoRoot();
    var lib = ResolveLib(root);
    if (lib is null)
    {
        Console.Error.WriteLine("libghostty-vt not found. Build it or set HYPA_GHOSTTY_VT.");
        return 1;
    }

    using var metaDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDir, "meta.json")));
    var cols = metaDoc.RootElement.GetProperty("cols").GetInt32();
    var rows = metaDoc.RootElement.GetProperty("rows").GetInt32();
    var suite = Path.GetFileName(fixtureDir);
    var failures = 0;

    foreach (var script in Directory.GetFiles(fixtureDir, "*.script.ndjson").OrderBy(p => p, StringComparer.Ordinal))
    {
        var name = Path.GetFileName(script).Replace(".script.ndjson", "", StringComparison.Ordinal);
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: lib);
        ApplyScript(vt, script);

        var snapPath = Path.Combine(fixtureDir, name + ".snapshot.expected.json");
        if (File.Exists(snapPath))
        {
            var actual = VtSnapshotNormalizer.ToCanonicalJson(vt.CaptureSnapshot());
            var expected = VtSnapshotNormalizer.ToCanonicalJson(
                VtSnapshotNormalizer.FromJson(File.ReadAllText(snapPath)));
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("SNAPSHOT MISMATCH " + suite + "/" + name);
                failures++;
            }
            else
            {
                Console.WriteLine("OK snapshot " + suite + "/" + name);
            }
        }
        else if (suite != "g-detect")
        {
            Console.Error.WriteLine("MISSING SNAPSHOT " + suite + "/" + name);
            failures++;
        }

        var detectPath = Path.Combine(fixtureDir, name + ".detection.expected.json");
        if (File.Exists(detectPath))
            Console.WriteLine("Detection text (" + name + "):\n" + vt.GetRecentText(80));
    }

    return failures == 0 ? 0 : 1;
}

static int ReplaySuite(string fixtureDir, bool writeExpected)
{
    fixtureDir = Path.GetFullPath(fixtureDir);
    if (!Directory.Exists(fixtureDir))
    {
        Console.Error.WriteLine("Fixture dir not found: " + fixtureDir);
        return 1;
    }

    var root = FindRepoRoot();
    var lib = ResolveLib(root);
    if (lib is null)
    {
        Console.Error.WriteLine("libghostty-vt not found. Build it or set HYPA_GHOSTTY_VT.");
        return 1;
    }

    using var metaDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDir, "meta.json")));
    var cols = metaDoc.RootElement.GetProperty("cols").GetInt32();
    var rows = metaDoc.RootElement.GetProperty("rows").GetInt32();
    var suite = Path.GetFileName(fixtureDir);

    foreach (var script in Directory.GetFiles(fixtureDir, "*.script.ndjson"))
    {
        var name = Path.GetFileName(script).Replace(".script.ndjson", "", StringComparison.Ordinal);
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: lib);
        ApplyScript(vt, script);

        var writeSnapshot = writeExpected && (suite != "g-detect" || DetectWritesSnapshot(name));
        if (writeSnapshot)
        {
            var snapPath = Path.Combine(fixtureDir, name + ".snapshot.expected.json");
            var json = VtSnapshotNormalizer.ToCanonicalJson(vt.CaptureSnapshot());
            File.WriteAllText(snapPath, json + "\n", new UTF8Encoding(false));
            Console.WriteLine("Wrote " + snapPath);
        }

        var detectPath = Path.Combine(fixtureDir, name + ".detection.expected.json");
        if (File.Exists(detectPath))
        {
            Console.WriteLine("Detection text (" + name + "):");
            Console.WriteLine(vt.GetRecentText(80));
        }
    }

    return 0;
}

static void ApplyScript(GhosttyVtEngine vt, string script)
{
    foreach (var line in File.ReadAllLines(script))
    {
        if (string.IsNullOrWhiteSpace(line))
            continue;
        using var doc = JsonDocument.Parse(line);
        var rootEl = doc.RootElement;
        var type = rootEl.GetProperty("type").GetString();
        switch (type)
        {
            case "feed":
                var data = rootEl.GetProperty("data").GetString() ?? "";
                vt.Feed(Convert.FromBase64String(data));
                break;
            case "resize":
                vt.Resize(rootEl.GetProperty("cols").GetInt32(), rootEl.GetProperty("rows").GetInt32());
                break;
            case "checkpoint":
                break;
            default:
                throw new InvalidOperationException("Unknown event " + type);
        }
    }
}

static bool DetectWritesSnapshot(string name) =>
    name is "chrome-kind-codex" or "chrome-kind-claude"
        or "leftover-working-idle" or "leftover-working-done"
        or "leftover-blocked-idle" or "leftover-ellipsis-idle"
        or "wrong-kind";

static void WriteCodexSuite(string dir)
{
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "meta.json"), """
        {
          "suite": "g-agent-codex",
          "floor": "F2",
          "provider": "ghostty",
          "cols": 120,
          "rows": 40,
          "term": "xterm-256color",
          "lang": "C.UTF-8",
          "tz": "UTC",
          "agent_name": "codex",
          "agent_version": "0.147.0",
          "process_name": "codex",
          "ghostty_commit": "c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3",
          "zig_version": "0.15.2",
          "fixture_schema": 1,
          "capture_kind": "reconstructed_chrome",
          "sanitized": true,
          "reconstruction_note": "Not an offline PTY recording. Generic 120x40 box TUI. Not captured from Codex 0.147.0.",
          "checkpoints": ["working", "blocked", "idle", "done"]
        }
        """ + "\n");

    WriteScript(dir, "working", H15Chrome.CodexWorking(), includeModes: true, includeResize: true);
    WriteDetection(dir, "working", "working", "codex", 0.55, processName: "codex");
    WriteScript(dir, "blocked", H15Chrome.CodexBlocked());
    WriteDetection(dir, "blocked", "blocked", "codex", 0.70, processName: "codex");
    WriteScript(dir, "idle", H15Chrome.CodexIdle());
    WriteDetection(dir, "idle", "idle", "codex", 0.45, processName: null);
    WriteScript(dir, "done", H15Chrome.CodexDone());
    WriteDetection(dir, "done", "done", "codex", 0.50, processName: "codex");
}

static void WriteClaudeSuite(string dir)
{
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "meta.json"), """
        {
          "suite": "g-agent-claude",
          "floor": "F2",
          "provider": "ghostty",
          "cols": 120,
          "rows": 40,
          "term": "xterm-256color",
          "lang": "C.UTF-8",
          "tz": "UTC",
          "agent_name": "claude",
          "agent_version": "2.1.233",
          "process_name": "claude",
          "ghostty_commit": "c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3",
          "zig_version": "0.15.2",
          "fixture_schema": 1,
          "capture_kind": "reconstructed_chrome",
          "sanitized": true,
          "reconstruction_note": "Not an offline PTY recording. Generic 120x40 box TUI. Not captured from Claude Code 2.1.233.",
          "checkpoints": ["working", "blocked", "idle", "done"]
        }
        """ + "\n");

    WriteScript(dir, "working", H15Chrome.ClaudeWorking(), includeModes: true, includeResize: true);
    WriteDetection(dir, "working", "working", "claude", 0.55, processName: "claude");
    WriteScript(dir, "blocked", H15Chrome.ClaudeBlocked());
    WriteDetection(dir, "blocked", "blocked", "claude", 0.70, processName: "claude");
    WriteScript(dir, "idle", H15Chrome.ClaudeIdle());
    WriteDetection(dir, "idle", "idle", "claude", 0.45, processName: null);
    WriteScript(dir, "done", H15Chrome.ClaudeDone());
    WriteDetection(dir, "done", "done", "claude", 0.50, processName: "claude");
}

static void WriteDetectSuite(string dir)
{
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "meta.json"), """
        {
          "suite": "g-detect",
          "floor": "F2",
          "provider": "ghostty",
          "cols": 120,
          "rows": 40,
          "term": "xterm-256color",
          "lang": "C.UTF-8",
          "tz": "UTC",
          "agent_name": "mixed",
          "agent_version": "mixed",
          "process_name": "mixed",
          "ghostty_commit": "c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3",
          "zig_version": "0.15.2",
          "fixture_schema": 1,
          "capture_kind": "reconstructed_chrome",
          "sanitized": true,
          "reconstruction_note": "Unique labelled scenes. Not clones of G-agent scripts.",
          "checkpoints": [
            "chrome-kind-codex",
            "chrome-kind-claude",
            "leftover-working-idle",
            "leftover-working-done",
            "leftover-blocked-idle",
            "leftover-ellipsis-idle",
            "unknown-idle",
            "stale-thinking",
            "shell-only",
            "wrong-kind",
            "completed-count"
          ]
        }
        """ + "\n");

    WriteScript(dir, "chrome-kind-codex", H15Chrome.ChromeKindCodex(), includeModes: true);
    WriteDetection(dir, "chrome-kind-codex", "working", "codex", 0.55, processName: null);
    WriteScript(dir, "chrome-kind-claude", H15Chrome.ChromeKindClaude());
    WriteDetection(dir, "chrome-kind-claude", "working", "claude", 0.55, processName: null);
    WriteScript(dir, "leftover-working-idle", H15Chrome.LeftoverWorkingIdle());
    WriteDetection(dir, "leftover-working-idle", "idle", "codex", 0.45, processName: null);
    WriteScript(dir, "leftover-working-done", H15Chrome.LeftoverWorkingDone());
    WriteDetection(dir, "leftover-working-done", "done", "claude", 0.50, processName: null);
    WriteScript(dir, "leftover-blocked-idle", H15Chrome.LeftoverBlockedIdle());
    WriteDetection(dir, "leftover-blocked-idle", "idle", "codex", 0.45, processName: null);
    WriteScript(dir, "leftover-ellipsis-idle", H15Chrome.LeftoverEllipsisIdle());
    WriteDetection(dir, "leftover-ellipsis-idle", "idle", "claude", 0.45, processName: null);
    WriteScript(dir, "unknown-idle", H15Chrome.UnknownIdle());
    WriteDetection(dir, "unknown-idle", "unknown", null, 0.0, maxConfidence: 0.0, processName: null);
    WriteScript(dir, "stale-thinking", H15Chrome.StaleThinking());
    WriteDetection(dir, "stale-thinking", "unknown", null, 0.0, maxConfidence: 0.0, processName: null);
    WriteScript(dir, "shell-only", H15Chrome.ShellOnly());
    WriteDetection(dir, "shell-only", "idle", "shell", 0.45, processName: null);
    WriteScript(dir, "wrong-kind", H15Chrome.CursorWorking());
    WriteDetection(dir, "wrong-kind", "working", "cursor", 0.55, processName: "cursor");
    WriteScript(dir, "completed-count", H15Chrome.CompletedCountInProgress());
    WriteDetection(dir, "completed-count", "unknown", "codex", 0.0, maxConfidence: 0.0, processName: null);
}

static void WriteScript(
    string dir,
    string name,
    string chrome,
    bool includeModes = false,
    bool includeResize = false)
{
    var sb = new StringBuilder();
    if (includeResize)
    {
        sb.AppendLine(ResizeJson(80, 24));
        sb.AppendLine(FeedJson("pre-resize"));
    }

    sb.AppendLine(ResizeJson(120, 40));
    if (includeModes)
        sb.AppendLine(FeedJson("\u001b[?2004h\u001b[?1000h\u001b[?1006h"));
    sb.AppendLine(FeedJson(chrome));
    sb.AppendLine(CheckpointJson(name));
    File.WriteAllText(
        Path.Combine(dir, name + ".script.ndjson"),
        sb.ToString(),
        new UTF8Encoding(false));
}

static void WriteScriptFromBytes(string dir, string name, byte[] bytes)
{
    var sb = new StringBuilder();
    sb.AppendLine(ResizeJson(120, 40));
    sb.AppendLine(FeedJsonBytes(bytes));
    sb.AppendLine(CheckpointJson(name));
    File.WriteAllText(
        Path.Combine(dir, name + ".script.ndjson"),
        sb.ToString(),
        new UTF8Encoding(false));
}

static string FeedJsonBytes(byte[] bytes)
{
    var b64 = Convert.ToBase64String(bytes);
    return "{\"type\":\"feed\",\"encoding\":\"base64\",\"data\":\"" + b64 + "\"}";
}

static void WriteDetection(
    string dir,
    string name,
    string status,
    string? kind,
    double minConfidence,
    double? maxConfidence = null,
    string? processName = null)
{
    var kindJson = kind is null ? "null" : "\"" + kind + "\"";
    var processJson = processName is null ? "null" : "\"" + processName + "\"";
    var maxJson = maxConfidence is null
        ? ""
        : ",\n  \"max_confidence\": " + maxConfidence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var json =
        "{\n"
        + "  \"status\": \"" + status + "\",\n"
        + "  \"agent_kind\": " + kindJson + ",\n"
        + "  \"min_confidence\": " + minConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + maxJson + ",\n"
        + "  \"process_name\": " + processJson + ",\n"
        + "  \"detection_source\": \"GetRecentText(80)\"\n"
        + "}\n";
    File.WriteAllText(Path.Combine(dir, name + ".detection.expected.json"), json, new UTF8Encoding(false));
}

static string FeedJson(string text)
{
    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    return "{\"type\":\"feed\",\"encoding\":\"base64\",\"data\":\"" + b64 + "\"}";
}

static string ResizeJson(int cols, int rows) =>
    "{\"type\":\"resize\",\"cols\":" + cols + ",\"rows\":" + rows + "}";

static string CheckpointJson(string name) =>
    "{\"type\":\"checkpoint\",\"name\":\"" + name + "\"}";

static string? ResolveLib(string root)
{
    var env = Environment.GetEnvironmentVariable("HYPA_GHOSTTY_VT");
    if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        return Path.GetFullPath(env);

    foreach (var rid in new[] { "osx-arm64", "osx-x64", "linux-arm64", "linux-x64" })
    {
        foreach (var name in new[] { "libghostty-vt.dylib", "libghostty-vt.so" })
        {
            var path = Path.Combine(root, "native", "runtimes", rid, "native", name);
            if (File.Exists(path))
                return path;
        }
    }

    return GhosttyLibraryLoader.FindExistingLibraryPath(pathOverride: null);
}

static int SanitizeRecorded()
{
    var root = FindRepoRoot();
    var vtRoot = Path.Combine(root, "tests", "Hypa.AgentRuntime.Tests", "Fixtures", "vt");
    var status = 0;
    status |= SanitizeSuite(Path.Combine(vtRoot, "g-agent-codex-recorded"));
    status |= SanitizeSuite(Path.Combine(vtRoot, "g-agent-claude-recorded"));
    return status;
}

static int SanitizeSuite(string dir)
{
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine("Missing suite " + dir);
        return 1;
    }

    foreach (var script in Directory.GetFiles(dir, "*.script.ndjson").OrderBy(p => p, StringComparer.Ordinal))
    {
        var lines = File.ReadAllLines(script);
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var doc = JsonDocument.Parse(line);
            var rootEl = doc.RootElement;
            if (rootEl.GetProperty("type").GetString() != "feed")
                continue;
            var data = rootEl.GetProperty("data").GetString() ?? "";
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(data));
            var sanitized = Hypa.Terminal.Vt.RecordedFeedSanitizer.Sanitize(text);
            if (Encoding.UTF8.GetByteCount(sanitized) != Encoding.UTF8.GetByteCount(text))
            {
                Console.Error.WriteLine("Sanitize changed UTF-8 length in " + script);
                return 1;
            }

            if (!string.Equals(sanitized, text, StringComparison.Ordinal))
            {
                lines[i] = FeedJson(sanitized);
                changed = true;
            }
        }

        if (changed)
        {
            File.WriteAllLines(script, lines, new UTF8Encoding(false));
            Console.WriteLine("Sanitized " + script);
        }
        else
        {
            Console.WriteLine("Clean " + script);
        }
    }

    return 0;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new InvalidOperationException("repo root not found");
}
