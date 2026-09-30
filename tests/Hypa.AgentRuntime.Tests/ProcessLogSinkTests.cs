using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Logging;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class ProcessLogSinkTests : IDisposable
{
    private readonly string _dir;

    public ProcessLogSinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Json_lines_parse_and_startup_has_required_fields()
    {
        var path = Path.Combine(_dir, "mux.log");
        using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
        ProcessLogLifecycle.Startup(sink, ProcessLogEvents.SubsystemMux, "sess-1");
        var lines = File.ReadAllLines(path);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(ProcessLogEvents.AppStartup, first.RootElement.GetProperty("event").GetString());
        Assert.Equal(ProcessLogEvents.SubsystemMux, first.RootElement.GetProperty("subsystem").GetString());
        Assert.Equal(ProcessLogEvents.OutcomeStarted, first.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(Environment.ProcessId, first.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("sess-1", first.RootElement.GetProperty("session_id").GetString());
        var ts = first.RootElement.GetProperty("ts").GetString();
        Assert.False(string.IsNullOrWhiteSpace(ts));
        Assert.EndsWith("Z", ts, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(ts, out _));
    }

    [Fact]
    public void Relative_explicit_mux_path_writes_under_working_directory()
    {
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_dir);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
#pragma warning disable CA1416
                File.SetUnixFileMode(_dir, UnixLogPathSecurity.OwnerDirectoryMode);
#pragma warning restore CA1416
            }

            Assert.True(string.IsNullOrEmpty(Path.GetDirectoryName("mux.log")));
            var resolved = ProcessLogPaths.ResolveMuxPath("/unused/hypa.sock", "mux.log");
            Assert.Equal(Path.GetFullPath("mux.log"), resolved);
            Assert.True(Path.IsPathRooted(resolved));
            Assert.False(string.IsNullOrEmpty(Path.GetDirectoryName(resolved)));

            var sink = ProcessLogSinkFactory.OpenMux("/unused/hypa.sock", "mux.log");
            var file = Assert.IsType<JsonLinesFileLogSink>(sink);
            using (file)
            {
                Assert.False(file.Disabled);
                ProcessLogLifecycle.Startup(file, ProcessLogEvents.SubsystemMux, "sess");
                Assert.True(File.Exists(resolved));
                var lines = File.ReadAllLines(resolved);
                Assert.NotEmpty(lines);
                using var doc = JsonDocument.Parse(lines[0]);
                Assert.Equal(ProcessLogEvents.AppStartup, doc.RootElement.GetProperty("event").GetString());
            }
        }
        finally
        {
            try { Directory.SetCurrentDirectory(previous); }
            catch { /* restore */ }
        }
    }

    [Fact]
    public void Off_filter_writes_no_file_records()
    {
        var path = Path.Combine(_dir, "off.log");
        var sink = ProcessLogSinkFactory.Open(path, ProcessLogLevel.Off);
        ProcessLogLifecycle.Startup(sink, ProcessLogEvents.SubsystemMux, "sess");
        sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.ApiRequestStart,
            Subsystem = ProcessLogEvents.SubsystemApi,
            Outcome = ProcessLogEvents.OutcomeStarted,
            Ts = DateTimeOffset.UtcNow,
            Pid = 1,
            Level = ProcessLogLevel.Information,
        });
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Debug_filter_writes_more_lines_than_info()
    {
        var info = new CapturingProcessLogSink(ProcessLogLevel.Information);
        var debug = new CapturingProcessLogSink(ProcessLogLevel.Debug);
        ProcessLogApi.WriteStart(info, "1", ProtocolMethods.PaneRead, "c1", "s");
        ProcessLogApi.WriteStart(debug, "1", ProtocolMethods.PaneRead, "c1", "s");
        ProcessLogApi.WriteStart(info, "2", ProtocolMethods.TabFocus, "c1", "s");
        ProcessLogApi.WriteStart(debug, "2", ProtocolMethods.TabFocus, "c1", "s");
        Assert.DoesNotContain(info.Records, r => r.Method == ProtocolMethods.PaneRead);
        Assert.Contains(debug.Records, r => r.Method == ProtocolMethods.PaneRead);
        Assert.True(debug.Records.Count > info.Records.Count);
    }

    [Fact]
    public void Size_limit_rotates_and_retains_three()
    {
        var path = Path.Combine(_dir, "rot.log");
        using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Debug, maxBytes: 80, retainFiles: 3);
        for (var i = 0; i < 40; i++)
        {
            sink.Write(new ProcessLogRecord
            {
                Event = ProcessLogEvents.ApiRequestStart,
                Subsystem = ProcessLogEvents.SubsystemApi,
                Outcome = ProcessLogEvents.OutcomeStarted,
                Ts = DateTimeOffset.UtcNow,
                Pid = 1,
                Level = ProcessLogLevel.Debug,
                RequestId = "r" + i.ToString(),
                Method = ProtocolMethods.TabFocus,
            });
        }

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.True(File.Exists(path + ".2"));
        Assert.True(File.Exists(path + ".3"));
        Assert.False(File.Exists(path + ".4"));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public void Rotation_refuses_unrelated_retained_file()
    {
        var path = Path.Combine(_dir, "rot.log");
        const string seed = "user-notes-must-remain\n";
        File.WriteAllText(path + ".1", seed);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path + ".1", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Debug, maxBytes: 80, retainFiles: 3);
        for (var i = 0; i < 40; i++)
        {
            sink.Write(new ProcessLogRecord
            {
                Event = ProcessLogEvents.ApiRequestStart,
                Subsystem = ProcessLogEvents.SubsystemApi,
                Outcome = ProcessLogEvents.OutcomeStarted,
                Ts = DateTimeOffset.UtcNow,
                Pid = 1,
                Level = ProcessLogLevel.Debug,
                RequestId = "r" + i.ToString(),
                Method = ProtocolMethods.TabFocus,
            });
        }

        Assert.Equal(seed, File.ReadAllText(path + ".1"));
        Assert.True(sink.Disabled);
        Assert.False(File.Exists(path + ".2"));
    }

    [SkippableFact]
    public void Read_only_directory_disables_sink()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix mode");
        var locked = Path.Combine(_dir, "locked");
        Directory.CreateDirectory(locked);

        // bypass if not supported
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "Not supported on this platform");
        }
        else
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var path = Path.Combine(locked, "mux.log");
        using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
        sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.AppStartup,
            Subsystem = ProcessLogEvents.SubsystemMux,
            Outcome = ProcessLogEvents.OutcomeStarted,
            Ts = DateTimeOffset.UtcNow,
            Pid = 1,
            Level = ProcessLogLevel.Information,
        });
        Assert.True(sink.Disabled);

        // bypass if not supported
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "Not supported on this platform");
        }
        else
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Subscription_id_omitted_without_session_id()
    {
        var record = new ProcessLogRecord
        {
            Event = ProcessLogEvents.AttachSubscribe,
            Subsystem = ProcessLogEvents.SubsystemAttach,
            Outcome = ProcessLogEvents.OutcomeOk,
            Ts = DateTimeOffset.UtcNow,
            Pid = 1,
            SubscriptionId = "sub_1",
        };
        var line = Encoding.UTF8.GetString(ProcessLogJsonWriter.WriteLine(record));
        using var doc = JsonDocument.Parse(line);
        Assert.False(doc.RootElement.TryGetProperty("subscription_id", out _));
    }

    [Fact]
    public void Routine_pane_read_is_debug()
    {
        Assert.True(ProcessLogApi.IsRoutine(ProtocolMethods.PaneRead));
        Assert.Equal(ProcessLogLevel.Debug, ProcessLogApi.StartLevel(ProtocolMethods.PaneRead));
        Assert.Equal(ProcessLogLevel.Information, ProcessLogApi.StartLevel(ProtocolMethods.TabFocus));
        Assert.True(ProcessLogApi.ChangesUi(ProtocolMethods.TabFocus));
        Assert.False(ProcessLogApi.ChangesUi(ProtocolMethods.PaneRead));
    }

    [Fact]
    public void Information_filter_does_not_enable_debug_output_chunk_records()
    {
        var sink = new CapturingProcessLogSink(ProcessLogLevel.Information);
        Assert.False(sink.IsEnabled(ProcessLogLevel.Debug));
        ProcessLogApi.WriteStart(sink, "1", ProtocolMethods.PaneRead, "c1", "s");
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Information_sink_does_not_log_per_pty_output_chunk()
    {
        var logs = new CapturingProcessLogSink(ProcessLogLevel.Information);
        Assert.False(logs.IsEnabled(ProcessLogLevel.Debug));
        var factory = TestPaneFactories.Scripted();
        await using var hub = new EventSubscriptionHub(logs, "pty");
        var state = new AppState(SessionId.New("pty"));
        state.UpdateSession(s => s with { Name = "pty", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace("/tmp/ws");
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            subscriptions: hub);
        var created = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse("""{"command":"echo","cwd":"/tmp"}""").RootElement,
            CancellationToken.None);
        var paneId = created.GetProperty("pane_id").GetString()!;
        var runtime = factory.Created.Single(r => r.Id.Value == paneId);
        runtime.FireOutput("chunk-one");
        runtime.FireOutput("chunk-two");
        await Task.Delay(40);
        Assert.False(logs.IsEnabled(ProcessLogLevel.Debug));
        Assert.DoesNotContain(logs.Records, r => r.Event == ProcessLogEvents.ApiRequestStart);
        Assert.DoesNotContain(logs.Records, r => r.Event == ProcessLogEvents.ApiRequestComplete);
        Assert.DoesNotContain(logs.Records, r => r.Event == ProcessLogEvents.ApiRequestFail);
        Assert.All(logs.Records, r => Assert.Equal(ProcessLogEvents.EventsQueue, r.Event));
        await cp.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public void Fail_err_is_redacted_plain_text_not_json_envelope()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var secret = "sk-abcdefghijklmnopqrstuvwxyz0123";
        var err = ProcessLogApi.RedactFailErr(redactor, "boom " + secret);
        Assert.False(string.IsNullOrWhiteSpace(err));
        Assert.False(err.TrimStart().StartsWith('{'));
        Assert.DoesNotContain(secret, err, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(ProcessLogApi.EncodeErrObject(err));
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("err").ValueKind);
    }

    [Fact]
    public void Fail_err_with_control_char_stays_on_json_redact_path()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var secret = "sk-abcdefghijklmnopqrstuvwxyz0123";
        var raw = "fail\0" + secret + "\u0008tail";
        var encoded = ProcessLogApi.EncodeErrObject(raw);
        using (var doc = JsonDocument.Parse(encoded))
        {
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Contains("\\u0000", encoded, StringComparison.Ordinal);
        }

        var err = ProcessLogApi.RedactFailErr(redactor, raw);
        Assert.False(err.TrimStart().StartsWith('{'));
        Assert.DoesNotContain(secret, err, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", err, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Preexisting_group_readable_file_does_not_receive_records()
    {
        // bypass if not supported
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "Not supported on this platform");
        }
        else
        {
            var path = Path.Combine(_dir, "exposed.log");
            File.WriteAllText(path, "seed\n");
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
            ProcessLogLifecycle.Startup(sink, ProcessLogEvents.SubsystemMux, "sess");
            Assert.True(sink.Disabled);
            Assert.Equal("seed\n", File.ReadAllText(path));
        }
    }

    [SkippableFact]
    public void Symlink_log_path_does_not_follow()
    {
        // bypass if not supported
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "Not supported on this platform");
        }
        else
        {
            var target = Path.Combine(_dir, "target.log");
            File.WriteAllText(target, "keep\n");
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var path = Path.Combine(_dir, "mux.log");
            File.CreateSymbolicLink(path, target);
            using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
            ProcessLogLifecycle.Startup(sink, ProcessLogEvents.SubsystemMux, "sess");
            Assert.True(sink.Disabled);
            Assert.Equal("keep\n", File.ReadAllText(target));
        }
    }

    [SkippableFact]
    public void Fifo_at_active_path_does_not_block_write()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix fifo");
        var path = Path.Combine(_dir, "mux.log");
        Assert.Equal(0, MkFifo(path, 0x180));

        var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
        var write = Task.Run(() => ProcessLogLifecycle.Startup(
            sink,
            ProcessLogEvents.SubsystemMux,
            "sess"));
        var finished = write.Wait(TimeSpan.FromSeconds(2));
        Assert.True(finished, "write blocked on fifo");
        try
        {
            write.GetAwaiter().GetResult();
            Assert.True(sink.Disabled);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (finished)
                sink.Dispose();
        }
    }

    [SkippableFact]
    public void Fifo_at_retained_path_does_not_block_rotation()
    {
        // bypass if not supported
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "Not supported on this platform");
        }
        else
        {
            var path = Path.Combine(_dir, "rot.log");
            const string seed = "{\"event\":\"seed\"}\n";
            File.WriteAllText(path, seed);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(0, MkFifo(path + ".1", 0x180));

            var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Debug, maxBytes: 80, retainFiles: 3);
            var write = Task.Run(() =>
            {
                for (var i = 0; i < 40; i++)
                {
                    sink.Write(new ProcessLogRecord
                    {
                        Event = ProcessLogEvents.ApiRequestStart,
                        Subsystem = ProcessLogEvents.SubsystemApi,
                        Outcome = ProcessLogEvents.OutcomeStarted,
                        Ts = DateTimeOffset.UtcNow,
                        Pid = 1,
                        Level = ProcessLogLevel.Debug,
                        RequestId = "r" + i.ToString(),
                        Method = ProtocolMethods.TabFocus,
                    });
                }
            });
            var finished = write.Wait(TimeSpan.FromSeconds(2));
            Assert.True(finished, "rotation blocked on fifo");
            try
            {
                write.GetAwaiter().GetResult();
                Assert.True(File.Exists(path + ".1"));
                Assert.True(sink.Disabled);
                Assert.False(File.Exists(path + ".2"));
            }
            finally
            {
                if (finished)
                    sink.Dispose();
            }
        }
    }

    [Fact]
    public void Reopen_appends_after_preseeded_bytes()
    {
        var path = Path.Combine(_dir, "append.log");
        const string seed = "seed-bytes-must-remain\n";
        File.WriteAllText(path, seed);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
        ProcessLogLifecycle.Startup(sink, ProcessLogEvents.SubsystemMux, "sess");
        Assert.False(sink.Disabled);
        var text = File.ReadAllText(path);
        Assert.StartsWith(seed, text, StringComparison.Ordinal);
        Assert.True(text.Length > seed.Length);
        var rest = text[seed.Length..].TrimEnd();
        using var doc = JsonDocument.Parse(rest);
        Assert.Equal(ProcessLogEvents.AppStartup, doc.RootElement.GetProperty("event").GetString());
    }

    [SkippableFact]
    public void World_writable_parent_disables_sink()
    {
        // bypass if not supported
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "Not supported on this platform");
        }
        else
        {
            var parent = Path.Combine(_dir, "shared");
            Directory.CreateDirectory(parent);
            File.SetUnixFileMode(
                parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
            var path = Path.Combine(parent, "mux.log");
            using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
            ProcessLogLifecycle.Startup(sink, ProcessLogEvents.SubsystemMux, "sess");
            Assert.True(sink.Disabled);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void Remote_attach_log_is_not_a_shared_temp_dir()
    {
        var sink = ProcessLogSinkFactory.OpenAttach("/unused.sock", 4242, remote: true);
        if (sink is NullProcessLogSink)
            return;
        Assert.False(string.IsNullOrWhiteSpace(sink.Path));
        Assert.DoesNotContain(
            Path.Combine(Path.GetTempPath(), "hypa-logs"),
            sink.Path,
            StringComparison.Ordinal);
        Assert.Contains(Path.Combine(".hypa", "logs"), sink.Path, StringComparison.Ordinal);
        (sink as IDisposable)?.Dispose();
    }

    [SkippableFact]
    public async Task Mux_host_serves_when_sink_is_off_or_disabled()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip.If(true, "Not supported on this platform");
        }
        else
        {
            var offDir = Path.Combine(Path.GetTempPath(), "hl" + Guid.NewGuid().ToString("N")[..8]);
            var offLog = Path.Combine(offDir, "off.log");
            await using var offHost = await MuxLogHost.Start(
                offDir,
                ProcessLogSinkFactory.Open(offLog, ProcessLogLevel.Off));
            var pingOff = await offHost.Client.CallAsync(ProtocolMethods.Ping, []);
            Assert.True(pingOff.GetProperty("ok").GetBoolean());
            Assert.False(File.Exists(offLog));

            var infoDir = Path.Combine(Path.GetTempPath(), "hl" + Guid.NewGuid().ToString("N")[..8]);
            var infoLog = Path.Combine(infoDir, "mux.log");
            await using (var infoHost = await MuxLogHost.Start(
                infoDir,
                new JsonLinesFileLogSink(infoLog, ProcessLogLevel.Information)))
            {
                ProcessLogLifecycle.Startup(infoHost.Logs, ProcessLogEvents.SubsystemMux, "info");
                var pingInfo = await infoHost.Client.CallAsync(ProtocolMethods.Ping, []);
                Assert.True(pingInfo.GetProperty("ok").GetBoolean());
                Assert.True(File.Exists(infoLog));
                foreach (var line in File.ReadAllLines(infoLog))
                {
                    using var doc = JsonDocument.Parse(line);
                    Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
                }
            }

            var locked = Path.Combine(Path.GetTempPath(), "hl" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(locked);
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var disabledPath = Path.Combine(locked, "mux.log");
            var roState = Path.Combine(Path.GetTempPath(), "hl" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                await using var disabledHost = await MuxLogHost.Start(
                    roState,
                    new JsonLinesFileLogSink(disabledPath, ProcessLogLevel.Information));
                ProcessLogLifecycle.Startup(
                    disabledHost.Logs,
                    ProcessLogEvents.SubsystemMux,
                    "disabled");
                Assert.True(disabledHost.Logs.Disabled);
                var ping = await disabledHost.Client.CallAsync(ProtocolMethods.Ping, new JsonObject());
                Assert.True(ping.GetProperty("ok").GetBoolean());
            }
            finally
            {
                File.SetUnixFileMode(
                    locked,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public void Attach_log_is_mode_0600_on_unix()
    {
        var path = ProcessLogPaths.ResolveAttachPath(Path.Combine(_dir, "hypa.sock"), 4242);
        using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
        ProcessLogLifecycle.Startup(sink, ProcessLogEvents.SubsystemAttach, "sess");
        Assert.True(File.Exists(path));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public void Sink_write_error_does_not_throw_to_caller()
    {
        var locked = Path.Combine(_dir, "no-write");
        Directory.CreateDirectory(locked);
        var path = Path.Combine(locked, "mux.log");
        using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            sink.Write(new ProcessLogRecord
            {
                Event = ProcessLogEvents.AppStartup,
                Subsystem = ProcessLogEvents.SubsystemMux,
                Outcome = ProcessLogEvents.OutcomeStarted,
                Ts = DateTimeOffset.UtcNow,
                Pid = 1,
                Level = ProcessLogLevel.Information,
            });
            Assert.True(sink.Disabled);
        }
        finally
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    locked,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public void Filter_from_environment_off_and_debug()
    {
        Assert.Equal(ProcessLogLevel.Off, ProcessLogFilter.Parse("off"));
        Assert.Equal(ProcessLogLevel.Debug, ProcessLogFilter.Parse("debug"));
        Assert.Equal(ProcessLogLevel.Information, ProcessLogFilter.Parse(null));
        Assert.Equal(ProcessLogLevel.Information, ProcessLogFilter.Parse("info"));
    }

    private sealed class MuxLogHost : IAsyncDisposable
    {
        public required ControlPlaneClient Client { get; init; }
        public required IProcessLogSink Logs { get; init; }
        public required UnixSocketServer Server { get; init; }
        public required ControlPlaneService Control { get; init; }
        public required EventSubscriptionHub Hub { get; init; }
        public required FileRuntimeEventJournal Journal { get; init; }
        public required string Dir { get; init; }

        public static async Task<MuxLogHost> Start(string dir, IProcessLogSink logs)
        {
            Directory.CreateDirectory(dir);
            var sock = Path.Combine(dir, "s.sock");
            var paths = new RuntimeStatePaths { StateDirectory = dir };
            var migrator = new SqliteRuntimeSchemaMigrator(paths);
            Assert.True((await migrator.MigrateAsync()).IsOk);
            var state = new AppState(SessionId.New("loghost"));
            state.UpdateSession(s => s with { Name = "loghost", LifecycleState = SessionLifecycle.Ready });
            var store = new SqliteRuntimeSessionStore(paths);
            Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
            var manifests = new SqliteJournalManifestStore(paths);
            var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
            Assert.True((await journal.RecoverAsync()).IsOk);
            var hub = new EventSubscriptionHub();
            var intel = new PaneIntelligencePipeline();
            var cp = new ControlPlaneService(
                state,
                TestPaneFactories.Create(intel),
                intel,
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: hub);
            var server = new UnixSocketServer(
                cp,
                sock,
                UnixSocketServerOptions.Default,
                processLog: logs,
                sessionId: state.SessionId.Value);
            await server.StartAsync(CancellationToken.None);
            var client = new ControlPlaneClient(sock);
            await client.ConnectAsync();
            return new MuxLogHost
            {
                Client = client,
                Logs = logs,
                Server = server,
                Control = cp,
                Hub = hub,
                Journal = journal,
                Dir = dir,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            await Control.ShutdownAsync(CancellationToken.None);
            await Hub.DisposeAsync();
            await Journal.DisposeAsync();
            (Logs as IDisposable)?.Dispose();
            try
            {
                SqliteTestCleanup.ReleaseAndDelete(Dir, Path.Combine(Dir, "runtime.db"));
            }
            catch
            {
            }
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string pathname, int mode);
}
