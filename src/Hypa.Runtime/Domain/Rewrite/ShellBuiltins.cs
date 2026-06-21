namespace Hypa.Runtime.Domain.Rewrite;

/// Shell builtins that mutate shell state or only exist inside a shell.
/// Wrapping these in a separate `hypa -c` process loses the state they set
/// (cwd, env, …) and may fail outright, so they must never be rewritten.
public static class ShellBuiltins
{
    public static readonly IReadOnlySet<string> Stateful = new HashSet<string>(StringComparer.Ordinal)
    {
        "cd", "export", "unset", "set", "source", ".", "alias", "unalias",
        "eval", "exec", "pushd", "popd", "dirs", "local", "declare", "typeset",
        "readonly", "shift", "trap", "umask", "wait", "jobs", "fg", "bg",
        "disown", "hash", "ulimit", "let",
    };

    public static bool IsStateful(string verb) => Stateful.Contains(verb);
}
