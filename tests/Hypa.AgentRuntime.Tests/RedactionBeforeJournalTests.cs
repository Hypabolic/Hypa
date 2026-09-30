using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class RedactionBeforeJournalTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;
    private CapturingSink? _outputSink;

    public RedactionBeforeJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h07-redact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        try
        {
            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void Default_redactor_scrubs_env_and_token_patterns()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var input = """{"msg":"API_TOKEN=supersecret123","auth":"Bearer abcdefghijklmnop"}""";
        var outText = redactor.RedactJsonPayload("session.lifecycle", input);
        Assert.DoesNotContain("supersecret123", outText);
        Assert.DoesNotContain("abcdefghijklmnop", outText);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, outText);

        var sk = redactor.RedactJsonPayload("x", "key sk-abcdefghijklmnopqrstuvwxyz0123 here");
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz0123", sk);

        var pem = redactor.RedactJsonPayload("x",
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF6PZGFw=\n-----END RSA PRIVATE KEY-----");
        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Z3VS5JJcds3xfn", pem);
    }

    [Fact]
    public void Closed_terminal_bytes_redact_incomplete_secret_prefix()
    {
        var redactor = new DefaultEventPayloadRedactor();
        const string prefix = "sk-abcdefghijklmn";
        var keyed = redactor.RedactTerminalBytes("pane_hold_paint", Encoding.UTF8.GetBytes(prefix));
        Assert.True(keyed.IsEmpty);
        var closed = Encoding.UTF8.GetString(redactor.RedactClosedTerminalBytes(Encoding.UTF8.GetBytes(prefix)).Span);
        Assert.Equal(DefaultEventPayloadRedactor.Replacement, closed);
        Assert.DoesNotContain(prefix, closed, StringComparison.Ordinal);
        var leftover = redactor.FlushTerminalStream("pane_hold_paint");
        var leftoverText = Encoding.UTF8.GetString(leftover.Span);
        Assert.DoesNotContain(prefix, leftoverText, StringComparison.Ordinal);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, leftoverText, StringComparison.Ordinal);
    }

    [Fact]
    public void Peek_closed_bytes_cover_pem_body_hold_without_flush()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var begin = Encoding.UTF8.GetBytes("-----BEGIN RSA PRIVATE KEY-----\n");
        Assert.True(redactor.RedactTerminalBytes("pane_pem_peek", begin).IsEmpty);
        var body = Encoding.UTF8.GetBytes("MIIEowIBAAKCAQEA0Z3VS5JJcds3xfnSECRET\n");
        Assert.True(redactor.RedactTerminalBytes("pane_pem_peek", body).IsEmpty);

        var closedOnly = Encoding.UTF8.GetString(redactor.RedactClosedTerminalBytes(body).Span);
        Assert.Contains("SECRET", closedOnly, StringComparison.Ordinal);

        var peek = Encoding.UTF8.GetString(
            redactor.PeekClosedTerminalBytes("pane_pem_peek", body).Span);
        Assert.DoesNotContain("SECRET", peek, StringComparison.Ordinal);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, peek, StringComparison.Ordinal);

        var leftover = Encoding.UTF8.GetString(redactor.FlushTerminalStream("pane_pem_peek").Span);
        Assert.DoesNotContain("SECRET", leftover, StringComparison.Ordinal);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, leftover, StringComparison.Ordinal);
    }

    [Fact]
    public void Peek_closed_bytes_cover_suppress_until_terminator_tail()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var oversized = "sk-" + new string('a', (16 * 1024) + 8);
        _ = redactor.RedactTerminalBytes("pane_term_peek", Encoding.UTF8.GetBytes(oversized));
        var tail = Encoding.UTF8.GetBytes("TAILSECRET");
        Assert.True(redactor.RedactTerminalBytes("pane_term_peek", tail).IsEmpty);

        var closedOnly = Encoding.UTF8.GetString(redactor.RedactClosedTerminalBytes(tail).Span);
        Assert.Contains("TAILSECRET", closedOnly, StringComparison.Ordinal);

        var peek = Encoding.UTF8.GetString(
            redactor.PeekClosedTerminalBytes("pane_term_peek", tail).Span);
        Assert.DoesNotContain("TAILSECRET", peek, StringComparison.Ordinal);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, peek, StringComparison.Ordinal);

        var leftover = Encoding.UTF8.GetString(redactor.FlushTerminalStream("pane_term_peek").Span);
        Assert.DoesNotContain("TAILSECRET", leftover, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_bytes_redacted_before_base64()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var raw = Encoding.UTF8.GetBytes("export OPENAI_API_KEY=sk-abcdefghijklmnopqrstuvwxyz012345");
        var redacted = redactor.RedactTerminalBytes(raw);
        var text = Encoding.UTF8.GetString(redacted.Span);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz012345", text);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, text);
    }

    [Fact]
    public void Terminal_output_json_redact_skips_base64_data_field()
    {
        var redactor = new DefaultEventPayloadRedactor();
        // Craft valid base64 that embeds AWS-key-shaped / password-assignment traps.
        // Mid-stream '=' is not valid base64 padding, so use AKIA[0-9A-Z]{16} and
        // "token:" separators that PasswordInline / AwsAccessKey match on full-text rules.
        var trapBytes = FindBytesWhoseBase64Contains("AKIA");
        var b64 = Convert.ToBase64String(trapBytes);
        Assert.Matches(@"AKIA[0-9A-Z]{16}", b64);

        var payload =
            $$"""{"pane_id":"p_trap","encoding":"base64","data":"{{b64}}","byte_count":{{trapBytes.Length}}}""";

        // Full-text RedactText would corrupt AKIA spans; event-type aware path must not.
        var corrupted = DefaultEventPayloadRedactor.RedactText(payload);
        Assert.NotEqual(payload, corrupted);

        var safe = redactor.RedactJsonPayload(ProtocolEventTypes.TerminalOutput, payload);
        Assert.Equal(payload, safe);
        Assert.Equal(payload, redactor.RedactJsonPayload(ProtocolEventTypes.TerminalRender, payload));
        using var doc = JsonDocument.Parse(safe);
        var dataB64 = doc.RootElement.GetProperty("data").GetString()!;
        var decoded = Convert.FromBase64String(dataB64);
        Assert.Equal(trapBytes, decoded);
    }

    [Fact]
    public async Task Terminal_output_journal_payload_is_redacted_and_matches_live()
    {
        // Mirror ControlPlane EmitOutputAsync: redact bytes → base64 → AppendAsync → PostLive.
        // (No second-pass RedactJsonPayload over base64 — see terminal_output_json_redact_skips.)
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h07redact"));
        state.UpdateSession(s => s with { Name = "h07redact", LifecycleState = SessionLifecycle.Ready });

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var hub = new EventSubscriptionHub();
        var redactor = new DefaultEventPayloadRedactor();
        var secret = "API_TOKEN=supersecret_value_xyz\n";
        var data = Encoding.UTF8.GetBytes(secret);
        var redactedBytes = redactor.RedactTerminalBytes(data);
        var redactedText = Encoding.UTF8.GetString(redactedBytes.Span);
        Assert.DoesNotContain("supersecret_value_xyz", redactedText);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, redactedText);

        var b64 = Convert.ToBase64String(redactedBytes.Span);
        var payload =
            $$"""{"pane_id":"p_secret","encoding":"base64","data":"{{b64}}","byte_count":{{redactedBytes.Length}}}""";
        // Identity for terminal.output (defense in depth with EmitOutputAsync).
        payload = redactor.RedactJsonPayload(ProtocolEventTypes.TerminalOutput, payload);

        var append = await journal.AppendAsync(
            EventClass.Output,
            EventReliability.Output,
            ProtocolEventTypes.TerminalOutput,
            payload,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.False(append.IsOk);

        var livePosted = new RuntimeEventRecord
        {
            Seq = 1,
            Class = EventClass.Output,
            Reliability = EventReliability.Output,
            Type = ProtocolEventTypes.TerminalOutput,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = payload,
        };
        hub.PostLive(livePosted);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
        Assert.True(range.IsOk);
        Assert.DoesNotContain(range.Value, r => r.Type == ProtocolEventTypes.TerminalOutput);
        Assert.DoesNotContain("supersecret_value_xyz", livePosted.PayloadJson);

        using var doc = JsonDocument.Parse(livePosted.PayloadJson);
        var dataB64 = doc.RootElement.GetProperty("data").GetString()!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(dataB64));
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
        Assert.DoesNotContain("supersecret_value_xyz", decoded);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ControlPlane_emit_output_redacts_before_journal_and_live()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h07cpred"));
        state.UpdateSession(s => s with { Name = "h07cpred", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var hub = new EventSubscriptionHub();
        var sink = new CapturingSink();
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_redact",
            ConnectionId = "c_redact",
            FromSeq = 0,
            Classes = new HashSet<EventClass>(), // all
            ReplayBudget = 0,
            Live = true,
            Sink = sink,
        });
        sub.EnableLive();

        var secretBytes = Encoding.UTF8.GetBytes("export API_TOKEN=supersecret_cp_xyz\n");
        // Base64 trap content that would break if re-redacted as JSON text (AKIA false positive).
        var trapBytes = FindBytesWhoseBase64Contains("AKIA");
        var factory = new EmittingPaneFactory();
        var presentation = new DefaultAgentPresentationCompressor();
        var intelligence = new PaneIntelligencePipeline(compressor: presentation);
        var cp = new ControlPlaneService(
            state,
            factory,
            intelligence,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub,
            redactor: new DefaultEventPayloadRedactor(),
            presentation: presentation,
            evidence: new InMemoryRuntimeEvidenceJournal());

        var create = await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse(new JsonObject
            {
                ["command"] = "echo",
                ["cwd"] = _dir,
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;
        // terminal.output live delivery requires observe attachment (fail closed).
        sub.AttachPane(paneId, attachmentId: "att_redact", mode: "observe");

        factory.Emit(paneId, secretBytes);

        string? livePayload = null;
        long liveSeq = -1;
        for (var i = 0; i < 50 && livePayload is null; i++)
        {
            foreach (var line in sink.Lines.ToArray())
            {
                using var env = JsonDocument.Parse(line);
                if (!env.RootElement.TryGetProperty("params", out var prms))
                    continue;
                if (prms.GetProperty("type").GetString() != ProtocolEventTypes.TerminalOutput)
                    continue;
                liveSeq = prms.GetProperty("seq").GetInt64();
                livePayload = prms.GetProperty("payload").ValueKind == JsonValueKind.String
                    ? prms.GetProperty("payload").GetString()
                    : prms.GetProperty("payload").GetRawText();
                break;
            }

            if (livePayload is null)
                await Task.Delay(20);
        }

        Assert.NotNull(livePayload);
        using (var doc = JsonDocument.Parse(livePayload!))
        {
            var dataB64 = doc.RootElement.GetProperty("data").GetString()!;
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(dataB64));
            Assert.DoesNotContain("supersecret_cp_xyz", decoded);
            Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
        }

        Assert.DoesNotContain("supersecret_cp_xyz", livePayload);
        var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
        Assert.True(range.IsOk);
        Assert.DoesNotContain(range.Value, r => r.Type == ProtocolEventTypes.TerminalOutput);

        factory.Emit(paneId, trapBytes);
        string? trapPayload = null;
        long trapSeq = -1;
        for (var i = 0; i < 50; i++)
        {
            foreach (var line in sink.Lines.ToArray())
            {
                using var env = JsonDocument.Parse(line);
                if (!env.RootElement.TryGetProperty("params", out var prms))
                    continue;
                if (prms.GetProperty("type").GetString() != ProtocolEventTypes.TerminalOutput)
                    continue;
                var seq = prms.GetProperty("seq").GetInt64();
                if (seq == liveSeq)
                    continue;
                trapSeq = seq;
                trapPayload = prms.GetProperty("payload").ValueKind == JsonValueKind.String
                    ? prms.GetProperty("payload").GetString()
                    : prms.GetProperty("payload").GetRawText();
            }

            if (trapPayload is not null)
                break;
            await Task.Delay(20);
        }

        Assert.NotNull(trapPayload);
        Assert.NotEqual(liveSeq, trapSeq);
        using (var doc = JsonDocument.Parse(trapPayload!))
        {
            var dataB64 = doc.RootElement.GetProperty("data").GetString()!;
            var decoded = Convert.FromBase64String(dataB64);
            Assert.Equal(trapBytes, decoded);
        }

        sub.Close();
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ControlPlane_lifecycle_emit_uses_redactor()
    {
        // Lifecycle payload fields are controlled, but EmitLifecycleAsync must still go through
        // the injected redactor (not PassThrough) so secret-shaped state never lands durable.
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h07cplife"));
        state.UpdateSession(s => s with { Name = "h07cplife", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var redactor = new RecordingRedactor(new DefaultEventPayloadRedactor());
        var factory = new EmittingPaneFactory();
        var intelligence = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            factory,
            intelligence,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            redactor: redactor);

        await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse(new JsonObject
            {
                ["command"] = "true",
                ["cwd"] = _dir,
            }.ToJsonString()).RootElement,
            CancellationToken.None);

        Assert.Contains(redactor.JsonCalls, c => c.eventType == ProtocolEventTypes.PaneLifecycle);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 20, CancellationToken.None);
        Assert.True(range.IsOk);
        Assert.Contains(range.Value, r => r.Type == ProtocolEventTypes.PaneLifecycle);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Lifecycle_emit_redacts_secret_shaped_strings()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h07life"));
        state.UpdateSession(s => s with { Name = "h07life", LifecycleState = SessionLifecycle.Ready });

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var redactor = new DefaultEventPayloadRedactor();
        // Direct unit check: redactor on a lifecycle-shaped payload.
        var payload = """{"pane_id":"p1","state":"running","note":"PASSWORD=hunter2"}""";
        var redacted = redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, payload);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, redacted);

        await journal.DisposeAsync();
    }

    [Fact]
    public void Binding_changed_json_redact_preserves_opaque_ids()
    {
        var redactor = new DefaultEventPayloadRedactor();
        // Opaque Atomic fields can look secret-shaped (sk-…, API_TOKEN=… in project_root).
        // Full-text RedactText would corrupt join metadata; binding.changed is identity.
        var payload =
            """{"pane_id":"p1","binding":{"agent_session_id":"as_sk-abcdefghijklmnopqrstuvwxyz0123","run_id":"sk-abcdefghijklmnopqrstuvwxyz0123","step_id":"step_1","memory_id":"mem_1","project_root":"/workspace/API_TOKEN=notasecret","tenant_id":"ten_1"}}""";

        var corrupted = DefaultEventPayloadRedactor.RedactText(payload);
        Assert.NotEqual(payload, corrupted);

        var safe = redactor.RedactJsonPayload(ProtocolEventTypes.BindingChanged, payload);
        Assert.Equal(payload, safe);
        Assert.Contains("sk-abcdefghijklmnopqrstuvwxyz0123", safe);
        Assert.Contains("API_TOKEN=notasecret", safe);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_redacts_sk_token_split_across_two_stream_chunks()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_split", Encoding.UTF8.GetBytes(token[..12]));
        var second = redactor.RedactTerminalBytes("pane_split", Encoding.UTF8.GetBytes(token[12..]));
        var flush = redactor.FlushTerminalStream("pane_split");
        var joined = ConcatUtf8(first, second, flush);

        Assert.DoesNotContain(token, joined);
        Assert.DoesNotContain("sk-abcdefghij", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_redacts_sk_token_split_after_minimum_length()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var split = 25; // sk- + 22 alnum, above {20,}; \b-at-EOS must not commit the suffix
        Assert.True(split > 3 + 20);
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_sk_min", Encoding.UTF8.GetBytes(token[..split]));
        var second = redactor.RedactTerminalBytes("pane_sk_min", Encoding.UTF8.GetBytes(token[split..]));
        var flush = redactor.FlushTerminalStream("pane_sk_min");
        var joined = ConcatUtf8(first, second, flush);

        Assert.DoesNotContain(token, joined);
        Assert.DoesNotContain(token[split..], joined);
        Assert.DoesNotContain(token[..split], joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_redacts_ghp_token_split_after_minimum_length()
    {
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var split = 4 + 22;
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_ghp", Encoding.UTF8.GetBytes(token[..split]));
        var second = redactor.RedactTerminalBytes("pane_ghp", Encoding.UTF8.GetBytes(token[split..]));
        var flush = redactor.FlushTerminalStream("pane_ghp");
        var joined = ConcatUtf8(first, second, flush);

        Assert.DoesNotContain(token, joined);
        Assert.DoesNotContain(token[split..], joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_holds_api_token_assignment_split_at_equals()
    {
        const string value = "supersecret_value";
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_env", Encoding.UTF8.GetBytes("API_TOKEN="));
        var second = redactor.RedactTerminalBytes("pane_env", Encoding.UTF8.GetBytes(value));
        var flush = redactor.FlushTerminalStream("pane_env");
        var joined = ConcatUtf8(first, second, flush);

        Assert.DoesNotContain(value, joined);
        Assert.DoesNotContain("supersecret", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_holds_password_assignment_split_at_equals()
    {
        const string value = "hunter2";
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_pw", Encoding.UTF8.GetBytes("PASSWORD="));
        var second = redactor.RedactTerminalBytes("pane_pw", Encoding.UTF8.GetBytes(value));
        var flush = redactor.FlushTerminalStream("pane_pw");
        var joined = ConcatUtf8(first, second, flush);

        Assert.DoesNotContain(value, joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_holds_assignment_split_mid_value()
    {
        const string value = "supersecret_value_xyz";
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes(
            "pane_mid", Encoding.UTF8.GetBytes("export API_TOKEN=super"));
        var second = redactor.RedactTerminalBytes(
            "pane_mid", Encoding.UTF8.GetBytes("secret_value_xyz"));
        var flush = redactor.FlushTerminalStream("pane_mid");
        var joined = ConcatUtf8(first, second, flush);

        Assert.DoesNotContain(value, joined);
        Assert.DoesNotContain("supersecret", joined);
        Assert.DoesNotContain("secret_value_xyz", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_holds_bearer_and_inline_password_splits()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var b1 = redactor.RedactTerminalBytes("pane_br", Encoding.UTF8.GetBytes("Bearer eyJhbGciOi"));
        var b2 = redactor.RedactTerminalBytes("pane_br", Encoding.UTF8.GetBytes("JIUzI1NiIsInR5cCI6IkpXVCJ9"));
        var bFlush = redactor.FlushTerminalStream("pane_br");
        var bearer = ConcatUtf8(b1, b2, bFlush);
        Assert.DoesNotContain("eyJhbGciOi", bearer);
        Assert.DoesNotContain("JIUzI1NiIsInR5cCI6IkpXVCJ9", bearer);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, bearer);

        var p1 = redactor.RedactTerminalBytes("pane_pw2", Encoding.UTF8.GetBytes("password="));
        var p2 = redactor.RedactTerminalBytes("pane_pw2", Encoding.UTF8.GetBytes("hunter2"));
        var pFlush = redactor.FlushTerminalStream("pane_pw2");
        var password = ConcatUtf8(p1, p2, pFlush);
        Assert.DoesNotContain("hunter2", password);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, password);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_redacts_secret_before_incomplete_utf8_tail()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var chunk = new byte[Encoding.UTF8.GetByteCount(token) + 1];
        Encoding.UTF8.GetBytes(token, chunk);
        chunk[^1] = 0xE2; // incomplete 3-byte lead; whole span fails Utf8.IsValid

        var redactor = new DefaultEventPayloadRedactor();
        var stateless = Encoding.UTF8.GetString(redactor.RedactTerminalBytes(chunk).Span);
        Assert.DoesNotContain(token, stateless);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, stateless);

        var first = redactor.RedactTerminalBytes("pane_utf8", chunk);
        var flush = redactor.FlushTerminalStream("pane_utf8");
        var joined = ConcatUtf8(first, flush);
        Assert.DoesNotContain(token, joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_redacts_pem_split_across_two_stream_chunks()
    {
        const string pem =
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3VS5JJcds3xfnSECRET\n-----END RSA PRIVATE KEY-----";
        var split = pem.IndexOf("SECRET", StringComparison.Ordinal) + 3;
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_pem", Encoding.UTF8.GetBytes(pem[..split]));
        var second = redactor.RedactTerminalBytes("pane_pem", Encoding.UTF8.GetBytes(pem[split..]));
        var flush = redactor.FlushTerminalStream("pane_pem");
        var joined = ConcatUtf8(first, second, flush);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Z3VS5JJcds3xfnSECRET", joined);
        Assert.DoesNotContain("SECRET", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
        Assert.DoesNotContain("MIIE", Encoding.UTF8.GetString(first.Span));
    }

    [Fact]
    public void DefaultEventPayloadRedactor_flush_redacts_incomplete_secret_prefix()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_flush", Encoding.UTF8.GetBytes("ready sk-abc"));
        Assert.DoesNotContain("sk-abc", Encoding.UTF8.GetString(first.Span));
        var flush = redactor.FlushTerminalStream("pane_flush");
        var joined = ConcatUtf8(first, flush);
        Assert.DoesNotContain("sk-abc", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
        Assert.Contains("ready ", joined);
    }

    [Fact]
    public void RedactJsonPayload_password_inline_keeps_valid_json()
    {
        var redactor = new DefaultEventPayloadRedactor();
        const string input = """{"pane_id":"p1","note":"PASSWORD=hunter2"}""";
        var redacted = redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, input);
        Assert.DoesNotContain("hunter2", redacted);
        using var doc = JsonDocument.Parse(redacted);
        Assert.Equal("p1", doc.RootElement.GetProperty("pane_id").GetString());
        var note = doc.RootElement.GetProperty("note").GetString();
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, note);
        Assert.DoesNotContain("hunter2", note);
    }

    [Fact]
    public void RedactJsonPayload_pem_inside_string_keeps_valid_json()
    {
        var redactor = new DefaultEventPayloadRedactor();
        const string input =
            """{"pane_id":"p1","note":"-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3VS5JJcds3xfnSECRET\n-----END RSA PRIVATE KEY-----"}""";
        var redacted = redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, input);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Z3VS5JJcds3xfnSECRET", redacted);
        using var doc = JsonDocument.Parse(redacted);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.DoesNotContain('\n', redacted);
        Assert.Contains("\\n", redacted);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void RedactJsonPayload_identity_events_still_skip_full_text()
    {
        var redactor = new DefaultEventPayloadRedactor();
        const string payload =
            """{"pane_id":"p1","binding":{"run_id":"sk-abcdefghijklmnopqrstuvwxyz0123","project_root":"/workspace/API_TOKEN=notasecret"}}""";

        Assert.Equal(payload, redactor.RedactJsonPayload(ProtocolEventTypes.BindingChanged, payload));
        Assert.Equal(payload, redactor.RedactJsonPayload(ProtocolEventTypes.ExportAcked, payload));
        Assert.Equal(payload, redactor.RedactJsonPayload(ProtocolEventTypes.CheckpointLifecycle, payload));
        Assert.NotEqual(payload, redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, payload));
    }

    [Fact]
    public async Task ControlPlane_split_terminal_chunks_do_not_store_secret_in_journal()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var decoded = await EmitSplitAndReadJournalAsync(
            "h19split", token[..12], token[12..]);
        Assert.DoesNotContain(token, decoded);
        Assert.DoesNotContain("sk-abcdefghij", decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_split_api_token_assignment_absent_from_journal()
    {
        const string value = "supersecret_value";
        var decoded = await EmitSplitAndReadJournalAsync(
            "h19env", "API_TOKEN=", value);
        Assert.DoesNotContain(value, decoded);
        Assert.DoesNotContain("supersecret", decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_split_password_assignment_absent_from_journal()
    {
        const string value = "hunter2";
        var decoded = await EmitSplitAndReadJournalAsync(
            "h19pw", "PASSWORD=", value);
        Assert.DoesNotContain(value, decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_split_assignment_mid_value_absent_from_journal()
    {
        var decoded = await EmitSplitAndReadJournalAsync(
            "h19mid", "export API_TOKEN=super", "secret_value_xyz");
        Assert.DoesNotContain("supersecret_value_xyz", decoded);
        Assert.DoesNotContain("secret_value_xyz", decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_split_sk_after_minimum_absent_from_journal()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var split = 25;
        var decoded = await EmitSplitAndReadJournalAsync(
            "h19skmin", token[..split], token[split..]);
        Assert.DoesNotContain(token, decoded);
        Assert.DoesNotContain(token[split..], decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_split_openai_key_assignment_absent_from_journal()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var decoded = await EmitSplitAndReadJournalAsync(
            "h19oai", "export OPENAI_API_KEY=", token + "\n");
        Assert.DoesNotContain(token, decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_split_bearer_and_password_inline_absent_from_journal()
    {
        var bearer = await EmitSplitAndReadJournalAsync(
            "h19br", "Bearer eyJhbGciOi", "JIUzI1NiIsInR5cCI6IkpXVCJ9");
        Assert.DoesNotContain("eyJhbGciOi", bearer);
        Assert.DoesNotContain("JIUzI1NiIsInR5cCI6IkpXVCJ9", bearer);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, bearer);

        var password = await EmitSplitAndReadJournalAsync(
            "h19pwin", "password=", "hunter2");
        Assert.DoesNotContain("hunter2", password);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, password);
    }

    [Fact]
    public void RedactText_authorization_bearer_jwt_is_redacted()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.abc";
        var header = "Authorization: Bearer " + jwt;
        var redacted = DefaultEventPayloadRedactor.RedactText(header + "\n");
        Assert.DoesNotContain(jwt, redacted);
        Assert.DoesNotContain("eyJhbGciOi", redacted);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, redacted);
        Assert.Contains("Authorization", redacted, StringComparison.OrdinalIgnoreCase);

        var assigned = DefaultEventPayloadRedactor.RedactText("AUTHORIZATION=Bearer " + jwt);
        Assert.DoesNotContain(jwt, assigned);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, assigned);
    }

    [Fact]
    public void RedactText_numbered_fox_fill_without_secret_needles_is_unchanged()
    {
        var text = BuildNumberedFoxFill(10_000);
        var redacted = DefaultEventPayloadRedactor.RedactText(text);
        Assert.Same(text, redacted);
    }

    [Fact]
    public void RedactTerminalBytes_numbered_fox_chunk_without_secret_needles_keeps_payload()
    {
        var text = BuildNumberedFoxFill(80);
        var redactor = new DefaultEventPayloadRedactor();
        var data = Encoding.UTF8.GetBytes(text);
        var keyed = redactor.RedactTerminalBytes("pane_fill_plain", data);
        Assert.Equal(text, Encoding.UTF8.GetString(keyed.Span));
        var closed = redactor.RedactClosedTerminalBytes(data);
        Assert.Equal(text, Encoding.UTF8.GetString(closed.Span));
    }

    [Fact]
    public void RedactText_secret_among_fox_lines_is_still_redacted()
    {
        var noise = BuildNumberedFoxFill(4);
        var text = noise + "sk-abcdefghijklmnopqrstuvwxyz0123\n" + noise;
        var redacted = DefaultEventPayloadRedactor.RedactText(text);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz0123", redacted);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, redacted);

        var quoted = DefaultEventPayloadRedactor.RedactText("API_KEY=\"hunter2-secret\"");
        Assert.DoesNotContain("hunter2-secret", quoted);
        Assert.Contains("API_KEY=\"[REDACTED]\"", quoted);
    }

    private static string BuildNumberedFoxFill(int lineCount)
    {
        var words = "the quick brown fox jumps over the lazy dog while nine calm ravens watch from a cold stone wall near the river bank".Split(' ');
        var lines = new string[lineCount];
        for (var i = 1; i <= lineCount; i++)
        {
            var prefix = $"{i:00000} ";
            var width = 72 - prefix.Length;
            var window = string.Join(' ', Enumerable.Range(0, 20).Select(k => words[(i + k) % words.Length]));
            var body = (window + " " + window);
            if (body.Length > width)
                body = body[..width];
            body = body.PadRight(width, '.');
            lines[i - 1] = prefix + body;
            if (lines[i - 1].Length != 72)
                throw new InvalidOperationException($"fill line {i} length {lines[i - 1].Length}");
        }

        return string.Join('\n', lines) + "\n";
    }

    [Fact]
    public void DefaultEventPayloadRedactor_redacts_authorization_bearer_in_one_chunk()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.abc";
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes(
            "pane_auth1", Encoding.UTF8.GetBytes("Authorization: Bearer " + jwt + "\n"));
        var flush = redactor.FlushTerminalStream("pane_auth1");
        var joined = ConcatUtf8(first, flush);
        Assert.DoesNotContain(jwt, joined);
        Assert.DoesNotContain("eyJhbGciOi", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_redacts_authorization_bearer_split_across_chunks()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.abc";
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes(
            "pane_auth2", Encoding.UTF8.GetBytes("Authorization: Bearer eyJhbGciOi"));
        var second = redactor.RedactTerminalBytes(
            "pane_auth2", Encoding.UTF8.GetBytes("JIUzI1NiIsInR5cCI6IkpXVCJ9.e30.abc"));
        var flush = redactor.FlushTerminalStream("pane_auth2");
        var joined = ConcatUtf8(first, second, flush);
        Assert.DoesNotContain(jwt, joined);
        Assert.DoesNotContain("eyJhbGciOi", joined);
        Assert.DoesNotContain("JIUzI1NiIsInR5cCI6IkpXVCJ9", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_invalid_utf8_does_not_leak_held_sk_suffix()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var split = 25;
        Assert.True(split > 3 + 20);
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes(
            "pane_utf8hold", Encoding.UTF8.GetBytes(token[..split]));
        var secondBytes = new byte[Encoding.UTF8.GetByteCount(token[split..]) + 1];
        Encoding.UTF8.GetBytes(token.AsSpan()[split..], secondBytes);
        secondBytes[^1] = 0xFF;
        var second = redactor.RedactTerminalBytes("pane_utf8hold", secondBytes);
        var flush = redactor.FlushTerminalStream("pane_utf8hold");
        var joined = ConcatUtf8(first, second, flush);
        Assert.DoesNotContain(token, joined);
        Assert.DoesNotContain(token[split..], joined);
        Assert.DoesNotContain(token[..split], joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void RedactJsonPayload_secret_field_names_are_redacted()
    {
        var redactor = new DefaultEventPayloadRedactor();
        const string input = """{"password":"hunter2","api_key":"shortsecret","note":"ok"}""";
        var redacted = redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, input);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("shortsecret", redacted);
        using var doc = JsonDocument.Parse(redacted);
        Assert.Equal(DefaultEventPayloadRedactor.Replacement, doc.RootElement.GetProperty("password").GetString());
        Assert.Equal(DefaultEventPayloadRedactor.Replacement, doc.RootElement.GetProperty("api_key").GetString());
        Assert.Equal("ok", doc.RootElement.GetProperty("note").GetString());

        const string nested = """{"password":{"inner":"hunter2"},"OPENAI_API_KEY":"not-sk-shaped"}""";
        var nestedRedacted = redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, nested);
        Assert.DoesNotContain("hunter2", nestedRedacted);
        Assert.DoesNotContain("not-sk-shaped", nestedRedacted);
        using var nestedDoc = JsonDocument.Parse(nestedRedacted);
        Assert.Equal(
            DefaultEventPayloadRedactor.Replacement,
            nestedDoc.RootElement.GetProperty("password").GetString());
        Assert.Equal(
            DefaultEventPayloadRedactor.Replacement,
            nestedDoc.RootElement.GetProperty("OPENAI_API_KEY").GetString());
    }

    [Fact]
    public void DefaultEventPayloadRedactor_closed_pem_then_begin_split_redacts_second_key()
    {
        const string first =
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3FIRST\n-----END RSA PRIVATE KEY-----\n-----BEG";
        const string second =
            "IN RSA PRIVATE KEY-----\nSECRETMATERIAL\n-----END RSA PRIVATE KEY-----";
        var redactor = new DefaultEventPayloadRedactor();
        var a = redactor.RedactTerminalBytes("pane_pem2", Encoding.UTF8.GetBytes(first));
        var b = redactor.RedactTerminalBytes("pane_pem2", Encoding.UTF8.GetBytes(second));
        var flush = redactor.FlushTerminalStream("pane_pem2");
        var joined = ConcatUtf8(a, b, flush);
        Assert.DoesNotContain("SECRETMATERIAL", joined);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Z3FIRST", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void DefaultEventPayloadRedactor_holds_TOKEN_and_SECRET_split_before_equals()
    {
        const string tokenValue = "supersecret_token_value";
        const string secretValue = "supersecret_secret_value";
        var redactor = new DefaultEventPayloadRedactor();

        var t1 = redactor.RedactTerminalBytes("pane_tok", Encoding.UTF8.GetBytes("TOKEN"));
        var t2 = redactor.RedactTerminalBytes("pane_tok", Encoding.UTF8.GetBytes("=" + tokenValue));
        var tFlush = redactor.FlushTerminalStream("pane_tok");
        var tokenJoined = ConcatUtf8(t1, t2, tFlush);
        Assert.DoesNotContain(tokenValue, tokenJoined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, tokenJoined);

        var s1 = redactor.RedactTerminalBytes("pane_sec", Encoding.UTF8.GetBytes("SECRET"));
        var s2 = redactor.RedactTerminalBytes("pane_sec", Encoding.UTF8.GetBytes("=" + secretValue));
        var sFlush = redactor.FlushTerminalStream("pane_sec");
        var secretJoined = ConcatUtf8(s1, s2, sFlush);
        Assert.DoesNotContain(secretValue, secretJoined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, secretJoined);

        var m1 = redactor.RedactTerminalBytes("pane_tok2", Encoding.UTF8.GetBytes("Token"));
        var m2 = redactor.RedactTerminalBytes("pane_tok2", Encoding.UTF8.GetBytes("=" + tokenValue));
        var mFlush = redactor.FlushTerminalStream("pane_tok2");
        var mixed = ConcatUtf8(m1, m2, mFlush);
        Assert.DoesNotContain(tokenValue, mixed);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, mixed);
    }

    [Fact]
    public async Task ControlPlane_authorization_bearer_jwt_absent_from_journal()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.abc";
        var decoded = await EmitOnceAndReadJournalAsync(
            "h19auth1", "Authorization: Bearer " + jwt + "\n");
        Assert.DoesNotContain(jwt, decoded);
        Assert.DoesNotContain("eyJhbGciOi", decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_authorization_bearer_split_absent_from_journal()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.abc";
        var decoded = await EmitSplitAndReadJournalAsync(
            "h19auth2", "Authorization: Bearer eyJhbGciOi", "JIUzI1NiIsInR5cCI6IkpXVCJ9.e30.abc");
        Assert.DoesNotContain(jwt, decoded);
        Assert.DoesNotContain("eyJhbGciOi", decoded);
        Assert.DoesNotContain("JIUzI1NiIsInR5cCI6IkpXVCJ9", decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_invalid_utf8_after_held_sk_does_not_journal_suffix()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var split = 25;
        var second = new byte[Encoding.UTF8.GetByteCount(token[split..]) + 1];
        Encoding.UTF8.GetBytes(token.AsSpan()[split..], second);
        second[^1] = 0xFF;
        var decoded = await EmitSplitBytesAndReadJournalAsync(
            "h19utf8hold", Encoding.UTF8.GetBytes(token[..split]), second);
        Assert.DoesNotContain(token, decoded);
        Assert.DoesNotContain(token[split..], decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_closed_pem_header_split_does_not_journal_second_body()
    {
        const string first =
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3FIRST\n-----END RSA PRIVATE KEY-----\n-----BEG";
        const string second =
            "IN RSA PRIVATE KEY-----\nSECRETMATERIAL\n-----END RSA PRIVATE KEY-----";
        var decoded = await EmitSplitAndReadJournalAsync("h19pem2", first, second);
        Assert.DoesNotContain("SECRETMATERIAL", decoded);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Z3FIRST", decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
    }

    [Fact]
    public async Task ControlPlane_TOKEN_and_SECRET_case_split_absent_from_journal()
    {
        var token = await EmitSplitAndReadJournalAsync(
            "h19tok", "TOKEN", "=supersecret_token_value");
        Assert.DoesNotContain("supersecret_token_value", token);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, token);

        var secret = await EmitSplitAndReadJournalAsync(
            "h19sec", "SECRET", "=supersecret_secret_value");
        Assert.DoesNotContain("supersecret_secret_value", secret);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, secret);

        var mixed = await EmitSplitAndReadJournalAsync(
            "h19tokm", "Token", "=supersecret_token_value");
        Assert.DoesNotContain("supersecret_token_value", mixed);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, mixed);
    }

    [Fact]
    public async Task Journal_json_secret_fields_stay_valid_and_omit_secrets()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h19jsonfields"));
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var redactor = new DefaultEventPayloadRedactor();
        const string input = """{"password":"hunter2","api_key":"hunter2","note":"ok"}""";
        var redacted = redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, input);
        Assert.True(HyjrPayloadJson.IsJsonValue(redacted));
        Assert.DoesNotContain("hunter2", redacted);

        var append = await journal.AppendAsync(
            EventClass.Lifecycle,
            EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle,
            redacted,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);
        Assert.DoesNotContain("hunter2", append.Value.PayloadJson);
        using (var live = JsonDocument.Parse(append.Value.PayloadJson))
        {
            Assert.Equal(DefaultEventPayloadRedactor.Replacement, live.RootElement.GetProperty("password").GetString());
            Assert.Equal(DefaultEventPayloadRedactor.Replacement, live.RootElement.GetProperty("api_key").GetString());
        }

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 10, CancellationToken.None);
        Assert.True(range.IsOk);
        var durable = range.Value.Single(r => r.Type == ProtocolEventTypes.PaneLifecycle);
        Assert.DoesNotContain("hunter2", durable.PayloadJson);
        using var stored = JsonDocument.Parse(durable.PayloadJson);
        Assert.Equal(JsonValueKind.Object, stored.RootElement.ValueKind);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ControlPlane_60kib_nonscret_chunk_stays_parseable_terminal_output()
    {
        var raw = new byte[60 * 1024];
        Array.Fill(raw, (byte)'x');
        var (cp, paneId, journal, factory) = await StartEmittingPlaneAsync("h19big");
        factory.Emit(paneId, raw);

        string? payloadJson = null;
        for (var i = 0; i < 50 && payloadJson is null; i++)
        {
            payloadJson = FirstLiveTerminalPayload(_outputSink);
            if (payloadJson is null)
                await Task.Delay(20);
        }

        Assert.NotNull(payloadJson);
        Assert.DoesNotContain("redacted_payload", payloadJson);
        using var doc = JsonDocument.Parse(payloadJson);
        Assert.False(
            doc.RootElement.TryGetProperty("opaque", out var opaque) && opaque.GetBoolean());
        Assert.Equal("base64", doc.RootElement.GetProperty("encoding").GetString());
        var decoded = Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString()!);
        Assert.Equal(raw, decoded);

        await cp.DispatchAsync(
            "pane.close",
            JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
            CancellationToken.None);
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ControlPlane_default_construction_redacts_secret_from_journal()
    {
        const string secret = "supersecret_default_path_xyz";
        var (cp, paneId, journal, factory) = await StartEmittingPlaneAsync(
            "h19default", injectRedactor: false);
        factory.Emit(paneId, Encoding.UTF8.GetBytes("export API_TOKEN=" + secret + "\n"));
        var decoded = "";
        for (var i = 0; i < 150 && !decoded.Contains(DefaultEventPayloadRedactor.Replacement, StringComparison.Ordinal); i++)
        {
            decoded = "";
            foreach (var payload in LiveTerminalPayloads(_outputSink))
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("opaque", out var opaque) && opaque.GetBoolean())
                    continue;
                var dataB64 = doc.RootElement.GetProperty("data").GetString()!;
                decoded += Encoding.UTF8.GetString(Convert.FromBase64String(dataB64));
            }

            if (decoded.Contains(DefaultEventPayloadRedactor.Replacement, StringComparison.Ordinal))
                break;
            await Task.Delay(20);
        }

        await cp.DispatchAsync(
            "pane.close",
            JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
            CancellationToken.None);
        Assert.DoesNotContain(secret, decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
        await AssertJournalBytesOmitSecretsAsync(journal, secret);
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ControlPlane_64kib_secret_boundary_absent_from_journal()
    {
        // Completed sk- token (not an unterminated KEY= hold). Token bytes straddle
        // the 64 KiB raw cap so span[..maxRaw] runs on already-redacted output.
        const string secret = "sk-H1964KBOUNDARYSECRETUNIQXYZ";
        const string secretPrefix = "sk-H1964KBOU";
        const string secretSuffix = "NDARYSECRETUNIQXYZ";
        const int cut = 64 * 1024;
        const int tailPad = 8 * 1024;
        Assert.Equal(12, secretPrefix.Length);
        Assert.Equal(secret, secretPrefix + secretSuffix);
        Assert.True(secret.Length >= 23, "sk- token must match SkToken (20+ chars after prefix)");

        var raw = new byte[cut + tailPad];
        Array.Fill(raw, (byte)'x');
        var secretOffset = cut - secretPrefix.Length;
        raw[secretOffset - 1] = (byte)'\n';
        Encoding.UTF8.GetBytes(secret, raw.AsSpan(secretOffset));
        raw[secretOffset + secret.Length] = (byte)'\n';

        var (cp, paneId, journal, factory) = await StartEmittingPlaneAsync("h19bound");
        var expectedMaxRaw = ExpectedTerminalOutputRawCap(paneId);
        Assert.Equal(cut, expectedMaxRaw);

        factory.Emit(paneId, raw);

        var records = Array.Empty<string>();
        var sawBoundedRecord = false;
        for (var i = 0; i < 150; i++)
        {
            records = LiveTerminalPayloads(_outputSink);
            sawBoundedRecord = records.Any(payload =>
            {
                using var parsed = JsonDocument.Parse(payload);
                return parsed.RootElement.TryGetProperty("byte_count", out var count)
                    && count.GetInt32() == expectedMaxRaw;
            });
            if (sawBoundedRecord)
                break;
            await Task.Delay(20);
        }

        Assert.NotEmpty(records);
        Assert.True(sawBoundedRecord, "WriteTerminalOutputCoreAsync did not take the 64 KiB truncation branch");

        foreach (var rec in records)
        {
            using var doc = JsonDocument.Parse(rec);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.DoesNotContain(secret, rec);
            Assert.DoesNotContain(secretPrefix, rec);
            Assert.DoesNotContain(secretSuffix, rec);

            if (doc.RootElement.TryGetProperty("opaque", out var opaque) && opaque.GetBoolean())
                continue;

            Assert.Equal("base64", doc.RootElement.GetProperty("encoding").GetString());
            var decodedBytes = Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString()!);
            var decoded = Encoding.UTF8.GetString(decodedBytes);
            var byteCount = doc.RootElement.GetProperty("byte_count").GetInt32();
            Assert.Equal(decodedBytes.Length, byteCount);
            Assert.True(byteCount <= expectedMaxRaw, "terminal.output exceeded the 64 KiB raw cap");
            Assert.DoesNotContain(secret, decoded);
            Assert.DoesNotContain(secretPrefix, decoded);
            Assert.DoesNotContain(secretSuffix, decoded);

            if (byteCount != expectedMaxRaw)
                continue;

            Assert.Equal(expectedMaxRaw, decoded.Length);
            Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
        }

        await cp.DispatchAsync(
            "pane.close",
            JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
            CancellationToken.None);
        await AssertJournalBytesOmitSecretsAsync(journal, secret, secretPrefix, secretSuffix);
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ControlPlane_incomplete_utf8_tail_does_not_store_secret_in_journal()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var chunk = new byte[Encoding.UTF8.GetByteCount(token) + 1];
        Encoding.UTF8.GetBytes(token, chunk);
        chunk[^1] = 0xE2;

        var (cp, paneId, journal, factory) = await StartEmittingPlaneAsync("h19utf8");
        factory.Emit(paneId, chunk);
        await cp.DispatchAsync(
            "pane.close",
            JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
            CancellationToken.None);

        var decoded = await WaitConcatTerminalOutputAsync(journal, 1);
        Assert.DoesNotContain(token, decoded);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, decoded);
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ControlPlane_redacted_lifecycle_json_is_parseable_in_journal()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h19lifejson"));
        state.UpdateSession(s => s with { Name = "h19lifejson", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var redactor = new DefaultEventPayloadRedactor();
        var factory = new EmittingPaneFactory();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            redactor: redactor);

        await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse(new JsonObject
            {
                ["command"] = "true",
                ["cwd"] = _dir,
            }.ToJsonString()).RootElement,
            CancellationToken.None);

        const string dangerous = """{"pane_id":"p1","state":"running","note":"PASSWORD=hunter2"}""";
        var redacted = redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, dangerous);
        Assert.True(HyjrPayloadJson.IsJsonValue(redacted));
        var append = await journal.AppendAsync(
            EventClass.Lifecycle,
            EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle,
            redacted,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
        Assert.True(range.IsOk);
        foreach (var rec in range.Value.Where(r => r.Type == ProtocolEventTypes.PaneLifecycle))
        {
            using var payload = JsonDocument.Parse(rec.PayloadJson);
            Assert.True(
                payload.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array);
            Assert.DoesNotContain("hunter2", rec.PayloadJson);
            var stored = HyjrPayloadJson.WrapStoredPayload(rec.Type, rec.OccurredAt, rec.PayloadJson);
            using var env = JsonDocument.Parse(stored);
            Assert.True(HyjrPayloadJson.TryUnwrapStoredPayload(
                stored, out var type, out _, out var inner));
            Assert.Equal(ProtocolEventTypes.PaneLifecycle, type);
            using var innerDoc = JsonDocument.Parse(inner);
            Assert.Equal(JsonValueKind.Object, innerDoc.RootElement.ValueKind);
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task FileRuntimeEventJournal_non_json_payload_stored_as_opaque_object()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h19opaque"));
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        const string secret = "PASSWORD=hunter2 not-json -----BEGIN RSA PRIVATE KEY-----\nSECRET\n";
        var append = await journal.AppendAsync(
            EventClass.Lifecycle,
            EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle,
            secret,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);

        using (var live = JsonDocument.Parse(append.Value.PayloadJson))
        {
            Assert.True(live.RootElement.GetProperty("opaque").GetBoolean());
            Assert.Equal("redacted_payload", live.RootElement.GetProperty("kind").GetString());
            Assert.Equal("utf8", live.RootElement.GetProperty("encoding").GetString());
            Assert.Equal("[REDACTED]", live.RootElement.GetProperty("data").GetString());
        }

        Assert.DoesNotContain("hunter2", append.Value.PayloadJson);
        Assert.DoesNotContain("SECRET", append.Value.PayloadJson);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 10, CancellationToken.None);
        Assert.True(range.IsOk);
        var durable = range.Value.Single(r => r.Type == ProtocolEventTypes.PaneLifecycle);
        using var stored = JsonDocument.Parse(durable.PayloadJson);
        Assert.True(stored.RootElement.GetProperty("opaque").GetBoolean());
        Assert.DoesNotContain("hunter2", durable.PayloadJson);

        var wrapped = HyjrPayloadJson.WrapStoredPayload(
            durable.Type, durable.OccurredAt, durable.PayloadJson);
        using var env = JsonDocument.Parse(wrapped);
        Assert.True(HyjrPayloadJson.TryUnwrapStoredPayload(wrapped, out _, out _, out var inner));
        using var innerDoc = JsonDocument.Parse(inner);
        Assert.Equal(JsonValueKind.Object, innerDoc.RootElement.ValueKind);

        await journal.DisposeAsync();
    }

    private async Task<(ControlPlaneService Cp, string PaneId, FileRuntimeEventJournal Journal, EmittingPaneFactory Factory)>
        StartEmittingPlaneAsync(string sessionName, bool injectRedactor = true)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New(sessionName));
        state.UpdateSession(s => s with { Name = sessionName, LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var factory = new EmittingPaneFactory();
        var presentation = new DefaultAgentPresentationCompressor();
        var intelligence = new PaneIntelligencePipeline(compressor: presentation);
        var hub = new EventSubscriptionHub();
        _outputSink = new CapturingSink();
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_live_output",
            ConnectionId = "c_redact",
            FromSeq = 0,
            Classes = new HashSet<EventClass>(),
            ReplayBudget = 0,
            Live = true,
            Sink = _outputSink,
        });
        sub.EnableLive();
        var cp = injectRedactor
            ? new ControlPlaneService(
                state,
                factory,
                intelligence,
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: hub,
                redactor: new DefaultEventPayloadRedactor(),
                presentation: presentation,
                evidence: new InMemoryRuntimeEvidenceJournal())
            : new ControlPlaneService(
                state,
                factory,
                intelligence,
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: hub,
                presentation: presentation,
                evidence: new InMemoryRuntimeEvidenceJournal());

        var create = await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse(new JsonObject
            {
                ["command"] = "echo",
                ["cwd"] = _dir,
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;
        sub.AttachPane(paneId, attachmentId: "att_live_output", mode: "observe");

        return (cp, paneId, journal, factory);
    }

    private async Task<string> EmitSplitAndReadJournalAsync(
        string sessionName, string first, string second)
    {
        return await EmitSplitBytesAndReadJournalAsync(
            sessionName, Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
    }

    private async Task<string> EmitOnceAndReadJournalAsync(string sessionName, string data)
    {
        var (cp, paneId, journal, factory) = await StartEmittingPlaneAsync(sessionName);
        factory.Emit(paneId, Encoding.UTF8.GetBytes(data));
        await cp.DispatchAsync(
            "pane.close",
            JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
            CancellationToken.None);

        var decoded = await WaitConcatTerminalOutputAsync(journal, data.Length);
        await journal.DisposeAsync();
        return decoded;
    }

    private async Task<string> EmitSplitBytesAndReadJournalAsync(
        string sessionName, byte[] first, byte[] second)
    {
        var (cp, paneId, journal, factory) = await StartEmittingPlaneAsync(sessionName);
        factory.Emit(paneId, first);
        factory.Emit(paneId, second);
        await cp.DispatchAsync(
            "pane.close",
            JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
            CancellationToken.None);

        var decoded = await WaitConcatTerminalOutputAsync(
            journal, first.Length + second.Length);
        await journal.DisposeAsync();
        return decoded;
    }

    private async Task<string> WaitConcatTerminalOutputAsync(
        FileRuntimeEventJournal journal, int minDecodedChars)
    {
        var decoded = "";
        for (var i = 0; i < 150; i++)
        {
            var sb = new StringBuilder();
            foreach (var payload in LiveTerminalPayloads(_outputSink))
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("opaque", out var opaque) && opaque.GetBoolean())
                    continue;
                var dataB64 = doc.RootElement.GetProperty("data").GetString()!;
                sb.Append(Encoding.UTF8.GetString(Convert.FromBase64String(dataB64)));
            }

            decoded = sb.ToString();
            if (decoded.Length >= Math.Min(minDecodedChars, DefaultEventPayloadRedactor.Replacement.Length)
                && decoded.Contains(DefaultEventPayloadRedactor.Replacement, StringComparison.Ordinal))
            {
                var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
                Assert.True(range.IsOk);
                Assert.DoesNotContain(range.Value, r => r.Type == ProtocolEventTypes.TerminalOutput);
                return decoded;
            }

            await Task.Delay(20);
        }

        if (_outputSink is not null && !decoded.Contains(DefaultEventPayloadRedactor.Replacement, StringComparison.Ordinal))
        {
            var types = new StringBuilder();
            foreach (var line in _outputSink.Lines.ToArray())
            {
                using var env = JsonDocument.Parse(line);
                if (env.RootElement.TryGetProperty("params", out var prms)
                    && prms.TryGetProperty("type", out var type))
                    types.Append(type.GetString()).Append(',');
            }

            Assert.Fail("live output missing replacement; types=" + types);
        }

        return decoded;
    }

    private static string? FirstLiveTerminalPayload(CapturingSink? sink) =>
        LiveTerminalPayloads(sink).FirstOrDefault();

    private static string[] LiveTerminalPayloads(CapturingSink? sink)
    {
        if (sink is null)
            return [];
        var found = new List<string>();
        foreach (var line in sink.Lines.ToArray())
        {
            using var env = JsonDocument.Parse(line);
            if (!env.RootElement.TryGetProperty("params", out var prms))
                continue;
            if (prms.GetProperty("type").GetString() != ProtocolEventTypes.TerminalOutput)
                continue;
            var payload = prms.GetProperty("payload");
            found.Add(payload.ValueKind == JsonValueKind.String
                ? payload.GetString()!
                : payload.GetRawText());
        }

        return found.ToArray();
    }

    private async Task AssertJournalBytesOmitSecretsAsync(
        FileRuntimeEventJournal journal, params string[] secrets)
    {
        var range = await journal.ReadRangeAsync(0, classes: null, budget: 100, CancellationToken.None);
        Assert.True(range.IsOk);
        foreach (var rec in range.Value)
        {
            using var parsed = JsonDocument.Parse(rec.PayloadJson);
            Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, rec.PayloadJson);
        }

        if (!Directory.Exists(_paths.JournalDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr"))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, text);
                var secretBytes = Encoding.UTF8.GetBytes(secret);
                Assert.Equal(-1, bytes.AsSpan().IndexOf(secretBytes));
            }
        }
    }

    private static int ExpectedTerminalOutputRawCap(string paneId)
    {
        const int maxRaw = 64 * 1024;
        var empty = RuntimeEventPayloadJson.WriteTerminalOutput(paneId, "", 0);
        var emptyBytes = Encoding.UTF8.GetByteCount(empty);
        var n = maxRaw;
        while (n > 0)
        {
            var b64 = ((n + 2) / 3) * 4;
            var total = emptyBytes + b64 + (n.ToString().Length - 1);
            if (total <= HyjrPayloadJson.MaxPayloadBytes)
                return n;
            var overflow = total - HyjrPayloadJson.MaxPayloadBytes;
            n -= Math.Max(3, ((overflow + 3) / 4) * 3);
        }

        return 0;
    }

    private static int? TerminalOutputByteCount(RuntimeEventRecord rec)
    {
        using var doc = JsonDocument.Parse(rec.PayloadJson);
        if (doc.RootElement.TryGetProperty("opaque", out var opaque) && opaque.GetBoolean())
            return null;
        if (!doc.RootElement.TryGetProperty("byte_count", out var count))
            return null;
        return count.GetInt32();
    }

    private static string ConcatUtf8(params ReadOnlyMemory<byte>[] chunks)
    {
        var sb = new StringBuilder();
        foreach (var chunk in chunks)
        {
            if (!chunk.IsEmpty)
                sb.Append(Encoding.UTF8.GetString(chunk.Span));
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Binding_changed_journal_preserves_opaque_ids_matching_stored_binding()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h07bindred"));
        state.UpdateSession(s => s with { Name = "h07bindred", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var presentation = new DefaultAgentPresentationCompressor();
        var intelligence = new PaneIntelligencePipeline(compressor: presentation);
        var cp = new ControlPlaneService(
            state,
            new BindingOpaqueIdPaneFactory(),
            intelligence,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            redactor: new DefaultEventPayloadRedactor(),
            presentation: presentation,
            evidence: new InMemoryRuntimeEvidenceJournal());

        const string runId = "sk-abcdefghijklmnopqrstuvwxyz0123";
        const string projectRoot = "/tmp/API_TOKEN=opaque_path_value";
        await cp.DispatchAsync(
            ProtocolMethods.RuntimeBindingSet,
            JsonDocument.Parse(new JsonObject
            {
                ["binding"] = new JsonObject
                {
                    ["agent_session_id"] = "as_1",
                    ["run_id"] = runId,
                    ["step_id"] = "step_1",
                    ["project_root"] = projectRoot,
                    ["tenant_id"] = "ten_1",
                },
            }.ToJsonString()).RootElement,
            CancellationToken.None);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
        Assert.True(range.IsOk);
        var changed = range.Value.Single(r => r.Type == ProtocolEventTypes.BindingChanged);
        Assert.Contains(runId, changed.PayloadJson);
        Assert.Contains(projectRoot, changed.PayloadJson);
        Assert.DoesNotContain(DefaultEventPayloadRedactor.Replacement, changed.PayloadJson);

        // Stored session binding equals journal payload binding object.
        var stored = state.Snapshot().Binding!;
        Assert.Equal(runId, stored.RunId);
        Assert.Equal(projectRoot, stored.ProjectRoot);
        using var doc = JsonDocument.Parse(changed.PayloadJson);
        Assert.Equal(runId, doc.RootElement.GetProperty("binding").GetProperty("run_id").GetString());
        Assert.Equal(projectRoot, doc.RootElement.GetProperty("binding").GetProperty("project_root").GetString());

        await journal.DisposeAsync();
    }

    private sealed class BindingOpaqueIdPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) => new NoopPaneRuntime(options.Id);
    }

    private sealed class NoopPaneRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 42_200;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Build bytes whose standard base64 encoding contains an AWS-key-shaped span
    /// (<c>AKIA</c> + 16 A-Z0-9) so full-text redaction would false-positive.
    /// </summary>
    private static byte[] FindBytesWhoseBase64Contains(string needle)
    {
        // Prefer a constructed valid base64 string that embeds AKIA + 16 alphanumerics.
        // Base64 alphabet is A-Za-z0-9+/=; AKIA + 16 A-Z0-9 is entirely in-alphabet.
        if (string.Equals(needle, "AKIA", StringComparison.Ordinal))
        {
            // 20-char AKIA key shape. Prefix with non-word base64 (+/) so \bAKIA… matches.
            const string keyShape = "AKIAIOSFODNN7EXAMPLE"; // 20 chars
            var crafted = "++/+" + keyShape + "+/++"; // length 28
            while (crafted.Length % 4 != 0)
                crafted += "A";
            var bytes = Convert.FromBase64String(crafted);
            var roundTrip = Convert.ToBase64String(bytes);
            Assert.Contains("AKIA", roundTrip, StringComparison.Ordinal);
            Assert.Matches(@"AKIA[0-9A-Z]{16}", roundTrip);
            // Prove full-text rules would false-positive on this base64 alone.
            var redactedB64 = DefaultEventPayloadRedactor.RedactText(roundTrip);
            Assert.NotEqual(roundTrip, redactedB64);
            return bytes;
        }

        // Generic search for other needles (short random buffers).
        var rng = new Random(42);
        var buf = new byte[48];
        for (var attempt = 0; attempt < 200_000; attempt++)
        {
            rng.NextBytes(buf);
            var b64 = Convert.ToBase64String(buf);
            if (b64.Contains(needle, StringComparison.Ordinal))
                return (byte[])buf.Clone();
        }

        throw new InvalidOperationException($"Could not find bytes whose base64 contains '{needle}'");
    }

    private sealed class RecordingRedactor(IEventPayloadRedactor inner) : IEventPayloadRedactor
    {
        public List<(string eventType, string payload)> JsonCalls { get; } = new();

        public string RedactJsonPayload(string eventType, string payloadJson)
        {
            JsonCalls.Add((eventType, payloadJson));
            return inner.RedactJsonPayload(eventType, payloadJson);
        }

        public ReadOnlyMemory<byte> RedactTerminalBytes(ReadOnlyMemory<byte> data) =>
            inner.RedactTerminalBytes(data);

        public ReadOnlyMemory<byte> RedactClosedTerminalBytes(ReadOnlyMemory<byte> data) =>
            inner.RedactClosedTerminalBytes(data);

        public ReadOnlyMemory<byte> PeekClosedTerminalBytes(string streamKey, ReadOnlyMemory<byte> data) =>
            inner.PeekClosedTerminalBytes(streamKey, data);

        public ReadOnlyMemory<byte> RedactTerminalBytes(string streamKey, ReadOnlyMemory<byte> data) =>
            inner.RedactTerminalBytes(streamKey, data);

        public ReadOnlyMemory<byte> FlushTerminalStream(string streamKey) =>
            inner.FlushTerminalStream(streamKey);
    }

    private sealed class CapturingSink : IEventPushSink
    {
        private readonly object _gate = new();
        public string ConnectionId => "c_redact";
        public List<string> Lines { get; } = new();

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                Lines.Add(jsonLine);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake pane that raises OutputReceived on demand so ControlPlane EmitOutputAsync
    /// is exercised with the injected redactor after observe attach.
    /// </summary>
    private sealed class EmittingPaneFactory : IPaneRuntimeFactory
    {
        private EmittingPaneRuntime? _last;

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            _last = new EmittingPaneRuntime(options.Id);
            return _last;
        }

        public void Emit(string paneId, byte[] data)
        {
            if (_last is null || !string.Equals(_last.Id.Value, paneId, StringComparison.Ordinal))
                throw new InvalidOperationException("No matching pane to emit output");
            _last.EmitNow(data);
        }
    }

    private sealed class EmittingPaneRuntime : IPaneRuntime
    {
        public EmittingPaneRuntime(PaneId id) => Id = id;

        public PaneId Id { get; }
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 42_001;
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
#pragma warning disable CS0067
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }

        public void EmitNow(byte[] data) => OutputReceived?.Invoke(this, data);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
