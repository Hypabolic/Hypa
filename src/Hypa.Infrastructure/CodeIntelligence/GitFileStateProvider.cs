using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.CodeIntelligence;

public sealed class GitFileStateProvider(ICommandRunner commandRunner) : IGitFileStateProvider
{
    public async Task<IReadOnlyDictionary<string, string>?> GetCleanBlobOidsAsync(
        string projectRoot, CancellationToken ct)
    {
        try
        {
            var trackedOutput = await RunGitAsync(projectRoot, ["ls-files", "-s"], ct);
            if (trackedOutput is null)
                return null;

            var modifiedOutput = await RunGitAsync(projectRoot, ["ls-files", "--modified"], ct);
            if (modifiedOutput is null)
                return null;

            var modifiedPaths = ParsePathLines(modifiedOutput);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (path, oid) in ParseTrackedFiles(trackedOutput))
            {
                if (!modifiedPaths.Contains(path))
                    result[path] = oid;
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetCleanBlobOidAsync(
        string absolutePath, string projectRoot, CancellationToken ct)
    {
        try
        {
            var relativePath = ToGitRelativePath(projectRoot, absolutePath);
            var trackedOutput = await RunGitAsync(projectRoot, ["ls-files", "-s", "--", relativePath], ct);
            if (trackedOutput is null)
                return null;

            var modifiedOutput = await RunGitAsync(projectRoot, ["ls-files", "--modified", "--", relativePath], ct);
            if (modifiedOutput is null)
                return null;

            if (ParsePathLines(modifiedOutput).Contains(relativePath))
                return null;

            var trackedFile = ParseTrackedFiles(trackedOutput).FirstOrDefault();
            return trackedFile.Path is null ? null : trackedFile.Oid;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> RunGitAsync(string projectRoot, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var invocation = CommandInvocation.Buffered("git", arguments, $"git {string.Join(' ', arguments)}") with
        {
            WorkingDirectory = projectRoot,
        };

        var result = await commandRunner.RunAsync(invocation, ct);
        if (!result.IsOk || result.Value.ExitCode != 0)
            return null;

        return result.Value.Stdout;
    }

    private static HashSet<string> ParsePathLines(string output) =>
        output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<(string Path, string Oid)> ParseTrackedFiles(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            var parts = line.Split('\t', 2);
            if (parts.Length != 2)
                throw new FormatException("git ls-files -s output did not contain a tab separator.");

            var metadata = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length < 3)
                throw new FormatException("git ls-files -s output did not contain mode, oid, and stage.");

            yield return (parts[1], metadata[1]);
        }
    }

    private static string ToGitRelativePath(string projectRoot, string absolutePath) =>
        Path.GetRelativePath(projectRoot, absolutePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
}
