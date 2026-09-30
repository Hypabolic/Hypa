using Hypa.Infrastructure.Runner;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class WindowsExecutableResolverTests
{
    private static readonly string[] DefaultExt = [".COM", ".EXE", ".BAT", ".CMD"];

    [Fact]
    public void ResolvePath_PrefersCmdOverExtensionlessSibling()
    {
        using var fixture = PathFixture.Create();
        fixture.Write("fake-tool", "#!/bin/sh\necho bare\n");
        fixture.Write("fake-tool.cmd", "@echo marker-cmd\n");

        var resolved = WindowsExecutableResolver.ResolvePath(
            "fake-tool",
            workingDirectory: null,
            pathEnv: fixture.Directory,
            pathextExtensions: DefaultExt);

        Assert.NotNull(resolved);
        Assert.EndsWith("fake-tool.cmd", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(WindowsExecutableResolver.IsBatchFile(resolved!));
    }

    [Fact]
    public void ResolvePath_PrefersExeOverCmd_ByPathextOrder()
    {
        using var fixture = PathFixture.Create();
        fixture.Write("fake-tool.cmd", "@echo from-cmd\n");
        fixture.Write("fake-tool.exe", "not-a-real-pe");

        var resolved = WindowsExecutableResolver.ResolvePath(
            "fake-tool",
            workingDirectory: null,
            pathEnv: fixture.Directory,
            pathextExtensions: DefaultExt);

        Assert.NotNull(resolved);
        Assert.EndsWith("fake-tool.exe", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.False(WindowsExecutableResolver.IsBatchFile(resolved!));
    }

    [Fact]
    public void ResolvePath_ExtensionlessOnly_ReturnsNull()
    {
        using var fixture = PathFixture.Create();
        fixture.Write("lonely-shim", "#!/bin/sh\necho no\n");

        var resolved = WindowsExecutableResolver.ResolvePath(
            "lonely-shim",
            workingDirectory: null,
            pathEnv: fixture.Directory,
            pathextExtensions: DefaultExt);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolvePath_ExactExtensionMatch_FindsExe()
    {
        using var fixture = PathFixture.Create();
        fixture.Write("tool.exe", "pe");
        fixture.Write("tool.cmd", "@echo cmd\n");

        var resolved = WindowsExecutableResolver.ResolvePath(
            "tool.exe",
            workingDirectory: null,
            pathEnv: fixture.Directory,
            pathextExtensions: DefaultExt);

        Assert.NotNull(resolved);
        Assert.EndsWith("tool.exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePath_SearchesWorkingDirectoryBeforePath()
    {
        using var cwd = PathFixture.Create();
        using var pathDir = PathFixture.Create();
        cwd.Write("tool.cmd", "@echo from-cwd\n");
        pathDir.Write("tool.cmd", "@echo from-path\n");

        var resolved = WindowsExecutableResolver.ResolvePath(
            "tool",
            workingDirectory: cwd.Directory,
            pathEnv: pathDir.Directory,
            pathextExtensions: DefaultExt);

        Assert.NotNull(resolved);
        Assert.StartsWith(cwd.Directory, resolved!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePath_PathWithDirectory_NoExtension_AppliesPathext()
    {
        using var fixture = PathFixture.Create();
        fixture.Write("nested.cmd", "@echo nested\n");
        var basePath = Path.Combine(fixture.Directory, "nested");

        var resolved = WindowsExecutableResolver.ResolvePath(
            basePath,
            workingDirectory: null,
            pathEnv: string.Empty,
            pathextExtensions: DefaultExt);

        Assert.NotNull(resolved);
        Assert.EndsWith("nested.cmd", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePath_PathWithDirectoryAndExtension_DoesNotInventPathext()
    {
        using var fixture = PathFixture.Create();
        fixture.Write("tool.cmd", "@echo cmd\n");
        var wrong = Path.Combine(fixture.Directory, "tool.exe");

        var resolved = WindowsExecutableResolver.ResolvePath(
            wrong,
            workingDirectory: null,
            pathEnv: string.Empty,
            pathextExtensions: DefaultExt);

        Assert.Null(resolved);
    }

    [Fact]
    public void ParsePathext_DefaultWhenEmpty()
    {
        var parsed = WindowsExecutableResolver.ParsePathext(null);
        Assert.Equal(WindowsExecutableResolver.DefaultPathext, parsed);

        parsed = WindowsExecutableResolver.ParsePathext("");
        Assert.Equal(WindowsExecutableResolver.DefaultPathext, parsed);
    }

    [Fact]
    public void ParsePathext_NormalizesMissingDots()
    {
        var parsed = WindowsExecutableResolver.ParsePathext(".EXE;BAT;cmd");
        Assert.Equal([".EXE", ".BAT", ".cmd"], parsed);
    }

    [Fact]
    public void QuoteCmdArgument_QuotesSpacesAndEmpty()
    {
        Assert.Equal("\"\"", WindowsExecutableResolver.QuoteCmdArgument(""));
        Assert.Equal("plain", WindowsExecutableResolver.QuoteCmdArgument("plain"));
        Assert.Equal("\"a b\"", WindowsExecutableResolver.QuoteCmdArgument("a b"));
        Assert.Equal("\"say \"\"hi\"\"\"", WindowsExecutableResolver.QuoteCmdArgument("say \"hi\""));
    }

    [Fact]
    public void QuoteCmdArgument_DoublesPercentToPreventEnvExpansion()
    {
        // Direct CreateProcess would pass %PATH% literally; cmd would expand it.
        Assert.Equal("%%PATH%%", WindowsExecutableResolver.QuoteCmdArgument("%PATH%"));
        Assert.Equal("\"a %%b%% c\"", WindowsExecutableResolver.QuoteCmdArgument("a %b% c"));
    }

    [Fact]
    public void QuoteCmdArgument_DoublesTrailingBackslashBeforeClosingQuote()
    {
        // Spaces force quoting; trailing \ must be doubled so it does not escape the closer.
        Assert.Equal("\"C:\\Program Files\\tool\\\\\"", WindowsExecutableResolver.QuoteCmdArgument(@"C:\Program Files\tool\"));
    }

    [Fact]
    public void BuildCmdCArgument_IncludesQuotedExecutableAndArgs()
    {
        var line = WindowsExecutableResolver.BuildCmdCArgument(
            @"C:\Program Files\tool.cmd",
            ["install", "pkg name"]);

        Assert.Equal("\"C:\\Program Files\\tool.cmd\" install \"pkg name\"", line);
    }

    [Fact]
    public void ResolvePath_SearchesProcessCwdWhenWorkingDirectoryNull()
    {
        using var fixture = PathFixture.Create();
        fixture.Write("cwd-tool.cmd", "@echo from-process-cwd\n");

        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(fixture.Directory);
            var resolved = WindowsExecutableResolver.ResolvePath(
                "cwd-tool",
                workingDirectory: null,
                pathEnv: "/nonexistent-path-xyz",
                pathextExtensions: DefaultExt);

            Assert.NotNull(resolved);
            Assert.EndsWith("cwd-tool.cmd", resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void ResolvePath_EmptyPathSegmentMeansCurrentDirectory()
    {
        using var fixture = PathFixture.Create();
        fixture.Write("empty-seg.cmd", "@echo empty-seg\n");

        // Leading empty segment: ";C:\other" → cwd then other.
        var pathEnv = Path.PathSeparator + Path.Combine(Path.GetTempPath(), "no-such-hypa-dir");
        var resolved = WindowsExecutableResolver.ResolvePath(
            "empty-seg",
            workingDirectory: fixture.Directory,
            pathEnv: pathEnv,
            pathextExtensions: DefaultExt);

        Assert.NotNull(resolved);
        Assert.EndsWith("empty-seg.cmd", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplySpawnPlan_WrappedInCmd_UsesRawArgumentsNotArgumentList()
    {
        // Regression: ArgumentList re-escapes quotes and breaks "C:\Program Files\…\npm.cmd".
        var cmdLine = WindowsExecutableResolver.BuildCmdCArgument(
            @"C:\Program Files\nodejs\npm.cmd",
            ["install", "pkg name"]);
        var plan = new WindowsExecutableResolver.SpawnPlan(
            FileName: @"C:\Windows\System32\cmd.exe",
            Arguments: ["/d", "/s", "/c", cmdLine],
            WrappedInCmd: true,
            ResolvedPath: @"C:\Program Files\nodejs\npm.cmd");

        var psi = new global::System.Diagnostics.ProcessStartInfo();
        WindowsExecutableResolver.ApplySpawnPlan(psi, plan);

        Assert.Equal(@"C:\Windows\System32\cmd.exe", psi.FileName);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal("/d /s /c " + cmdLine, psi.Arguments);
        Assert.Contains(@"C:\Program Files\nodejs\npm.cmd", psi.Arguments, StringComparison.Ordinal);
        Assert.Contains("\"pkg name\"", psi.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplySpawnPlan_Direct_UsesArgumentList()
    {
        var plan = new WindowsExecutableResolver.SpawnPlan(
            FileName: @"C:\tools\widget.exe",
            Arguments: ["-v", "a b"],
            WrappedInCmd: false,
            ResolvedPath: @"C:\tools\widget.exe");

        var psi = new global::System.Diagnostics.ProcessStartInfo();
        WindowsExecutableResolver.ApplySpawnPlan(psi, plan);

        Assert.Equal(@"C:\tools\widget.exe", psi.FileName);
        Assert.Equal(2, psi.ArgumentList.Count);
        Assert.Equal("-v", psi.ArgumentList[0]);
        Assert.Equal("a b", psi.ArgumentList[1]);
        Assert.True(string.IsNullOrEmpty(psi.Arguments));
    }

    [Fact]
    public void Resolve_NonWindows_IsPassthrough()
    {
        if (OperatingSystem.IsWindows())
            return; // passthrough path is for non-Windows hosts

        var plan = WindowsExecutableResolver.Resolve("npm", ["install"], workingDirectory: null, envOverrides: null);
        Assert.Equal("npm", plan.FileName);
        Assert.Equal(["install"], plan.Arguments);
        Assert.False(plan.WrappedInCmd);
        Assert.Null(plan.ResolvedPath);
    }

    [Fact]
    public void Resolve_Windows_WrapsCmdAndPreservesLogicalNameSeparately()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fixture = PathFixture.Create();
        fixture.Write("fake-tool.cmd", "@echo marker-from-cmd\r\n");

        var env = new Dictionary<string, string>
        {
            ["PATH"] = fixture.Directory,
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
        };

        var plan = WindowsExecutableResolver.Resolve(
            "fake-tool",
            ["arg1"],
            workingDirectory: null,
            envOverrides: env);

        Assert.True(plan.WrappedInCmd);
        Assert.True(
            plan.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase),
            $"Expected cmd.exe path, got: {plan.FileName}");
        Assert.Equal(4, plan.Arguments.Count);
        Assert.Equal("/d", plan.Arguments[0]);
        Assert.Equal("/s", plan.Arguments[1]);
        Assert.Equal("/c", plan.Arguments[2]);
        Assert.Contains("fake-tool.cmd", plan.Arguments[3], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arg1", plan.Arguments[3], StringComparison.Ordinal);

        // Logical invocation name stays bare for compressors.
        var inv = CommandInvocation.Buffered("fake-tool", ["arg1"], "fake-tool arg1");
        Assert.Equal("fake-tool", inv.Executable);
    }

    [Fact]
    public void Resolve_Windows_ExeNotWrapped()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fixture = PathFixture.Create();
        // Real PE not required for resolution; existence is enough.
        fixture.Write("widget.exe", "x");

        var env = new Dictionary<string, string>
        {
            ["PATH"] = fixture.Directory,
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
        };

        var plan = WindowsExecutableResolver.Resolve(
            "widget",
            ["-v"],
            workingDirectory: null,
            envOverrides: env);

        Assert.False(plan.WrappedInCmd);
        Assert.NotNull(plan.ResolvedPath);
        Assert.EndsWith("widget.exe", plan.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["-v"], plan.Arguments);
    }

    [Fact]
    public async Task ProcessCommandRunner_Windows_SpawnsCmdShimAndCapturesStdout()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fixture = PathFixture.Create();
        fixture.Write("fake-tool", "not-executable-shim");
        fixture.Write("fake-tool.cmd", "@echo marker-cmd\r\n");

        var runner = new ProcessCommandRunner();
        var inv = new CommandInvocation
        {
            Executable = "fake-tool",
            Arguments = [],
            OriginalCommand = "fake-tool",
            Mode = ToolRunMode.Buffered,
            Timeout = TimeSpan.FromSeconds(10),
            EnvOverrides = new Dictionary<string, string>
            {
                ["PATH"] = fixture.Directory,
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            },
        };

        var result = await runner.RunAsync(inv, CancellationToken.None);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        Assert.Contains("marker-cmd", result.Value.Stdout);
        Assert.Equal(0, result.Value.ExitCode);
        // Logical executable preserved for filters/compressors.
        Assert.Equal("fake-tool", inv.Executable);
    }

    [Fact]
    public async Task ProcessCommandRunner_Windows_MissingBareName_FailsStartClearly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fixture = PathFixture.Create();
        fixture.Write("lonely-shim", "not-a-pe");

        var runner = new ProcessCommandRunner();
        var inv = new CommandInvocation
        {
            Executable = "lonely-shim",
            Arguments = [],
            OriginalCommand = "lonely-shim",
            Mode = ToolRunMode.Buffered,
            Timeout = TimeSpan.FromSeconds(5),
            EnvOverrides = new Dictionary<string, string>
            {
                ["PATH"] = fixture.Directory,
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            },
        };

        var result = await runner.RunAsync(inv, CancellationToken.None);
        Assert.False(result.IsOk);
        Assert.Equal("PROCESS_START_FAILED", result.Error.Code);
    }

    private sealed class PathFixture : IDisposable
    {
        public string Directory { get; }

        private PathFixture(string directory) => Directory = directory;

        public static PathFixture Create()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-pathext-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            return new PathFixture(dir);
        }

        public void Write(string name, string contents)
        {
            File.WriteAllText(Path.Combine(Directory, name), contents);
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                    System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
