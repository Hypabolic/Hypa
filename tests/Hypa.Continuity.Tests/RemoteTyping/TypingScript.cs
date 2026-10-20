using System.Text.Json;
using Xunit;

namespace Hypa.Continuity.Tests.RemoteTyping;

/// <summary>
/// One recorded typing fixture of the typing conformance harness. Each key
/// has the bytes that the recorder wrote and the reads that the application
/// echoed for it.
/// </summary>
internal sealed record TypingScript(string Name, int Cols, int Rows, IReadOnlyList<TypingScriptKey> Keys)
{
    public const string FixtureDirectory = "tests/Hypa.AgentRuntime.Tests/Fixtures/typing";

    public static readonly IReadOnlyList<string> AllFixtures =
    [
        "bash-norc",
        "zsh-f",
        "zsh-p10k",
        "claude-code",
        "vim-insert",
        "sync-echo-tui",
    ];

    /// <summary>The startup output, then each typed key.</summary>
    public IEnumerable<TypingScriptKey> TypedKeys => Keys.Where(key => key.Input.Length > 0);

    public IEnumerable<TypingScriptKey> StartupKeys => Keys.Where(key => key.Input.Length == 0);

    public static TypingScript Load(string name)
    {
        var path = Path.Combine(FindRepoRoot(), FixtureDirectory, name + ".jsonl");
        var cols = 120;
        var rows = 40;
        var keys = new List<TypingScriptKey>();
        List<TypingScriptChunk>? current = null;
        string? label = null;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("meta", out var meta))
            {
                cols = meta.GetProperty("cols").GetInt32();
                rows = meta.GetProperty("rows").GetInt32();
                continue;
            }

            var key = root.GetProperty("key").GetString()!;
            // A line with "input" starts a new key write. Later lines with no
            // input are more reads for the same key.
            byte[] input = [];
            if (root.TryGetProperty("input", out var inputElement))
                input = Convert.FromBase64String(inputElement.GetString()!);
            if (current is null || input.Length > 0 || key != label)
            {
                current = [];
                label = key;
                keys.Add(new TypingScriptKey(key, input, current));
            }

            var bytes = Convert.FromBase64String(root.GetProperty("chunk").GetString()!);
            if (bytes.Length > 0)
                current.Add(new TypingScriptChunk(bytes, Math.Clamp(root.GetProperty("t_ms").GetDouble(), 0, 1000)));
        }

        return new TypingScript(name, cols, rows, keys);
    }

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        Assert.Fail("Hypa.slnx not found above " + AppContext.BaseDirectory);
        return "";
    }
}

/// <summary>One key: the bytes the recorder wrote and the reads of its echo.</summary>
internal sealed record TypingScriptKey(string Label, byte[] Input, IReadOnlyList<TypingScriptChunk> Chunks);

/// <summary>One read of the echo, at <see cref="Ms"/> after the key write.</summary>
internal sealed record TypingScriptChunk(byte[] Bytes, double Ms);
