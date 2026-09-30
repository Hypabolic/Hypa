namespace Hypa.AgentRuntime.Application.Plugins;

internal static class OfficialPluginAssets
{
    public static string AnnotateManifest => Read("annotate.hypa-plugin.toml");

    public static string SlotsManifest => Read("slots.hypa-plugin.toml");

    private static string Read(string name)
    {
        var resource = "Hypa.AgentRuntime.Resources.plugins." + name;
        using var stream = typeof(OfficialPluginAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("missing embedded plugin asset " + resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
