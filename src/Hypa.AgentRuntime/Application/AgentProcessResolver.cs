using System.Collections.Immutable;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// One process in a foreground job. <see cref="Argv0"/> is null when the
/// host reports only comm and argv.
/// </summary>
public sealed record ForegroundProcessSnapshot
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Argv0 { get; init; }
    public IReadOnlyList<string>? Argv { get; init; }
    public string? Cmdline { get; init; }
}

/// <summary>
/// Foreground process group. The leader is the process whose pid equals
/// <see cref="ProcessGroupId"/>.
/// </summary>
public sealed record ForegroundJobSnapshot
{
    public int ProcessGroupId { get; init; }
    public IReadOnlyList<ForegroundProcessSnapshot> Processes { get; init; } = [];
}

/// <summary>
/// Names the agent in a foreground job.
/// The group leader is first. Other processes follow by score.
/// A path token uses the basename, then a known package path, then the
/// canonical file path. Kinds resolve through <see cref="AgentKindCatalog"/>.
/// Linux and macOS only. Cmd, PowerShell, and the Windows cursor node.exe
/// layout are outside this path.
/// </summary>
public static class AgentProcessResolver
{
    private static readonly ImmutableArray<string> NodeEvalFlags =
        ImmutableArray.Create("-e", "--eval", "-p", "--print");

    private static readonly ImmutableArray<string> PythonEvalFlags =
        ImmutableArray.Create("-c");

    private static readonly ImmutableArray<string> PythonModuleFlags =
        ImmutableArray.Create("-m");

    private static readonly ImmutableArray<string> ShellEvalFlags =
        ImmutableArray.Create("-c");

    private static readonly ImmutableArray<string> PiDistCli =
        ImmutableArray.Create(
            "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");

    private static readonly ImmutableArray<string> PiBundleCli =
        ImmutableArray.Create(
            "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");

    private static readonly ImmutableArray<string> QwenIndex =
        ImmutableArray.Create(
            "node_modules", "@qwen-code", "qwen-code", "dist", "index");

    private static readonly ImmutableArray<string> MastracodeCli =
        ImmutableArray.Create("node_modules", "mastracode", "dist", "cli");

    /// <summary>
    /// Resolve the job to a canonical agent kind. Returns false when no
    /// process names an agent.
    /// </summary>
    public static bool TryIdentifyInJob(
        ForegroundJobSnapshot job,
        out string canonical,
        IAgentPathCanonicalizer? paths = null)
    {
        canonical = "";
        var candidate = SelectCandidate(job, paths);
        return candidate is not null && AgentKindCatalog.TryResolve(candidate, out canonical);
    }

    private static string? SelectCandidate(ForegroundJobSnapshot job, IAgentPathCanonicalizer? paths)
    {
        var processes = job.Processes;
        if (processes.Count == 0)
            return null;

        foreach (var process in processes)
        {
            if (process.Pid != job.ProcessGroupId)
                continue;
            var leader = NormalizedProcessName(process, paths);
            if (AgentKindCatalog.TryResolve(leader, out _))
                return leader;
            break;
        }

        string? best = null;
        var bestScore = 0;
        foreach (var process in processes)
        {
            var candidate = NormalizedProcessName(process, paths);
            if (!AgentKindCatalog.TryResolve(candidate, out _))
                continue;
            var score = ProcessPriority(process, candidate);
            if (best is not null && bestScore >= score)
                continue;
            best = candidate;
            bestScore = score;
        }

        return best;
    }

