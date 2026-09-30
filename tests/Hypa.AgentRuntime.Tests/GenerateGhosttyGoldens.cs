using System.Text;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// One-shot golden generator for G-VT-alt-ghostty. Run with
/// GENERATE_GHOSTTY_GOLDENS=1. Skipped otherwise.
/// </summary>
public class GenerateGhosttyGoldens
{
    [SkippableFact]
    public void Generate_when_env_set()
    {
        Skip.If(
            Environment.GetEnvironmentVariable("GENERATE_GHOSTTY_GOLDENS") != "1",
            "Set GENERATE_GHOSTTY_GOLDENS=1 to regenerate fixtures.");

        var lib = Environment.GetEnvironmentVariable("HYPA_GHOSTTY_VT");
        Skip.If(string.IsNullOrWhiteSpace(lib) || !File.Exists(lib!), "HYPA_GHOSTTY_VT required");

        var root = FindRepoRoot();
        var dir = Path.Combine(root, "tests", "Hypa.AgentRuntime.Tests", "Fixtures", "vt", "g-vt-alt-ghostty");
        Directory.CreateDirectory(dir);

        const int cols = 20;
        const int rows = 6;

        File.WriteAllText(Path.Combine(dir, "meta.json"), """
            {
              "suite": "g-vt-alt-ghostty",
              "floor": "F2",
              "provider": "ghostty",
              "cols": 20,
              "rows": 6,
              "note": "Synthetic G-VT-alt cases on GhosttyVtEngine. Structured equality against CaptureSnapshot. Generated under controlled local run."
            }
            """);

        WriteCase(dir, "sgr-bold-red", "\u001b[1;31mR\u001b[0m", cols, rows, lib!);
        WriteCase(dir, "alt-screen", "main\u001b[?1049h\u001b[HALT", cols, rows, lib!);
        WriteCase(dir, "scroll-region", "\u001b[2;5rtop", cols, rows, lib!);
        WriteCase(dir, "wide-char", "中文", cols, rows, lib!);
        WriteCase(dir, "cursor-cup", "\u001b[3;5HX", cols, rows, lib!);
        WriteCase(dir, "erase-cells", "HELLO\u001b[1;3H\u001b[K", cols, rows, lib!);
    }

    private static void WriteCase(string dir, string name, string input, int cols, int rows, string lib)
    {
        // Store input as UTF-8 text with real ESC bytes.
        var inputPath = Path.Combine(dir, name + ".input.txt");
        File.WriteAllText(inputPath, input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: lib);
        vt.Feed(input);
        var json = VtSnapshotNormalizer.ToCanonicalJson(vt.CaptureSnapshot());
        File.WriteAllText(
            Path.Combine(dir, name + ".snapshot.expected.json"),
            json + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string FindRepoRoot()
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
}
