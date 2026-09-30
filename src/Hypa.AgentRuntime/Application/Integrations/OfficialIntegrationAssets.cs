namespace Hypa.AgentRuntime.Application.Integrations;

internal static class OfficialIntegrationAssets
{
    public static string PiExtension => Read("pi.hypa-agent-state.ts");

    public static string OmpExtension => Read("omp.hypa-omp-agent-state.ts");

    public static string ClaudeHook => Read("claude.hypa-agent-state.sh");

    public static string CodexHook => Read("codex.hypa-agent-state.sh");

    public static string CopilotHook => Read("copilot.hypa-agent-state.sh");

    public static string DevinHook => Read("devin.hypa-agent-state.sh");

    public static string DroidHook => Read("droid.hypa-agent-state.sh");

    public static string KimiHook => Read("kimi.hypa-agent-state.sh");

    public static string OpenCodePlugin => Read("opencode.hypa-agent-state.js");

    public static string OpenCodeTuiPlugin => Read("opencode.hypa-tui-session.js");

    public static string KiloPlugin => Read("kilo.hypa-agent-state.js");

    public static string HermesManifest => Read("hermes.plugin.yaml");

    public static string HermesInit => Read("hermes.__init__.py");

    public static string QodercliHook => Read("qodercli.hypa-agent-state.sh");

    public static string QwenHook => Read("qwen.hypa-agent-session.sh");

    public static string CursorHook => Read("cursor.hypa-agent-state.sh");

    public static string MastracodeHook => Read("mastracode.hypa-agent-state.sh");

    public static string AntigravityCliHook => Read("antigravity_cli.hypa-agent-state.sh");

    public static string GrokHook => Read("grok.hypa-agent-state.sh");

    private static string Read(string name)
    {
        var resource = "Hypa.AgentRuntime.Resources.integrations." + name;
        using var stream = typeof(OfficialIntegrationAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("missing embedded integration asset " + resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
