namespace Hypa.Annotate.Application;

/// <summary>
/// Builds the last stdout JSON line for <c>hypa-annotate doctor</c>.
/// </summary>
public static class AnnotateDoctorService
{
    public static PluginDoctorStdout Report(string? stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            return new PluginDoctorStdout
            {
                Status = "fail",
                Value = "state directory missing",
                Detail = AnnotateEnv.StateDir + " is not set",
            };
        }

        var loaded = AnnotationStore.LoadAnnotations(stateDirectory);
        if (!loaded.IsOk)
        {
            return new PluginDoctorStdout
            {
                Status = "fail",
                Value = "store unreadable",
                Detail = loaded.Error,
            };
        }

        var count = loaded.Value.Count;
        return new PluginDoctorStdout
        {
            Status = "ok",
            Value = count + (count == 1 ? " annotation" : " annotations"),
        };
    }
}
