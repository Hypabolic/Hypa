namespace Hypa.AgentRuntime.Application.Plugins;

public static class PluginProgramPath
{
    public static string Resolve(string program, string pluginRoot, string? hostBinary)
    {
        if (string.Equals(program, BundledPluginLayout.BinaryPlaceholder, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(hostBinary))
        {
            return hostBinary;
        }

        if (Path.IsPathRooted(program))
            return program;

        if (program.Contains('/', StringComparison.Ordinal))
        {
            var relative = program.StartsWith("./", StringComparison.Ordinal) ? program[2..] : program;
            return Path.Combine(pluginRoot, relative);
        }

        return program;
    }
}
