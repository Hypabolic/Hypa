namespace Hypa.Annotate.Application;

internal static class StorePaths
{
    internal static string AnnotationsFile(string stateDirectory) =>
        Path.Combine(stateDirectory, "annotations.jsonl");

    internal static string ArchivesFile(string stateDirectory) =>
        Path.Combine(stateDirectory, "archives.jsonl");

    internal static string AnnotationsLock(string stateDirectory) =>
        Path.Combine(stateDirectory, ".annotations.lock");

    internal static string ArchivesLock(string stateDirectory) =>
        Path.Combine(stateDirectory, ".archives.lock");
}
