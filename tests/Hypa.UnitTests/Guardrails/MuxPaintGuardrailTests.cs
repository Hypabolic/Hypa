using Xunit;

namespace Hypa.UnitTests.Guardrails;

/// <summary>
/// Agents keep repeating three mux mistakes: merging attach into the mux
/// process, bringing back a second host VT, and seeding scrollback from the
/// wheel instead of calling pane.scroll. The scan fails those shapes.
/// </summary>
public sealed class MuxPaintGuardrailTests
{
    private const string ServerProject = "src/Hypa.AgentServer/Hypa.AgentServer.csproj";
    private const string Program = "src/Hypa.Cli/Program.cs";
    private const string AttachSession = "src/Hypa.Cli/Attach/AttachSession.cs";

    [Fact]
    public void Repository_keeps_mux_paint_on_the_supported_path()
    {
        var violations = Scan(LoadRepository());
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Past_mistakes_fail_the_scan()
    {
        var merged = LoadRepository();
        merged[ServerProject] += "\n<ProjectReference Include=\"..\\Hypa.Cli\\Hypa.Cli.csproj\" />\n";
        Assert.Contains(Scan(merged), line => line.Contains("separate process", StringComparison.Ordinal));

        var remapper = LoadRepository();
        remapper["src/Hypa.Cli/Attach/Chrome/VtBlitChromeRemapper.cs"] =
            "namespace Hypa.Cli.Attach.Chrome;\npublic sealed class VtBlitChromeRemapper {}\n";
        Assert.Contains(Scan(remapper), line => line.Contains("VtBlitChromeRemapper", StringComparison.Ordinal));

        var seeded = LoadRepository();
        seeded[AttachSession] = seeded[AttachSession].Replace(
            "await PublishServerScrollAsync(live, control, scrollPane, nextOffset, ct)",
            "live.History = new PaneHistoryView();",
            StringComparison.Ordinal);
        Assert.Contains(Scan(seeded), line => line.Contains("pane.scroll", StringComparison.Ordinal));

        var attached = LoadRepository();
        attached[Program] = attached[Program].Replace(
            "return await AgentRuntimeHostBuilder.Run(MuxInvocation.ForwardServeArgs(args));",
            "return await AttachHostEntry.Run(args);",
            StringComparison.Ordinal);
        Assert.Contains(Scan(attached), line => line.Contains("separate process", StringComparison.Ordinal));
    }

    internal static List<string> Scan(IReadOnlyDictionary<string, string> files)
    {
        var violations = new List<string>();
        var server = files[ServerProject];
        if (server.Contains("Hypa.Cli", StringComparison.Ordinal)
            || server.Contains("Hypa.AttachHost", StringComparison.Ordinal)
            || server.Contains("Attach\\", StringComparison.Ordinal)
            || server.Contains("Attach/", StringComparison.Ordinal))
        {
            violations.Add(
                ServerProject + ": the mux server must stay a separate process. Do not compile attach into hypa-runtime.");
        }

        var serve = Slice(files[Program], "if (MuxInvocation.IsMuxServe(args))", "if (MuxInvocation.IsAttach(args))");
        if (serve.Contains("AttachHostEntry", StringComparison.Ordinal))
        {
            violations.Add(Program + ": mux serve must not run the attach host. Keep the processes separate.");
        }

        foreach (var (path, text) in files)
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal))
                continue;
            if (text.Contains("class VtBlitChromeRemapper", StringComparison.Ordinal)
                || text.Contains("struct VtBlitChromeRemapper", StringComparison.Ordinal)
                || text.Contains("record VtBlitChromeRemapper", StringComparison.Ordinal))
            {
                violations.Add(path + ": do not reintroduce VtBlitChromeRemapper. Compose one host frame, then encode once.");
            }
        }

        var scroll = Slice(files[AttachSession], "case MouseCommandKind.ScrollHistory", "case MouseCommandKind.SetHistoryTop");
        if (!scroll.Contains("PublishServerScrollAsync", StringComparison.Ordinal)
            || scroll.Contains("PaneHistoryView", StringComparison.Ordinal))
        {
            violations.Add(
                AttachSession + ": wheel scroll calls pane.scroll through PublishServerScrollAsync. Do not seed PaneHistoryView.");
        }

        var publish = Slice(files[AttachSession], "internal static async Task PublishServerScrollAsync", "internal static async Task PublishServerScrollbarAsync");
        if (!publish.Contains("ProtocolMethods.PaneScroll", StringComparison.Ordinal))
        {
            violations.Add(AttachSession + ": PublishServerScrollAsync must call ProtocolMethods.PaneScroll.");
        }

        return violations;
    }

    private static string Slice(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
            return "";
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        if (to < 0)
            return text[from..];
        return text[from..to];
    }

    private static Dictionary<string, string> LoadRepository()
    {
        var root = FindRepoRoot();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in new[] { ServerProject, Program, AttachSession })
            files[relative] = File.ReadAllText(Path.Combine(root, relative));

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                continue;
            files.TryAdd(relative, File.ReadAllText(path));
        }

        return files;
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

        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
