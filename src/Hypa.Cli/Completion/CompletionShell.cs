namespace Hypa.Cli.Completion;

internal static class CompletionShell
{
    internal static readonly string[] Supported = ["bash", "elvish", "fish", "powershell", "zsh"];

    internal static string Usage() => string.Join("|", Supported);

    internal static bool TryParse(string shell, out string normalized)
    {
        foreach (var supported in Supported)
        {
            if (string.Equals(shell, supported, StringComparison.OrdinalIgnoreCase))
            {
                normalized = supported;
                return true;
            }
        }

        normalized = string.Empty;
        return false;
    }
}
