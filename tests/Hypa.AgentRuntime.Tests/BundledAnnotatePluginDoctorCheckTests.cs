using System.Diagnostics;
using System.Text.Json;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.Cli.Doctor;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class BundledAnnotatePluginDoctorCheckTests : IDisposable
{
    private readonly string _root;
    private readonly SystemPluginFiles _files;
    private readonly SystemPluginPathRoots _paths;
    private readonly FilePluginRegistry _registry;

    public BundledAnnotatePluginDoctorCheckTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-plugin-doctor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new SystemPluginFiles();
        _paths = new SystemPluginPathRoots(_root);
        _registry = new FilePluginRegistry(_files, _paths);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Linked_and_enabled_reports_ok()
    {
        var binary = WriteExecutable(Path.Combine(_root, "hypa"));
        var pluginRoot = Path.Combine(_root, "plugins", "bundled", "annotate");
        Directory.CreateDirectory(pluginRoot);
        WritePlugins(CreatePlugin("annotate", pluginRoot, binary, enabled: true));

        var result = ProgramResult("annotate");

        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Equal("linked and enabled", result.Value);
        Assert.DoesNotContain("hypa plugin bundled install annotate", CombinedText(result));
    }

    [Fact]
    public void Missing_plugin_root_reports_warn()
    {
        var binary = WriteExecutable(Path.Combine(_root, "hypa"));
        var missingRoot = Path.Combine(_root, "plugins", "bundled", "annotate-missing");
        WritePlugins(CreatePlugin("annotate", missingRoot, binary, enabled: true));

        var result = ProgramResult("annotate");

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Equal("plugin root missing", result.Value);
        Assert.DoesNotContain("hypa plugin bundled install annotate", CombinedText(result));
    }

    [Fact]
    public void Missing_program_path_reports_warn()
    {
        var pluginRoot = Path.Combine(_root, "plugins", "bundled", "annotate");
        Directory.CreateDirectory(pluginRoot);
        var missingBinary = Path.Combine(_root, "missing-hypa");
        WritePlugins(CreatePlugin("annotate", pluginRoot, missingBinary, enabled: true));

        var result = ProgramResult("annotate");

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Equal("program path missing", result.Value);
        Assert.DoesNotContain("hypa plugin bundled install annotate", CombinedText(result));
    }

    [Fact]
    public void Placeholder_program_resolves_through_host_binary()
    {
        var binary = WriteExecutable(Path.Combine(_root, "hypa"));
        var pluginRoot = Path.Combine(_root, "plugins", "alpha");
        Directory.CreateDirectory(pluginRoot);
        WritePlugins(CreatePlugin("alpha", pluginRoot, BundledPluginLayout.BinaryPlaceholder, enabled: true));

        var result = ProgramResult("alpha", hostBinary: binary);

        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Equal("linked and enabled", result.Value);
    }

    [Fact]
    public void Corrupt_registry_reports_warn_and_doctor_service_does_not_fail()
    {
        Directory.CreateDirectory(_paths.ConfigRoot);
        File.WriteAllText(_paths.RegistryPath, "{not-json");

        var source = CreateSource();
        var results = source.GetChecks().Select(c => c.Run()).ToList();
        var result = Assert.Single(results);
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Equal("registry unreadable", result.Value);

        var service = new DoctorService([], [source]);
        var fromService = service.Run();
        var annotate = Assert.Single(fromService);
        Assert.Equal(DoctorStatus.Warn, annotate.Status);
        Assert.NotEqual(DoctorStatus.Fail, annotate.Status);
    }

    [Fact]
    public void Unreadable_registry_reports_warn_and_doctor_service_does_not_throw()
    {
        Directory.CreateDirectory(_paths.ConfigRoot);
        File.WriteAllText(_paths.RegistryPath, "[]");
        var throwingFiles = new ThrowingReadRegistryFiles(_files, _paths.RegistryPath);
        var registry = new FilePluginRegistry(throwingFiles, _paths);
        var source = new PluginDoctorCheckSource(
            registry,
            throwingFiles,
            _paths,
            new UnusedDoctorRunner());

        var result = Assert.Single(source.GetChecks()).Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Equal("registry unreadable", result.Value);

        var service = new DoctorService([], [source]);
        var results = service.Run();
        Assert.All(results, r => Assert.NotEqual(DoctorStatus.Fail, r.Status));
    }

    [Fact]
    public void Doctor_service_concatenates_source_checks_on_each_run()
    {
        var source = CreateSource();
        var service = new DoctorService([new StaticEnvCheck()], [source]);

        var first = service.Run();
        Assert.Contains(first, r => r.Label == "runtime");
        Assert.DoesNotContain(first, r => r.Label.EndsWith(": program", StringComparison.Ordinal));

        var binary = WriteExecutable(Path.Combine(_root, "hypa"));
        var pluginRoot = Path.Combine(_root, "plugins", "annotate");
        Directory.CreateDirectory(pluginRoot);
        WritePlugins(CreatePlugin("annotate", pluginRoot, binary, enabled: true));

        var second = service.Run();
        Assert.Contains(second, r => r.Label == "runtime");
        Assert.Contains(second, r => r.Label == "annotate: program" && r.Status == DoctorStatus.Ok);
    }

    [Fact]
    public void Generic_program_check_covers_every_linked_plugin()
    {
        var binaryA = WriteExecutable(Path.Combine(_root, "bin-a"));
        var binaryB = WriteExecutable(Path.Combine(_root, "bin-b"));
        var rootA = Path.Combine(_root, "plugins", "alpha");
        var rootB = Path.Combine(_root, "plugins", "beta");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        WritePlugins(
            CreatePlugin("alpha", rootA, binaryA, enabled: true),
            CreatePlugin("beta", rootB, binaryB, enabled: true));

        var checks = CreateSource().GetChecks().ToList();
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal("Plugins", c.Category));
        var results = checks.Select(c => c.Run()).ToList();
        Assert.Contains(results, r => r.Label == "alpha: program" && r.Status == DoctorStatus.Ok);
        Assert.Contains(results, r => r.Label == "beta: program" && r.Status == DoctorStatus.Ok);
    }

    [Fact]
    public void Child_json_ok_uses_plugin_id_and_label()
    {
        var runner = new RecordingDoctorRunner
        {
            Next = new PluginDoctorRunResult(
                0,
                "log line\n{\"status\":\"ok\",\"value\":\"3 annotations\",\"detail\":\"store\",\"hint\":\"ok\"}\n",
                "",
                TimedOut: false),
        };
        var source = CreateAnnotateWithDoctor(runner);
        var checks = source.GetChecks().ToList();
        Assert.All(checks, c => Assert.Equal("Plugins", c.Category));
        var results = checks.Select(c => c.Run()).ToList();
        var result = Assert.Single(results, r => r.Label == "annotate: annotations");

        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Equal("3 annotations", result.Value);
        Assert.Equal("store", result.Detail);
        Assert.Equal("ok", result.Hint);
        Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(["doctor"]));
    }

    [Fact]
    public void Child_bad_json_reports_fail()
    {
        var runner = new RecordingDoctorRunner
        {
            Next = new PluginDoctorRunResult(0, "not-json\n", "", TimedOut: false),
        };

        var result = RunChild(runner);

        Assert.Equal(DoctorStatus.Fail, result.Status);
        Assert.Equal("malformed doctor json", result.Value);
        Assert.NotEqual(DoctorStatus.Ok, result.Status);
    }

    [Fact]
    public void Child_nonzero_exit_reports_fail_even_when_stdout_says_ok()
    {
        var runner = new RecordingDoctorRunner
        {
            Next = new PluginDoctorRunResult(1, "{\"status\":\"ok\",\"value\":\"nope\"}", "crash", TimedOut: false),
        };

        var result = RunChild(runner);

        Assert.Equal(DoctorStatus.Fail, result.Status);
        Assert.Contains("child exited 1", result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Child_timeout_reports_fail()
    {
        var runner = new RecordingDoctorRunner
        {
            Next = new PluginDoctorRunResult(null, "", "", TimedOut: true),
        };

        var result = RunChild(runner);

        Assert.Equal(DoctorStatus.Fail, result.Status);
        Assert.Equal("timed out", result.Value);
    }

    [Fact]
    public void Child_json_fail_with_exit_zero_maps_to_fail()
    {
        var runner = new RecordingDoctorRunner
        {
            Next = new PluginDoctorRunResult(
                0,
                "{\"status\":\"fail\",\"value\":\"store unreadable\",\"detail\":\"invalid data\"}",
                "",
                TimedOut: false),
        };

        var result = RunChild(runner);

        Assert.Equal(DoctorStatus.Fail, result.Status);
        Assert.Equal("store unreadable", result.Value);
        Assert.Equal("invalid data", result.Detail);
    }

    [Fact]
    public void Annotate_manifest_declares_store_doctor()
    {
        var parsed = new PluginManifestParser().Parse(OfficialPluginAssets.AnnotateManifest);
        Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.Message);
        var doctor = Assert.Single(parsed.Value.Doctor);
        Assert.Equal("store", doctor.Id);
        Assert.Equal("annotations", doctor.Label);
        Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, doctor.Command[0]);
        Assert.Equal(["doctor"], doctor.Command.Skip(1).ToArray());
    }

    [Fact]
    public void Doctor_child_timeout_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), PluginDoctorCheckSource.ChildTimeout);
    }

    [SkippableFact]
    public void Doctor_runner_passes_argv_without_a_shell()
    {
        Skip.If(!File.Exists("/bin/echo"), "/bin/echo is required.");
        var runner = new ProcessPluginDoctorRunner();
        var ran = runner.Run(
            "/bin/echo",
            ["hello;echo pwned"],
            _root,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(2));

        Assert.False(ran.TimedOut);
        Assert.Equal(0, ran.ExitCode);
        Assert.Contains("hello;echo pwned", ran.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\npwned", ran.Stdout, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Doctor_runner_kills_on_timeout()
    {
        Skip.If(!File.Exists("/bin/sleep"), "/bin/sleep is required.");
        var runner = new ProcessPluginDoctorRunner();
        var clock = Stopwatch.StartNew();
        var ran = runner.Run(
            "/bin/sleep",
            ["30"],
            _root,
            new Dictionary<string, string>(),
            TimeSpan.FromMilliseconds(400));
        clock.Stop();

        Assert.True(ran.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    private DoctorCheckResult RunChild(RecordingDoctorRunner runner) =>
        CreateAnnotateWithDoctor(runner)
            .GetChecks()
            .Select(c => c.Run())
            .Single(r => r.Label == "annotate: annotations");

    private PluginDoctorCheckSource CreateAnnotateWithDoctor(IPluginDoctorRunner runner)
    {
        var binary = WriteExecutable(Path.Combine(_root, "hypa-annotate"));
        var pluginRoot = Path.Combine(_root, "plugins", "bundled", "annotate");
        Directory.CreateDirectory(pluginRoot);
        WritePlugins(CreatePlugin(
            "annotate",
            pluginRoot,
            binary,
            enabled: true,
            doctor:
            [
                new PluginManifestDoctor
                {
                    Id = "store",
                    Label = "annotations",
                    Command = [binary, "doctor"],
                },
            ]));
        return CreateSource(runner, binary);
    }

    private DoctorCheckResult ProgramResult(string pluginId, string? hostBinary = null)
    {
        var checks = CreateSource(hostBinary: hostBinary).GetChecks().ToList();
        Assert.All(checks, c => Assert.Equal("Plugins", c.Category));
        return checks.Select(c => c.Run()).Single(r => r.Label == pluginId + ": program");
    }

    private PluginDoctorCheckSource CreateSource(
        IPluginDoctorRunner? runner = null,
        string? hostBinary = null) =>
        new(_registry, _files, _paths, runner ?? new UnusedDoctorRunner(), hostBinary);

    private void WritePlugins(params InstalledPlugin[] plugins)
    {
        var json = JsonSerializer.Serialize(plugins.ToList(), PluginRegistryJsonContext.Default.ListInstalledPlugin);
        Directory.CreateDirectory(_paths.ConfigRoot);
        File.WriteAllText(_paths.RegistryPath, json);
    }

    private static InstalledPlugin CreatePlugin(
        string pluginId,
        string pluginRoot,
        string program,
        bool enabled,
        IReadOnlyList<PluginManifestDoctor>? doctor = null) =>
        new()
        {
            PluginId = pluginId,
            Name = pluginId,
            Version = "0.0.1",
            MinHypaVersion = "0.1.0",
            ManifestPath = Path.Combine(pluginRoot, PluginHostService.ManifestFileName),
            PluginRoot = pluginRoot,
            Enabled = enabled,
            Actions =
            [
                new PluginManifestAction
                {
                    Id = "copy-context",
                    Title = "Copy context",
                    Command = [program, "plugin", "bundled", "action", "copy-context"],
                },
            ],
            Doctor = doctor ?? [],
        };

    private static string WriteExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return Path.GetFullPath(path);
    }

    private static string CombinedText(DoctorCheckResult result) =>
        string.Join(' ', new[] { result.Value, result.Detail, result.Hint }.Where(s => s is not null));

    private sealed class StaticEnvCheck : IDoctorCheck
    {
        public string Category => "Env";

        public DoctorCheckResult Run() => new("runtime", "ok", DoctorStatus.Ok);
    }

    private sealed class UnusedDoctorRunner : IPluginDoctorRunner
    {
        public PluginDoctorRunResult Run(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout) =>
            throw new InvalidOperationException("doctor child must not run");
    }

    private sealed class RecordingDoctorRunner : IPluginDoctorRunner
    {
        public PluginDoctorRunResult Next { get; set; } = new(0, "", "", TimedOut: false);

        public List<DoctorCall> Calls { get; } = [];

        public PluginDoctorRunResult Run(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout)
        {
            Calls.Add(new DoctorCall(program, arguments.ToArray()));
            return Next;
        }

        public sealed record DoctorCall(string Program, IReadOnlyList<string> Arguments);
    }

    private sealed class ThrowingReadRegistryFiles(IPluginFiles inner, string registryPath) : IPluginFiles
    {
        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public string ReadAllText(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(registryPath), StringComparison.Ordinal))
                throw new IOException("registry read denied");

            return inner.ReadAllText(path);
        }

        public void WriteAllText(string path, string contents) => inner.WriteAllText(path, contents);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public bool DeleteFile(string path) => inner.DeleteFile(path);

        public bool DeleteDirectory(string path) => inner.DeleteDirectory(path);

        public string GetFullPath(string path) => inner.GetFullPath(path);
    }
}
