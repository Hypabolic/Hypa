using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application;

/// <summary>Floor apply for terminal.default_shell, shell_mode, new_cwd, and scrollback.</summary>
public static class TerminalSpawnPolicy
{
    public const int MaxScrollbackLines = 1_000_000;

    /// <summary>
    /// Internal Ghostty page-list window. The user key stays 10_000_000.
    /// Sized so native filled cost can fall toward 4.5 MiB. The store
    /// keeps the rest of the user history.
    /// </summary>
    public const long NativeWindowBytes = 6_200_000;

    /// <summary>
    /// Retained rows measured at the 10_000_000-byte default for an
    /// 80x24 pane after a 10_000-line fill. T1 records the live number.
    /// </summary>
    public const int DefaultRetainedRows = 10_000;

    /// <summary>
    // One feed
    /// adds at most about 114 rows at 72 columns. The native window
    /// keeps the viewport plus this margin so one feed cannot outrun
    /// one drain.
    /// </summary>
    public const int HistoryDrainMarginRows = 512;

    public const int HistoryBlockRows = 256;

    public static (string File, IReadOnlyList<string> Args) ResolveEmptyCommand(
        AttachTerminalConfig terminal,
        string? shellEnv,
        bool isMacOs,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var exists = fileExists ?? File.Exists;
        var shell = FirstExistingShell(terminal.DefaultShell, shellEnv, exists);
        var login = terminal.ShellMode switch
        {
            TerminalShellMode.Login => true,
            TerminalShellMode.NonLogin => false,
            _ => isMacOs,
        };
        IReadOnlyList<string> args = login ? ["-l"] : [];
        return (shell, args);
    }

    public static string ResolveNewCwd(
        AttachTerminalConfig terminal,
        string? callerCwd,
        string? sourceCwd,
        string processCwd,
        string? home)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!string.IsNullOrWhiteSpace(callerCwd))
            return ExpandHome(callerCwd, home);

        return terminal.NewCwd.Kind switch
        {
            TerminalNewCwdKind.Home => string.IsNullOrWhiteSpace(home) ? processCwd : home,
            TerminalNewCwdKind.Current => processCwd,
            TerminalNewCwdKind.Path => ExpandHome(
                string.IsNullOrWhiteSpace(terminal.NewCwd.Path) ? processCwd : terminal.NewCwd.Path,
                home),
            _ => string.IsNullOrWhiteSpace(sourceCwd)
                ? (string.IsNullOrWhiteSpace(home) ? processCwd : home)
                : sourceCwd,
        };
    }

    public static int ResolveScrollbackLines(long limitBytes, int cols)
    {
        var width = Math.Max(cols, 1);
        if (limitBytes <= 0)
            return 1;
        var lines = limitBytes / width;
        if (lines < 1)
            return 1;
        return (int)Math.Min(lines, MaxScrollbackLines);
    }

    /// <summary>
    /// Ghostty <c>Terminal::new</c> takes a byte budget. Do not divide by columns.
    /// This is the total user history budget.
    /// </summary>
    public static long ResolveScrollbackBytes(long limitBytes)
    {
        if (limitBytes <= 0)
            return 1;
        return limitBytes;
    }

    /// <summary>
    /// Internal native window. Never larger than the user history budget.
    /// Keep the user key unchanged.
    /// </summary>
    public static long ResolveNativeWindowBytes(long limitBytes)
    {
        var limit = ResolveScrollbackBytes(limitBytes);
        return Math.Min(limit, NativeWindowBytes);
    }

    /// <summary>
    /// Store row bound so native window rows plus stored rows stay at
    /// least the pre-change retained row count.
    /// </summary>
    public static int ResolveHistoryStoreRowBound(long limitBytes)
    {
        var total = ResolveScrollbackBytes(limitBytes);
        var native = ResolveNativeWindowBytes(total);
        var nativeShare = total <= 0 ? 0 : (int)Math.Min(
            DefaultRetainedRows,
            DefaultRetainedRows * native / Math.Max(1, AttachAdvancedConfig.DefaultScrollbackLimitBytes));
        var bound = DefaultRetainedRows - nativeShare + HistoryDrainMarginRows;
        if (bound < HistoryDrainMarginRows)
            bound = HistoryDrainMarginRows;
        return Math.Min(Math.Max(bound, 1), MaxScrollbackLines);
    }

    public static string ExpandHome(string path, string? home)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        if (path == "~")
            return string.IsNullOrWhiteSpace(home) ? path : home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(home))
                return path;
            return Path.Combine(home, path[2..]);
        }

        return path;
    }

    /// <summary>
    /// directory; else <c>$HOME</c> when that directory exists; else <c>/</c>.
    /// </summary>
    public static string ResolveRestoreCwd(
        string? savedCwd,
        string? home,
        Func<string, bool>? directoryExists = null)
    {
        var exists = directoryExists ?? Directory.Exists;
        if (!string.IsNullOrWhiteSpace(savedCwd) && exists(savedCwd))
            return savedCwd;
        if (!string.IsNullOrWhiteSpace(home) && exists(home))
            return home;
        return "/";
    }

    private static string FirstExistingShell(string configured, string? shellEnv, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        if (!string.IsNullOrWhiteSpace(shellEnv) && exists(shellEnv))
            return shellEnv;
        if (exists("/bin/bash"))
            return "/bin/bash";
        return "/bin/sh";
    }
}
