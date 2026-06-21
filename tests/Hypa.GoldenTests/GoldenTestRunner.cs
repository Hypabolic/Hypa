using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Xunit;

namespace Hypa.GoldenTests;

public sealed partial class GoldenTestRunner
{
    private static readonly string FixturesPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Fixtures");

    private static readonly string SolutionRoot = FindSolutionRoot();

    // The CLI is built in the same configuration as this test assembly via the
    // build-order ProjectReference, so `dotnet run --no-build` must target that
    // same configuration (it would otherwise default to Debug and fail to find a
    // Release-only CI build).
    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Theory]
    [InlineData("doctor_output")]
    [InlineData("config_show_output")]
    [InlineData("run_c_echo")]
    [InlineData("run_c_exit_code")]
    [InlineData("run_c_pipe")]
    [InlineData("run_raw_echo")]
    [InlineData("run_t_echo")]
    public async Task Run(string fixtureName)
    {
        var fixtureDir = Path.Combine(FixturesPath, fixtureName);
        var args = (await File.ReadAllTextAsync(Path.Combine(fixtureDir, "input.txt"))).Trim();
        args = AdjustArgsForPlatform(fixtureName, args);
        var metaJson = await File.ReadAllTextAsync(Path.Combine(fixtureDir, "meta.json"));
        var meta = JsonSerializer.Deserialize(metaJson, GoldenMetaJsonContext.Default.GoldenMeta)!;

        var (stdout, stderr, exitCode, timedOut, timeout) = await RunHypaAsync(args);

        var diag = $"\nFixture: {fixtureName}\nArgs: {args}\nExit code: {exitCode}\nStdout:\n{stdout}\nStderr:\n{stderr}";

        Assert.True(
            !timedOut,
            $"Command timed out after {timeout.TotalSeconds:0.###} seconds.{diag}");

        Assert.True(
            meta.ExpectedExitCode == exitCode,
            $"Exit code mismatch. Expected {meta.ExpectedExitCode}, got {exitCode}.{diag}");

        var normalizedStdout = Normalize(stdout);
        var normalizedStderr = Normalize(stderr);

        Assert.True(
            !string.IsNullOrWhiteSpace(meta.ExpectedStdoutFile),
            $"Command fixture must specify expected_stdout_file.{diag}");

        var stdoutGoldenPath = Path.Combine(fixtureDir, meta.ExpectedStdoutFile);
        var expectedStdout = (await File.ReadAllTextAsync(stdoutGoldenPath))
            .Replace("\r\n", "\n").TrimEnd();
        Assert.True(
            normalizedStdout == expectedStdout,
            $"Stdout mismatch.{diag}\n\nExpected (normalized):\n{expectedStdout}\n\nActual (normalized):\n{normalizedStdout}");

        // Compare stderr against golden file; if no file specified, expect empty
        if (meta.ExpectedStderrFile is not null)
        {
            var goldenPath = Path.Combine(fixtureDir, meta.ExpectedStderrFile);
            var expected = (await File.ReadAllTextAsync(goldenPath))
                .Replace("\r\n", "\n").TrimEnd();
            Assert.True(
                normalizedStderr == expected,
                $"Stderr mismatch.{diag}\n\nExpected (normalized):\n{expected}\n\nActual (normalized):\n{normalizedStderr}");
        }
        else
        {
            Assert.True(
                normalizedStderr == string.Empty,
                $"Expected empty stderr but got:{diag}");
        }

        // Assert metadata contains/not_contains against stdout
        if (meta.ExpectedMetadata is { } m)
        {
            foreach (var s in m.Contains)
                Assert.True(stdout.Contains(s), $"Stdout missing expected string {s!.Replace("{", "{{").Replace("}", "}}")}.{diag}");
            foreach (var s in m.NotContains)
                Assert.True(!stdout.Contains(s), $"Stdout unexpectedly contains {s!.Replace("{", "{{").Replace("}", "}}")}.{diag}");
        }
    }

    [Fact]
    public async Task ConfigShow_OutputIsValidJson()
    {
        var (stdout, _, exitCode, timedOut, timeout) = await RunHypaAsync("config show");

        Assert.False(timedOut, $"Command timed out after {timeout.TotalSeconds:0.###} seconds.");
        Assert.Equal(0, exitCode);
        var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.TryGetProperty("enabled", out _));
        Assert.True(doc.RootElement.TryGetProperty("storage_path", out _));
    }

    [Fact]
    public async Task Doctor_OutputContainsExpectedCategories()
    {
        var (stdout, _, exitCode, timedOut, timeout) = await RunHypaAsync("doctor");

        Assert.False(timedOut, $"Command timed out after {timeout.TotalSeconds:0.###} seconds.");
        Assert.Equal(0, exitCode);
        Assert.Contains(".NET Runtime", stdout);
        Assert.Contains("OS", stdout);
        Assert.Contains("Config Paths", stdout);
        Assert.Contains("Project Root", stdout);
    }

    [Fact]
    public async Task HypaC_EchoHello_ContainsHelloAndNoMetadataForSmallOutput()
    {
        var (stdout, _, exitCode, timedOut, timeout) = await RunHypaAsync("-c \"echo hello\"");
        Assert.False(timedOut, $"Command timed out after {timeout.TotalSeconds:0.###} seconds.");
        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
        // "hello" is below SmallOutputThreshold=50 tokens, so no compression metadata appended
        Assert.DoesNotContain("[hypa:", stdout);
    }

    [Fact]
    public async Task HypaRaw_EchoHello_ContainsHelloNoMetadata()
    {
        var (stdout, _, exitCode, timedOut, timeout) = await RunHypaAsync(GetRawEchoArgs());
        Assert.False(timedOut, $"Command timed out after {timeout.TotalSeconds:0.###} seconds.");
        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
        Assert.DoesNotContain("[hypa:", stdout);
    }

    [Fact]
    public async Task HypaT_EchoHello_ExitsZero()
    {
        var (_, _, exitCode, timedOut, timeout) = await RunHypaAsync(GetPassthroughEchoArgs());
        Assert.False(timedOut, $"Command timed out after {timeout.TotalSeconds:0.###} seconds.");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task FiltersSavings_MarkdownOutputIsTable()
    {
        var (stdout, stderr, exitCode, timedOut, timeout) = await RunHypaAsync("filters savings --id dotnet-msbuild-noise --markdown");

        Assert.False(timedOut, $"Command timed out after {timeout.TotalSeconds:0.###} seconds.");
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, Normalize(stderr));
        Assert.Contains("| Filter | Applies | Original Tokens | Compressed Tokens | Saved Tokens | Saved |", stdout);
        Assert.Contains("| dotnet-msbuild-noise | dotnet |", stdout);
        Assert.Contains("| **TOTAL** |", stdout);
    }

    private static string Normalize(string raw)
    {
        var text = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var escapedSolutionRoot = EscapeBackslashes(SolutionRoot);
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var escapedHome = EscapeBackslashes(homePath);
        // Replace project root before home (project root path contains home path)
        text = text.Replace(escapedSolutionRoot, "<PROJECT_ROOT>");
        text = text.Replace(SolutionRoot, "<PROJECT_ROOT>");
        text = GoldenHomePattern().Replace(text, "<HOME>");
        text = text.Replace(escapedHome, "<HOME>");
        text = text.Replace(homePath, "<HOME>");
        // Keep this after placeholder replacement so slash normalization does not
        // transform escaped JSON paths (\\) before they can be replaced.
        text = text.Replace('\\', '/');
        text = text.Replace("<HOME>//", "<HOME>/");
        text = text.Replace("<PROJECT_ROOT>//", "<PROJECT_ROOT>/");
        // .NET runtime version e.g. "10.0.7"
        text = DotnetVersionPattern().Replace(text, "<DOTNET_VERSION>");
        // OS description on the doctor OS line
        text = OsVersionPattern().Replace(text, "<OS_VERSION>");
        // Update check result varies by network state and current version; normalize the entire line
        // plus any optional hint detail line that follows it.
        text = UpdateStatusPattern().Replace(text, "[  ok] Update               <UPDATE_STATUS>");
        // Strip trailing whitespace from each line — shells (especially CMD on Windows)
        // sometimes append trailing spaces that are semantically meaningless.
        text = string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()));
        return text.TrimEnd();
    }

    [GeneratedRegex(@"(?<=\.NET Runtime\s{1,30})\d+\.\d+\.\d+[-\w]*")]
    private static partial Regex DotnetVersionPattern();

    [GeneratedRegex(@"(?<=\[  ok\] OS\s{1,30})\S[^\n]*")]
    private static partial Regex OsVersionPattern();

    [GeneratedRegex(@"\[(?:  ok|warn)\] Update\s+[^\n]+(?:\n       [^\n]+)?")]
    private static partial Regex UpdateStatusPattern();

    [GeneratedRegex(@"(?:[A-Za-z]:)?[/\\][^\s""']*?hypa-golden-home-[0-9a-f]{32}")]
    private static partial Regex GoldenHomePattern();

    private static async Task<(string Stdout, string Stderr, int ExitCode, bool TimedOut, TimeSpan Timeout)> RunHypaAsync(string args)
    {
        var hypaPath = Environment.GetEnvironmentVariable("HYPA_BINARY_PATH");
        var timeout = GetCommandTimeout();
        var homePath = Path.Combine(Path.GetTempPath(), "hypa-golden-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homePath);

        ProcessStartInfo psi;
        if (hypaPath is not null && File.Exists(hypaPath))
        {
            psi = new ProcessStartInfo(hypaPath)
            {
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = SolutionRoot,
            };
        }
        else
        {
            var cliProject = Path.Combine(SolutionRoot, "src", "Hypa.Cli", "Hypa.Cli.csproj");
            // --no-build: Hypa.Cli is guaranteed built before tests run via the build-order
            // ProjectReference in Hypa.GoldenTests.csproj. Skipping the per-invocation build
            // eliminates MSBuild lock contention when tests run concurrently.
            // -c: dotnet run --no-build defaults to Debug; the CLI is built in the same
            // configuration as this test assembly, so target that to find the binary
            // (CI builds Release-only, where a Debug binary would not exist).
            psi = new ProcessStartInfo("dotnet")
            {
                Arguments = $"run --project \"{cliProject}\" --no-build -c {BuildConfiguration} --no-launch-profile -- {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = SolutionRoot,
            };
        }

        SetHomeEnvironment(psi, homePath);

        try
        {
            using var process = Process.Start(psi)!;
            // Read both streams concurrently to prevent deadlock when either buffer fills.
            // Some commands can leave redirected handles inherited by descendants after
            // the main process exits, so capture incrementally and keep real output.
            using var streamCts = new CancellationTokenSource();
            var stdoutCapture = StreamCapture.Start(process.StandardOutput, streamCts.Token);
            var stderrCapture = StreamCapture.Start(process.StandardError, streamCts.Token);
            var timedOut = false;
            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process exited between the timeout and kill attempt.
                }

                await WaitForExitAfterKillAsync(process);
            }

            var (stdout, stderr) = await FinishStreamCaptureAsync(stdoutCapture, stderrCapture, streamCts);
            var exitCode = process.HasExited ? process.ExitCode : -1;
            return (stdout, stderr, exitCode, timedOut, timeout);
        }
        finally
        {
            try
            {
                Directory.Delete(homePath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the temp home is unique per command.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; the temp home is unique per command.
            }
        }
    }

    private static void SetHomeEnvironment(ProcessStartInfo psi, string homePath)
    {
        var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        psi.Environment["HOME"] = homePath;
        psi.Environment["USERPROFILE"] = homePath;
        psi.Environment["DOTNET_CLI_HOME"] = realHome;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        if (!psi.Environment.ContainsKey("NUGET_PACKAGES"))
            psi.Environment["NUGET_PACKAGES"] = Path.Combine(realHome, ".nuget", "packages");
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Preserve the timeout failure instead of letting cleanup hang the test host.
        }
    }

    private static async Task<(string Stdout, string Stderr)> FinishStreamCaptureAsync(
        StreamCapture stdoutCapture,
        StreamCapture stderrCapture,
        CancellationTokenSource streamCts)
    {
        var captures = Task.WhenAll(stdoutCapture.Completion, stderrCapture.Completion);
        try
        {
            await captures.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            await streamCts.CancelAsync();
            try
            {
                await captures.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
                // Return the output already captured instead of blocking forever
                // on inherited pipe handles.
            }
        }

        return (stdoutCapture.Text, stderrCapture.Text);
    }

    private sealed class StreamCapture
    {
        private readonly StringBuilder output = new();

        private StreamCapture(StreamReader reader, CancellationToken ct)
        {
            Completion = RunAsync(reader, ct);
        }

        public Task Completion { get; }

        public string Text
        {
            get
            {
                lock (output)
                {
                    return output.ToString();
                }
            }
        }

        public static StreamCapture Start(StreamReader reader, CancellationToken ct) => new(reader, ct);

        private async Task RunAsync(StreamReader reader, CancellationToken ct)
        {
            var buffer = new char[4096];

            while (true)
            {
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                if (read == 0)
                    return;

                lock (output)
                {
                    output.Append(buffer, 0, read);
                }
            }
        }
    }

    private static TimeSpan GetCommandTimeout()
    {
        const int defaultSeconds = 60;
        var raw = Environment.GetEnvironmentVariable("HYPA_GOLDEN_COMMAND_TIMEOUT_SECONDS");
        if (int.TryParse(raw, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(defaultSeconds);
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0)
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("Could not locate solution root (no *.slnx found in parent directories).");
    }

    private static string EscapeBackslashes(string path) => path.Replace("\\", "\\\\");

    private static string AdjustArgsForPlatform(string fixtureName, string args)
    {
        if (!OperatingSystem.IsWindows())
            return args;

        return fixtureName switch
        {
            "run_t_echo" => "-t cmd /c echo hello",
            "run_raw_echo" => "raw cmd /c echo hello",
            "run_c_exit_code" => "-c \"pwsh -NoProfile -Command 'exit 42'\"",
            "run_c_pipe" => "-c \"(echo b&echo a) | sort\"",
            _ => args,
        };
    }

    private static string GetRawEchoArgs() =>
        OperatingSystem.IsWindows() ? "raw cmd /c echo hello" : "raw echo hello";

    private static string GetPassthroughEchoArgs() =>
        OperatingSystem.IsWindows() ? "-t cmd /c echo hello" : "-t echo hello";
}

internal sealed record GoldenMeta
{
    [JsonPropertyName("expected_exit_code")]
    public int ExpectedExitCode { get; init; }

    [JsonPropertyName("expected_stdout_file")]
    public string? ExpectedStdoutFile { get; init; }

    [JsonPropertyName("expected_stderr_file")]
    public string? ExpectedStderrFile { get; init; }

    [JsonPropertyName("expected_metadata")]
    public GoldenMetadataExpectation? ExpectedMetadata { get; init; }
}

internal sealed record GoldenMetadataExpectation
{
    [JsonPropertyName("contains")]
    public string[] Contains { get; init; } = [];

    [JsonPropertyName("not_contains")]
    public string[] NotContains { get; init; } = [];
}

[JsonSerializable(typeof(GoldenMeta))]
[JsonSerializable(typeof(GoldenMetadataExpectation))]
internal sealed partial class GoldenMetaJsonContext : JsonSerializerContext { }
