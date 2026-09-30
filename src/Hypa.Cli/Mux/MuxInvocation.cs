using System.Diagnostics;

namespace Hypa.Cli.Mux;

public static class MuxInvocation
{
    public const string LeanAttachFileName = "hypa-attach";
    public const string LeanMuxFileName = "hypa-runtime";
    public const string ProductFileName = "hypa";

    public static bool IsMuxServe(string[] args) =>
        args.Length >= 2
        && args[0] == "mux"
        && args[1] == "serve";

    /// <summary>
    /// Bare hypa and <c>hypa attach</c> are the client argv role
    // Compression flags and other
    /// commands stay on the generic host.
    /// </summary>
    public static bool IsAttach(string[] args)
    {
        if (args is null || args.Length == 0)
            return true;
        var head = args[0];
        if (head == "attach")
            return true;
        if (!LooksLikeAttachOption(head))
            return false;
        for (var i = 0; i < args.Length; i++)
        {
            if (!LooksLikeAttachOption(args[i]) && !IsAttachOptionValue(args, i))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Native product <c>hypa</c> must exec the lean sibling. Returns the
    /// process exit code when this host is product hypa (missing sibling
    /// or exec failure writes stderr). Returns null for testhost, dotnet,
    /// and the managed SDK apphost (hypa beside hypa.dll / hypa.deps.json)
    /// so <c>AttachHostEntry</c> can run in-process. Successful
    /// <c>execve</c> does not return.
    /// </summary>
    public static int? ExecLeanAttachIfProductHypa(string[] args) =>
        ExecLeanAttachIfProductHypa(
            args,
            Console.Error,
            Environment.ProcessPath,
            AppContext.BaseDirectory);

    public static bool TryExecLeanAttach(string[] args) =>
        ExecLeanAttachIfProductHypa(args) is 0;

    internal static int? ExecLeanAttachIfProductHypa(
        string[] args,
        TextWriter errors,
        string? processPath,
        string? baseDirectory)
    {
        if (!IsAttach(args))
            return null;
        if (!IsProductHypaProcess(processPath))
            return null;

        var sibling = ResolveLeanAttachPath(processPath, baseDirectory);
        if (sibling is null)
        {
            errors.WriteLine(MissingLeanAttachMessage(processPath, baseDirectory));
            return 1;
        }

        if (OperatingSystem.IsWindows())
            return SpawnLeanAttachSibling(sibling, args, errors);

        if (UnixProcessReplace.TryExec(sibling, args, out var errno))
            return 0;

        errors.WriteLine(UnixProcessReplace.FormatExecFailure(sibling, errno));
        return 1;
    }

    /// <summary>
    /// Native product <c>hypa mux serve</c> must exec the lean mux sibling.
    /// current exe with a server argv role. Hypa keeps that split as a
    /// separate <c>hypa-runtime</c> image and execs it so the pid stays
    /// stable. Returns the process exit code when this host is product hypa
    /// (missing sibling or exec failure writes stderr). Returns null for
    /// testhost, dotnet, and the managed SDK apphost so
    /// <c>AgentRuntimeHostBuilder</c> can run in-process. Successful
    /// <c>execve</c> does not return.
    /// </summary>
    public static int? ExecLeanMuxIfProductHypa(string[] args) =>
        ExecLeanMuxIfProductHypa(
            args,
            Console.Error,
            Environment.ProcessPath,
            AppContext.BaseDirectory);

    internal static int? ExecLeanMuxIfProductHypa(
        string[] args,
        TextWriter errors,
        string? processPath,
        string? baseDirectory)
    {
        if (!IsMuxServe(args))
            return null;
        if (!IsProductHypaProcess(processPath))
            return null;

        var sibling = ResolveLeanMuxPath(processPath, baseDirectory);
        if (sibling is null)
        {
            errors.WriteLine(MissingLeanMuxMessage(processPath, baseDirectory));
            return 1;
        }

        var execArgs = BuildMuxServeExecArgs(args);
        if (OperatingSystem.IsWindows())
            return SpawnLeanMuxSibling(sibling, execArgs, errors);

        if (UnixProcessReplace.TryExec(sibling, execArgs, out var errno))
            return 0;

        errors.WriteLine(UnixProcessReplace.FormatExecFailure(sibling, errno));
        return 1;
    }

    internal static bool IsProductHypaProcess(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
            return false;
        var name = Path.GetFileNameWithoutExtension(processPath);
        if (!name.Equals(ProductFileName, StringComparison.OrdinalIgnoreCase))
            return false;

        // SDK apphost is also named hypa and sits beside hypa.dll.
        // Native AOT publish has no managed sidecar; keep fail-closed there.
        return !LooksLikeManagedSdkApphost(processPath);
    }

    /// <summary>
    /// Managed SDK apphost ships <c>hypa.dll</c> or <c>hypa.deps.json</c>
    /// beside the host. Native product hypa does not.
    /// </summary>
    internal static bool LooksLikeManagedSdkApphost(string processPath)
    {
        var dir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(dir))
            return false;
        var stem = Path.GetFileNameWithoutExtension(processPath);
        return File.Exists(Path.Combine(dir, stem + ".dll"))
            || File.Exists(Path.Combine(dir, stem + ".deps.json"));
    }

    internal static string LeanAttachSiblingName => SiblingFileName(LeanAttachFileName);

    internal static string? ResolveLeanAttachPath() =>
        ResolveLeanAttachPath(Environment.ProcessPath, AppContext.BaseDirectory);

    internal static string? ResolveLeanAttachPath(string? processPath, string? baseDirectory) =>
        ResolveLeanSiblingPath(LeanAttachFileName, processPath, baseDirectory);

    internal static IReadOnlyList<string> LeanAttachSearchDirectories(
        string processPath,
        string? baseDirectory)
    {
        var dirs = new List<string>();
        AddDir(dirs, Path.GetDirectoryName(processPath));

        try
        {
            var link = File.ResolveLinkTarget(processPath, returnFinalTarget: true);
            if (link is not null)
                AddDir(dirs, Path.GetDirectoryName(link.FullName));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        AddDir(dirs, baseDirectory);
        return dirs;
    }

    internal static string MissingLeanAttachMessage(string? processPath, string? baseDirectory)
    {
        var searched = string.IsNullOrEmpty(processPath)
            ? "(no process path)"
            : string.Join(", ", LeanAttachSearchDirectories(processPath, baseDirectory));
        return "error: hypa-attach was not found beside hypa ('"
            + processPath
            + "'). Searched: "
            + searched
            + ". The lean attach sibling is required; attach does not run inside product hypa.";
    }

    private static string SiblingFileName(string fileName) =>
        OperatingSystem.IsWindows() ? fileName + ".exe" : fileName;

    private static string? ResolveLeanSiblingPath(
        string fileName,
        string? processPath,
        string? baseDirectory)
    {
        if (!IsProductHypaProcess(processPath))
            return null;

        var siblingName = SiblingFileName(fileName);
        foreach (var dir in LeanAttachSearchDirectories(processPath!, baseDirectory))
        {
            var sibling = Path.Combine(dir, siblingName);
            if (File.Exists(sibling))
                return sibling;
        }

        return null;
    }

    private static void AddDir(List<string> dirs, string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return;
        var full = Path.GetFullPath(dir);
        foreach (var existing in dirs)
        {
            if (string.Equals(existing, full, StringComparison.Ordinal))
                return;
        }

        dirs.Add(full);
    }

    private static int SpawnLeanAttachSibling(string path, string[] args, TextWriter errors)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var child = Process.Start(psi);
            if (child is null)
            {
                errors.WriteLine("error: failed to start lean attach '" + path + "'");
                return 1;
            }

            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            errors.WriteLine("error: failed to start lean attach '" + path + "': " + ex.Message);
            return 1;
        }
    }

