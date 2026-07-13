using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Runtime.Application.Services;

public sealed class PackageManagerScriptResolver(
    IFileSystem fileSystem,
    IShellLexer shellLexer) : IPackageManagerScriptResolver
{
    private static readonly HashSet<string> PackageManagers =
        new(StringComparer.OrdinalIgnoreCase) { "npm", "pnpm", "yarn" };

    private static readonly HashSet<string> SafeLeadingOptions =
        new(StringComparer.OrdinalIgnoreCase) { "--silent", "-s" };

    private static readonly Dictionary<string, string> NpmLifecycleScripts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["test"] = "test",
            ["t"] = "test",
            ["tst"] = "test",
            ["start"] = "start",
            ["stop"] = "stop",
            ["restart"] = "restart",
        };

    private static readonly HashSet<string> PnpmBuiltInCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "add", "approve-builds", "audit", "bin", "bugs", "c", "cache", "catalog", "cat-file",
            "cat-index", "ci", "clean", "completion", "config", "create", "dedupe", "deploy",
            "deprecate", "dist-tag", "dlx", "docs", "doctor", "env", "exec", "x", "fetch",
            "find-hash", "help", "ignored-builds", "import", "init", "install", "i",
            "install-test", "it", "licenses", "link", "list", "ls", "login", "logout", "outdated",
            "owner", "pack", "pack-app", "patch", "patch-commit", "patch-remove", "peers", "ping",
            "pkg", "pm", "pnx", "prefix", "prune", "publish", "rebuild", "recursive", "remove",
            "rm", "repo", "root", "run", "run-script", "runtime", "sbom", "search", "self-update",
            "server", "set-script", "setup", "stage", "star", "store", "unlink", "uninstall",
            "un", "unpublish", "update", "up", "upgrade", "version", "view", "whoami", "why",
            "with", "workspace", "workspaces",
        };

    private static readonly HashSet<string> YarnBuiltInCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "add", "audit", "autoclean", "bin", "cache", "check", "config", "constraints", "create",
            "dedupe", "dlx", "exec", "x", "explain", "generate-lock-entry", "global", "help", "import",
            "info", "init", "install", "i", "licenses", "link", "list", "lockfile", "ls", "login",
            "logout", "node", "npm", "outdated", "owner", "pack", "patch", "patch-commit", "plugin", "policies",
            "prune", "publish", "rebuild", "remove", "rm", "run-script", "search", "self-update",
            "set", "stage", "tag", "team", "unlink", "uninstall", "un", "unplug", "up", "update",
            "upgrade", "upgrade-interactive", "version", "versions", "why", "workspace", "workspaces",
        };

    public ResolvedPackageScript? TryResolve(CommandInvocation invocation)
    {
        try
        {
            var packageManager = NormalizeExecutable(invocation.Executable);
            if (!PackageManagers.Contains(packageManager))
                return null;

            if (string.Equals(packageManager, "npm", StringComparison.OrdinalIgnoreCase) &&
                HasNpmTargetChangingOption(invocation.Arguments))
            {
                return null;
            }

            var scriptName = ExtractScriptName(packageManager, invocation.Arguments);
            if (scriptName is null)
                return null;

            var workingDirectory = invocation.WorkingDirectory ?? fileSystem.GetCurrentDirectory();
            var packageJsonPath = Path.Combine(workingDirectory, "package.json");
            if (!fileSystem.FileExists(packageJsonPath))
                return null;

            using var document = JsonDocument.Parse(fileSystem.ReadAllText(packageJsonPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object ||
                !scripts.TryGetProperty(scriptName, out var script) ||
                script.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            if (scripts.TryGetProperty($"pre{scriptName}", out _) ||
                scripts.TryGetProperty($"post{scriptName}", out _))
            {
                return null;
            }

            var command = script.GetString();
            if (string.IsNullOrWhiteSpace(command))
                return null;

            var resolvedCommand = ExtractCanonicalCommand(command);
            return resolvedCommand is null
                ? null
                : new ResolvedPackageScript(
                    resolvedCommand.Value.Executable,
                    resolvedCommand.Value.Command);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ExtractScriptName(
        string packageManager,
        IReadOnlyList<string> arguments)
    {
        var commandIndex = 0;
        while (commandIndex < arguments.Count &&
               SafeLeadingOptions.Contains(arguments[commandIndex]))
        {
            commandIndex++;
        }

        if (commandIndex >= arguments.Count)
            return null;

        var command = arguments[commandIndex];
        if (!IsValidScriptName(command))
            return null;

        if (IsExplicitRunCommand(packageManager, command))
        {
            var scriptIndex = commandIndex + 1;
            while (scriptIndex < arguments.Count &&
                   SafeLeadingOptions.Contains(arguments[scriptIndex]))
            {
                scriptIndex++;
            }

            return scriptIndex < arguments.Count && IsValidScriptName(arguments[scriptIndex])
                ? arguments[scriptIndex]
                : null;
        }

        if (string.Equals(packageManager, "npm", StringComparison.OrdinalIgnoreCase))
        {
            return NpmLifecycleScripts.TryGetValue(command, out var lifecycleScript)
                ? lifecycleScript
                : null;
        }

        if (string.Equals(packageManager, "pnpm", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(command, "t", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(command, "tst", StringComparison.OrdinalIgnoreCase)))
        {
            return "test";
        }

        return !IsBuiltInCommand(packageManager, command)
            ? command
            : null;
    }

    private static bool HasNpmTargetChangingOption(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "--", StringComparison.Ordinal))
                return false;

            if (IsNpmTargetChangingOption(argument))
                return true;
        }

        return false;
    }

    private static bool IsNpmTargetChangingOption(string argument) =>
        string.Equals(argument, "--workspace", StringComparison.Ordinal) ||
        argument.StartsWith("--workspace=", StringComparison.Ordinal) ||
        string.Equals(argument, "--workspaces", StringComparison.Ordinal) ||
        argument.StartsWith("--workspaces=", StringComparison.Ordinal) ||
        string.Equals(argument, "--ws", StringComparison.Ordinal) ||
        argument.StartsWith("--ws=", StringComparison.Ordinal) ||
        string.Equals(argument, "-ws", StringComparison.Ordinal) ||
        argument.StartsWith("-ws=", StringComparison.Ordinal) ||
        string.Equals(argument, "-w", StringComparison.Ordinal) ||
        argument.StartsWith("-w=", StringComparison.Ordinal) ||
        string.Equals(argument, "--prefix", StringComparison.Ordinal) ||
        argument.StartsWith("--prefix=", StringComparison.Ordinal) ||
        string.Equals(argument, "-C", StringComparison.Ordinal) ||
        argument.StartsWith("-C=", StringComparison.Ordinal);

    private static bool IsExplicitRunCommand(string packageManager, string command) =>
        string.Equals(command, "run", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "run-script", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(packageManager, "npm", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(command, "rum", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(command, "urn", StringComparison.OrdinalIgnoreCase));

    private static bool IsValidScriptName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] != '-';

    private static bool IsBuiltInCommand(string packageManager, string command) =>
        packageManager switch
        {
            "pnpm" => PnpmBuiltInCommands.Contains(command),
            "yarn" => YarnBuiltInCommands.Contains(command),
            _ => true,
        };

    private (string Executable, string Command)? ExtractCanonicalCommand(string command)
    {
        var tokens = shellLexer.Lex(command);
        var canSkipAssignments = true;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == TokenKind.Whitespace)
                continue;

            if (token.Kind is TokenKind.Operator or
                TokenKind.Pipe or
                TokenKind.Redirect or
                TokenKind.Shellism)
            {
                return null;
            }

            if (token.Kind == TokenKind.Arg)
            {
                if (canSkipAssignments && IsAssignment(token.Value))
                {
                    if (OperatingSystem.IsWindows())
                        return null;

                    while (i + 1 < tokens.Count &&
                           tokens[i + 1].Kind is TokenKind.Arg or TokenKind.QuotedArg)
                    {
                        i++;
                    }

                    continue;
                }

                if (HasAdjacentArgumentSegment(tokens, i))
                    return null;

                return CreateCanonicalCommand(command, token, token.Value);
            }

            if (token.Kind == TokenKind.QuotedArg &&
                HasAdjacentArgumentSegment(tokens, i))
            {
                return null;
            }

            if (token.Kind == TokenKind.QuotedArg)
                return CreateCanonicalCommand(command, token, StripQuotes(token.Value));

            canSkipAssignments = false;
        }

        return null;
    }

    private static bool HasAdjacentArgumentSegment(
        IReadOnlyList<ShellToken> tokens,
        int candidateIndex)
    {
        var candidate = tokens[candidateIndex];
        return candidateIndex > 0 &&
               IsArgumentSegment(tokens[candidateIndex - 1]) &&
               tokens[candidateIndex - 1].Offset +
               tokens[candidateIndex - 1].Value.Length == candidate.Offset ||
               candidateIndex + 1 < tokens.Count &&
               IsArgumentSegment(tokens[candidateIndex + 1]) &&
               candidate.Offset + candidate.Value.Length ==
               tokens[candidateIndex + 1].Offset;
    }

    private static bool IsArgumentSegment(ShellToken token) =>
        token.Kind is TokenKind.Arg or TokenKind.QuotedArg;

    private static (string Executable, string Command)? CreateCanonicalCommand(
        string command,
        ShellToken leadToken,
        string leadExecutable)
    {
        var executable = NormalizeLeadExecutable(leadExecutable);
        if (executable is null)
            return null;

        var suffixOffset = leadToken.Offset + leadToken.Value.Length;
        return (executable, executable + command[suffixOffset..]);
    }

    private static bool IsAssignment(string value)
    {
        if (value.Length < 2 || (value[0] != '_' && !IsAsciiLetter(value[0])))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '=')
                return true;

            if (ch != '_' && !IsAsciiLetter(ch) && !char.IsAsciiDigit(ch))
                return false;
        }

        return false;
    }

    private static bool IsAsciiLetter(char ch) =>
        ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') ||
                                  (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? NormalizeLeadExecutable(string value)
    {
        var executable = NormalizeExecutable(value);
        return executable.Length == 0 ? null : executable;
    }

    private static string NormalizeExecutable(string executable)
    {
        var slashIndex = executable.LastIndexOf('/');
        var backslashIndex = executable.LastIndexOf('\\');
        var fileName = executable[(Math.Max(slashIndex, backslashIndex) + 1)..];
        var normalizedFileName =
            fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName;
        return normalizedFileName.ToLowerInvariant();
    }

}
