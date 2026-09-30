using System.Text;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt;

namespace VtGoldenGen;

/// <summary>
/// Offline PTY capture for recorded goldens.
/// Uses hypa-pty-host (no managed fork). Writes raw bytes and script.ndjson.
/// </summary>
internal static class PtyRecorder
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? cmd = null;
        var cmdArgs = new List<string>();
        var cwd = "/private/tmp/hypa-fixture";
        var outDir = Path.GetTempPath();
        var name = "capture";
        var cols = 120;
        var rows = 40;
        var steps = new List<string>();
        string? helper = null;
        var extraEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        var passParentEnv = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cmd":
                    cmd = RequireValue(args, ref i, "--cmd");
                    break;
                case "--arg":
                    cmdArgs.Add(RequireValue(args, ref i, "--arg"));
                    break;
                case "--cwd":
                    cwd = RequireValue(args, ref i, "--cwd");
                    break;
                case "--out":
                    outDir = RequireValue(args, ref i, "--out");
                    break;
                case "--name":
                    name = RequireValue(args, ref i, "--name");
                    break;
                case "--cols":
                    cols = int.Parse(RequireValue(args, ref i, "--cols"));
                    break;
                case "--rows":
                    rows = int.Parse(RequireValue(args, ref i, "--rows"));
                    break;
                case "--step":
                    steps.Add(RequireValue(args, ref i, "--step"));
                    break;
                case "--helper":
                    helper = RequireValue(args, ref i, "--helper");
                    break;
                case "--env":
                    {
                        var pair = RequireValue(args, ref i, "--env");
                        var eq = pair.IndexOf('=');
                        if (eq <= 0)
                        {
                            Console.Error.WriteLine("Invalid --env " + pair);
                            return 2;
                        }

                        extraEnv[pair[..eq]] = pair[(eq + 1)..];
                        break;
                    }
                case "--pass-parent-env":
                    passParentEnv = true;
                    break;
                default:
                    Console.Error.WriteLine("Unknown --record-pty flag: " + args[i]);
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(cmd) || !File.Exists(cmd))
        {
            Console.Error.WriteLine("Missing --cmd executable.");
            return 2;
        }

        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(outDir);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["LANG"] = "C.UTF-8",
            ["TZ"] = "UTC",
            ["COLUMNS"] = cols.ToString(),
            ["LINES"] = rows.ToString(),
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/tmp",
        };
        if (passParentEnv)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = entry.Key as string;
                var value = entry.Value as string;
                if (key is null || value is null)
                    continue;
                if (ChildEnvironmentPolicy.IsSecretKey(key))
                    continue;
                env[key] = value;
            }
        }

        foreach (var kv in extraEnv)
            env[kv.Key] = kv.Value;

        var hostOptions = new PtyHostOptions();
        if (!string.IsNullOrWhiteSpace(helper))
            hostOptions = hostOptions with { HelperPath = helper };

        await using var process = await PtyHostProcess.SpawnAsync(
            cmd,
            cmdArgs,
            cwd,
            cols,
            rows,
            env,
            hostOptions);

        using var buffer = new MemoryStream();
        var bufferLock = new object();
        var replies = new QueryReplies();
        using var readCts = new CancellationTokenSource();
        var readTask = PumpOutputAsync(
            process,
            buffer,
            bufferLock,
            replies,
            readCts.Token);

        try
        {
            foreach (var step in steps)
            {
                var status = await ApplyStepAsync(process, buffer, bufferLock, step, outDir, name);
                if (status != 0)
                    return status;
            }
        }
        finally
        {
            try
            {
                if (process.IsRunning)
                    process.StandardInput.Write(new byte[] { 0x03 });
            }
            catch
            {
                // Best effort interrupt.
            }

            readCts.Cancel();
            try { await readTask; } catch { /* cancelled */ }
        }

        return 0;
    }

    private static async Task<int> ApplyStepAsync(
        PtyHostProcess process,
        MemoryStream buffer,
        object bufferLock,
        string step,
        string outDir,
        string name)
    {
        var split = step.IndexOf(':');
        var kind = split < 0 ? step : step[..split];
        var payload = split < 0 ? "" : step[(split + 1)..];

        switch (kind)
        {
            case "wait":
                await Task.Delay(int.Parse(payload));
                return 0;
            case "wait-contains":
                {
                    var parts = payload.Split(':', 2);
                    var needle = Unescape(parts[0]);
                    var timeoutMs = parts.Length > 1 ? int.Parse(parts[1]) : 15000;
                    if (!await WaitContainsAsync(buffer, bufferLock, needle, timeoutMs))
                    {
                        Console.Error.WriteLine("Timeout waiting for: " + needle);
                        DumpPreview(buffer, bufferLock);
                        return 1;
                    }

                    return 0;
                }
            case "wait-any":
                {
                    var parts = payload.Split(':', 2);
                    var needles = parts[0].Split('|');
                    var timeoutMs = parts.Length > 1 ? int.Parse(parts[1]) : 15000;
                    if (!await WaitAnyAsync(buffer, bufferLock, needles, timeoutMs))
                    {
                        Console.Error.WriteLine("Timeout waiting for any: " + parts[0]);
                        DumpPreview(buffer, bufferLock);
                        return 1;
                    }

                    return 0;
                }
            case "send":
                {
                    var bytes = Encoding.UTF8.GetBytes(Unescape(payload));
                    await process.StandardInput.WriteAsync(bytes);
                    await process.StandardInput.FlushAsync();
                    return 0;
                }
            case "send-hex":
                {
                    var hex = payload.Replace(" ", "", StringComparison.Ordinal);
                    if (hex.Length % 2 != 0)
                    {
                        Console.Error.WriteLine("send-hex needs even hex digits");
                        return 2;
                    }

                    var bytes = Convert.FromHexString(hex);
                    await process.StandardInput.WriteAsync(bytes);
                    await process.StandardInput.FlushAsync();
                    return 0;
                }
            case "enter":
                {
                    await process.StandardInput.WriteAsync(new byte[] { 0x0d });
                    await process.StandardInput.FlushAsync();
                    return 0;
                }
            case "esc":
                {
                    await process.StandardInput.WriteAsync(new byte[] { 0x1b });
                    await process.StandardInput.FlushAsync();
                    return 0;
                }
            case "checkpoint":
                {
                    byte[] snapshot;
                    lock (bufferLock)
                        snapshot = buffer.ToArray();
                    var checkpoint = string.IsNullOrEmpty(payload) ? name : payload;
                    var binPath = Path.Combine(outDir, checkpoint + ".bin");
                    File.WriteAllBytes(binPath, snapshot);
                    Console.WriteLine("Wrote " + binPath + " (" + snapshot.Length + " bytes)");
                    return 0;
                }
            default:
                Console.Error.WriteLine("Unknown step: " + kind);
                return 2;
        }
    }

    private sealed class QueryReplies
    {
        public bool Da;
        public bool Cpr;
        public bool Fg;
        public bool Bg;
    }

    private static async Task PumpOutputAsync(
        PtyHostProcess process,
        MemoryStream buffer,
        object bufferLock,
        QueryReplies replies,
        CancellationToken ct)
    {
        var buf = new byte[4096];
        var pending = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await process.StandardOutput.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0)
                    break;
                lock (bufferLock)
                    buffer.Write(buf, 0, n);
                pending.Write(buf, 0, n);
                await ReplyTerminalQueriesAsync(process, pending, replies);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
    }

    /// <summary>
    /// Answer DA / CPR / OSC color queries so the TUI can enter alt-screen.
    /// </summary>
    private static async Task ReplyTerminalQueriesAsync(
        PtyHostProcess process,
        MemoryStream pending,
        QueryReplies state)
    {
        var text = Encoding.UTF8.GetString(pending.ToArray());
        var replies = new List<byte[]>();
        if (!state.Da && (text.Contains("\u001b[c", StringComparison.Ordinal)
            || text.Contains("\u001b[0c", StringComparison.Ordinal)))
        {
            state.Da = true;
            replies.Add(Encoding.ASCII.GetBytes("\u001b[?64;1;2;6;9;15;18;21;22c"));
        }

        if (!state.Cpr && (text.Contains("\u001b[6n", StringComparison.Ordinal)
            || text.Contains("\u001b[0n", StringComparison.Ordinal)))
        {
            state.Cpr = true;
            replies.Add(Encoding.ASCII.GetBytes("\u001b[1;1R"));
        }

        if (!state.Fg && text.Contains("\u001b]10;?", StringComparison.Ordinal))
        {
            state.Fg = true;
            replies.Add(Encoding.ASCII.GetBytes("\u001b]10;rgb:cccc/cccc/cccc\u0007"));
        }

        if (!state.Bg && text.Contains("\u001b]11;?", StringComparison.Ordinal))
        {
            state.Bg = true;
            replies.Add(Encoding.ASCII.GetBytes("\u001b]11;rgb:1e1e/1e1e/1e1e\u0007"));
        }

        if (replies.Count == 0)
            return;

        pending.SetLength(0);
        foreach (var reply in replies)
            await process.StandardInput.WriteAsync(reply);
        await process.StandardInput.FlushAsync();
    }

    private static async Task<bool> WaitContainsAsync(
        MemoryStream buffer,
        object bufferLock,
        string needle,
        int timeoutMs)
        => await WaitAnyAsync(buffer, bufferLock, [needle], timeoutMs);

    private static async Task<bool> WaitAnyAsync(
        MemoryStream buffer,
        object bufferLock,
        IReadOnlyList<string> needles,
        int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            string text;
            lock (bufferLock)
                text = Encoding.UTF8.GetString(buffer.ToArray());
            foreach (var needle in needles)
            {
                if (needle.Length > 0 && text.Contains(needle, StringComparison.Ordinal))
                    return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private static void DumpPreview(MemoryStream buffer, object bufferLock)
    {
        string text;
        lock (bufferLock)
            text = Encoding.UTF8.GetString(buffer.ToArray());
        var sanitized = RecordedFeedSanitizer.Sanitize(text);
        var preview = sanitized.Length > 4000 ? sanitized[^4000..] : sanitized;
        Console.Error.WriteLine("PTY preview (sanitized tail):");
        Console.Error.WriteLine(preview);
    }

    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            i++;
            sb.Append(value[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'e' => '\u001b',
                '\\' => '\\',
                _ => value[i],
            });
        }

        return sb.ToString();
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new InvalidOperationException(flag + " requires a value");
        i++;
        return args[i];
    }
}
