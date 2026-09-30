namespace Hypa.Cli.Mux;

/// <summary>
/// Operator consent for a destructive remote restart.
/// </summary>
internal static class RemoteRestartConsent
{
    internal static bool TryPrompt(TextReader input, TextWriter error, string detail)
    {
        error.WriteLine(detail);
        error.Write("Continue? [y/N] ");
        error.Flush();
        var line = input.ReadLine();
        return line is "y" or "Y" or "yes" or "Yes";
    }
}