    private static string NormalizedProcessName(
        ForegroundProcessSnapshot process,
        IAgentPathCanonicalizer? paths)
    {
        var effective = process.Argv0 ?? process.Name;
        // Linux names a script process after the script file (comm), not the
        // interpreter. Read the interpreter from argv so a wrapper script is
        // still found.
        var interpreter = First(process.Argv) ?? effective;
        if (IsGenericRuntimeOrShell(interpreter)
            && WrappedAgentNameFromRuntimeArgv(interpreter, process.Argv, paths) is { } wrapped)
        {
            return wrapped;
        }

        if (AgentKindCatalog.TryResolve(effective, out _))
            return effective;

        var runtime = First(process.Argv);
        if (runtime is not null)
        {
            var runtimeName = AgentKindCatalog.NormalizeLookupName(PathBasename(runtime));
            if (runtimeName is "node" or "bun"
                && WrappedAgentNameFromRuntimeArgv(runtime, process.Argv, paths) is { } fromRuntime
                && AgentKindCatalog.TryResolve(fromRuntime, out var kind)
                && kind == "qwen")
            {
                return fromRuntime;
            }
        }

        if (Argv0AgentName(process.Argv, paths) is { } fromArgv)
            return fromArgv;
        if (CmdlineArgv0AgentName(process.Cmdline, paths) is { } fromCmdline)
            return fromCmdline;
        return effective;
    }

    private static string? WrappedAgentNameFromRuntimeArgv(
        string runtime,
        IReadOnlyList<string>? argv,
        IAgentPathCanonicalizer? paths)
    {
        if (argv is null)
            return null;

        var runtimeName = AgentKindCatalog.NormalizeLookupName(PathBasename(runtime));
        if (runtimeName is "node" or "bun")
            return ScriptArgAgentName(argv, NodeEvalFlags, ImmutableArray<string>.Empty, paths);
        if (IsPythonRuntime(runtimeName))
            return ScriptArgAgentName(argv, PythonEvalFlags, PythonModuleFlags, paths);
        if (runtimeName is "sh" or "bash" or "zsh" or "fish")
            return ScriptArgAgentName(argv, ShellEvalFlags, ImmutableArray<string>.Empty, paths);
        return null;
    }

    private static string? ScriptArgAgentName(
        IReadOnlyList<string> argv,
        ImmutableArray<string> evalFlags,
        ImmutableArray<string> moduleFlags,
        IAgentPathCanonicalizer? paths)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (arg == "--")
                return i + 1 < argv.Count ? AgentNameFromPathToken(argv[i + 1], paths) : null;

            if (FlagMatches(arg, evalFlags) || FlagMatches(arg, moduleFlags))
                return null;

            if (arg.StartsWith('-'))
            {
                if (OptionTakesValue(arg))
                    i++;
                continue;
            }

