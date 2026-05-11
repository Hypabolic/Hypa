using System.Security.Cryptography;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Filters;

namespace Hypa.Runtime.Application.Services;

public sealed class TrustService(
    ITrustStore trustStore,
    IProjectRootDetector projectRootDetector,
    IFileSystem fileSystem,
    IClock clock)
{
    public async Task<string> GrantFiltersAsync(CancellationToken ct)
    {
        var projectRoot = projectRootDetector.Detect(fileSystem.GetCurrentDirectory());
        if (projectRoot is null)
            return "No project root detected. Run from inside a git repository.";

        var filterDir = Path.Combine(projectRoot, ".hypa", "filters");
        if (!fileSystem.DirectoryExists(filterDir))
            return $"No filter directory found at {filterDir}";

        var files = fileSystem.GetFiles(filterDir, "*.json");
        if (files.Count == 0)
            return $"No filter files found in {filterDir}";

        var granted = 0;
        foreach (var file in files)
        {
            var hash = ComputeHash(file);
            await trustStore.GrantAsync(new TrustRecord
            {
                ProjectRoot = projectRoot,
                FilterFilePath = file,
                FileHash = hash,
                GrantedAt = clock.UtcNow,
            }, ct);
            granted++;
        }

        return $"Trusted {granted} filter file(s) in {filterDir}";
    }

    public async Task<IReadOnlyList<TrustRecord>> GetStatusAsync(CancellationToken ct) =>
        await trustStore.GetAllAsync(ct);

    private string ComputeHash(string filePath)
    {
        var bytes = fileSystem.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
