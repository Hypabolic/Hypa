namespace Hypa.Infrastructure.Storage;

public sealed record HypaDataOptions
{
    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".hypa");

    public string DatabasePath => Path.Combine(DataDirectory, "hypa.db");
    public string ArtifactsDirectory => Path.Combine(DataDirectory, "artifacts");
}