            return AgentNameFromPathToken(arg, paths);
        }

        return null;
    }

    private static bool FlagMatches(string arg, ImmutableArray<string> flags)
    {
        foreach (var flag in flags)
        {
            if (arg == flag || ShortFlagPayload(arg, flag) || LongFlagValue(arg, flag))
                return true;
        }

        return false;
    }

    private static bool ShortFlagPayload(string arg, string flag) =>
        flag.StartsWith('-')
        && !flag.StartsWith("--", StringComparison.Ordinal)
        && arg.StartsWith(flag, StringComparison.Ordinal)
        && arg.Length > flag.Length;

    private static bool LongFlagValue(string arg, string flag) =>
        flag.StartsWith("--", StringComparison.Ordinal)
        && arg.StartsWith(flag, StringComparison.Ordinal)
        && arg.Length > flag.Length
        && arg[flag.Length] == '=';

    private static bool OptionTakesValue(string arg) =>
        arg is "-r" or "--require" or "--loader" or "--import"
            or "--experimental-loader" or "--inspect-port"
            or "-W" or "-X" or "-S" or "-L" or "-o";

    private static string? Argv0AgentName(IReadOnlyList<string>? argv, IAgentPathCanonicalizer? paths)
    {
        var token = First(argv);
        return token is null ? null : AgentNameFromPathToken(token, paths);
    }

    private static string? CmdlineArgv0AgentName(string? cmdline, IAgentPathCanonicalizer? paths)
    {
        var token = FirstToken(cmdline);
        return token is null ? null : AgentNameFromPathToken(token, paths);
    }

    private static string? FirstToken(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;
        if (start >= text.Length)
            return null;
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        return text[start..end];
    }

    private static string? AgentNameFromPathToken(string token, IAgentPathCanonicalizer? paths)
    {
        var trimmed = TrimQuotes(token.Trim());
        if (trimmed.Length == 0 || trimmed[0] == '-')
            return null;

        return AgentNameFromBasename(PathBasename(trimmed))
            ?? AgentNameFromKnownPackagePath(trimmed)
            ?? ResolvedAgentNameFromPathToken(trimmed, paths);
    }

    private static string? AgentNameFromKnownPackagePath(string path)
    {
        var raw = SplitComponents(path);
        if (EndsWith(raw, PiDistCli) || EndsWith(raw, PiBundleCli))
            return "pi";

        var normalized = new string[raw.Count];
        for (var i = 0; i < raw.Count; i++)
            normalized[i] = AgentKindCatalog.NormalizeLookupName(raw[i]);

        if (ContainsWindow(normalized, QwenIndex))
            return "qwen";
        if (ContainsWindow(normalized, MastracodeCli))
            return "mastracode";
        return null;
    }

    private static string? ResolvedAgentNameFromPathToken(
        string token,
        IAgentPathCanonicalizer? paths)
    {
        if (token.IndexOfAny(['/', '\\']) < 0)
            return null;
        var resolved = paths?.TryCanonicalize(token);
        if (resolved is null)
            return null;
        return AgentNameFromBasename(PathBasename(resolved))
            ?? AgentNameFromKnownPackagePath(resolved);
    }

    private static string? AgentNameFromBasename(string basename) =>
        AgentKindCatalog.TryResolve(basename, out var canonical) ? canonical : null;

    private static int ProcessPriority(ForegroundProcessSnapshot process, string normalizedName)
    {
        if (!string.Equals(normalizedName, process.Name, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (!IsGenericRuntimeOrShell(normalizedName))
            return 2;
        return 1;
    }

    private static bool IsGenericRuntimeOrShell(string name)
    {
        var normalized = AgentKindCatalog.NormalizeLookupName(PathBasename(name));
        return IsPythonRuntime(normalized)
            || normalized is "sh" or "bash" or "zsh" or "fish" or "tmux" or "node" or "bun";
    }

    private static bool IsPythonRuntime(string name)
    {
        if (name == "python")
            return true;
        const string prefix = "python";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var version = name[prefix.Length..];
        if (version.Length == 0)
            return false;

        var partStart = 0;
        for (var i = 0; i <= version.Length; i++)
        {
            if (i != version.Length && version[i] != '.')
                continue;
            if (i == partStart)
                return false;
            for (var j = partStart; j < i; j++)
            {
                if (!char.IsAsciiDigit(version[j]))
                    return false;
            }

            partStart = i + 1;
        }

        return true;
    }

    private static string? First(IReadOnlyList<string>? argv) =>
        argv is { Count: > 0 } ? argv[0] : null;

    private static string PathBasename(string path)
    {
        var end = path.Length;
        while (end > 0 && path[end - 1] is '/' or '\\')
            end--;
        if (end == 0)
            return path.Length == 0 ? "" : path;

        var start = end - 1;
        while (start >= 0 && path[start] is not '/' and not '\\')
            start--;
        var name = path[(start + 1)..end];
        return name.Length == 0 ? path : name;
    }

    private static List<string> SplitComponents(string path)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i != path.Length && path[i] is not '/' and not '\\')
                continue;
            if (i > start)
                parts.Add(path[start..i]);
            start = i + 1;
        }

        return parts;
    }

    private static bool EndsWith(List<string> components, ImmutableArray<string> suffix)
    {
        if (components.Count < suffix.Length)
            return false;
        var offset = components.Count - suffix.Length;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (!string.Equals(components[offset + i], suffix[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool ContainsWindow(string[] components, ImmutableArray<string> window)
    {
        if (components.Length < window.Length)
            return false;
        var last = components.Length - window.Length;
        for (var start = 0; start <= last; start++)
        {
            var match = true;
            for (var i = 0; i < window.Length; i++)
            {
                if (!string.Equals(components[start + i], window[i], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static string TrimQuotes(string token)
    {
        var start = 0;
        var end = token.Length;
        while (start < end && token[start] is '"' or '\'')
            start++;
        while (end > start && token[end - 1] is '"' or '\'')
            end--;
        return start == 0 && end == token.Length ? token : token[start..end];
    }
}