    private static bool LooksLikeAttachOption(string token) =>
        token is "--session" or "--cwd" or "--once" or "--remote" or "--remote-keybindings"
            or "--handoff" or "--remote-destination" or "--connect-placement"
        || token.StartsWith("--session=", StringComparison.Ordinal)
        || token.StartsWith("--cwd=", StringComparison.Ordinal)
        || token.StartsWith("--remote=", StringComparison.Ordinal)
        || token.StartsWith("--remote-keybindings=", StringComparison.Ordinal)
        || token.StartsWith("--connect-placement=", StringComparison.Ordinal);

    private static bool IsAttachOptionValue(string[] args, int index)
    {
        if (index <= 0)
            return false;
        return args[index - 1] is "--session" or "--cwd" or "--remote" or "--remote-keybindings"
            or "--connect-placement";
    }

    public static string[] ForwardServeArgs(string[] args)
    {
        var start = IsMuxServe(args) ? 2 : 0;
        var forwarded = new List<string>();
        for (var i = start; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session" or "--cwd" or "--state-dir" when i + 1 < args.Length:
                    forwarded.Add(args[i]);
                    forwarded.Add(args[++i]);
                    break;
                case "--help" or "-h":
                    forwarded.Add(args[i]);
                    break;
            }
        }

        return [.. forwarded];
    }

    /// <summary>
    /// Exec argv is <c>mux serve</c> plus the forwarded host flags.
    /// <c>MuxProcessIdentity.LooksLikeMux</c> requires those tokens
    /// </summary>
    internal static string[] BuildMuxServeExecArgs(string[] args)
    {
        var forwarded = ForwardServeArgs(args);
        var exec = new string[forwarded.Length + 2];
        exec[0] = "mux";
        exec[1] = "serve";
        if (forwarded.Length > 0)
            Array.Copy(forwarded, 0, exec, 2, forwarded.Length);
        return exec;
    }

    internal static string LeanMuxSiblingName => SiblingFileName(LeanMuxFileName);

    internal static string? ResolveLeanMuxPath() =>
        ResolveLeanMuxPath(Environment.ProcessPath, AppContext.BaseDirectory);

    internal static string? ResolveLeanMuxPath(string? processPath, string? baseDirectory) =>
        ResolveLeanSiblingPath(LeanMuxFileName, processPath, baseDirectory);

    internal static IReadOnlyList<string> LeanMuxSearchDirectories(
        string processPath,
        string? baseDirectory) =>
        LeanAttachSearchDirectories(processPath, baseDirectory);

    internal static string MissingLeanMuxMessage(string? processPath, string? baseDirectory)
    {
        var searched = string.IsNullOrEmpty(processPath)
            ? "(no process path)"
            : string.Join(", ", LeanMuxSearchDirectories(processPath, baseDirectory));
        return "error: hypa-runtime was not found beside hypa ('"
            + processPath
            + "'). Searched: "
            + searched
            + ". The lean mux sibling is required; mux serve does not run inside product hypa.";
    }

    private static int SpawnLeanMuxSibling(string path, string[] args, TextWriter errors)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var child = Process.Start(psi);
            if (child is null)
            {
                errors.WriteLine("error: failed to start lean mux '" + path + "'");
                return 1;
            }

            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            errors.WriteLine("error: failed to start lean mux '" + path + "': " + ex.Message);
            return 1;
        }
    }
}
