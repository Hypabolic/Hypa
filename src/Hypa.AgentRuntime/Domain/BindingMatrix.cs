namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Binding placement modes from design §7.2.
/// </summary>
public enum BindingMode
{
    /// <summary>Local unmanaged (default F1). All binding fields optional.</summary>
    LocalUnmanaged = 0,

    /// <summary>Local governed. Required agent_session_id, run_id, project_root, tenant_id.</summary>
    LocalGoverned = 1,

    /// <summary>Remote G1. Governed fields + project_root under /workspace.</summary>
    Remote = 2,
}

/// <summary>
/// Pure binding matrix validator (design §7.2). No I/O; opaque string checks only.
/// </summary>
public static class BindingMatrix
{
    public const string PlacementLocal = "local";
    public const string PlacementRemote = "remote";
    public const string WorkspaceRootPrefix = "/workspace";

    /// <summary>
    /// Resolve mode from session placement + governed flag.
    /// </summary>
    public static BindingMode ResolveMode(string? placement, bool governed)
    {
        var p = string.IsNullOrWhiteSpace(placement) ? PlacementLocal : placement.Trim().ToLowerInvariant();
        if (string.Equals(p, PlacementRemote, StringComparison.Ordinal))
            return BindingMode.Remote;
        return governed ? BindingMode.LocalGoverned : BindingMode.LocalUnmanaged;
    }

    /// <summary>
    /// Validate binding against the matrix. Returns null on success, error message on failure.
    /// </summary>
    /// <param name="binding">Binding under validation (may be empty for clear).</param>
    /// <param name="mode">Resolved binding mode.</param>
    /// <param name="paneScoped">True when updating a specific pane (step_id required under governed).</param>
    /// <param name="existingTenantId">Process tenant anchor for equality check; null if none yet.</param>
    /// <param name="existingRunId">Process run anchor (one governed Run); null if none yet.</param>
    public static string? Validate(
        AtomicBinding? binding,
        BindingMode mode,
        bool paneScoped = false,
        string? existingTenantId = null,
        string? existingRunId = null)
    {
        binding ??= new AtomicBinding();

        // memory_id is always optional opaque — never validated beyond string storage.
        return mode switch
        {
            BindingMode.LocalUnmanaged => null,
            BindingMode.LocalGoverned => ValidateGoverned(
                binding, paneScoped, requireWorkspaceRoot: false, existingTenantId, existingRunId),
            BindingMode.Remote => ValidateGoverned(
                binding, paneScoped: true, requireWorkspaceRoot: true, existingTenantId, existingRunId),
            _ => "Unknown binding mode",
        };
    }

    private static string? ValidateGoverned(
        AtomicBinding binding,
        bool paneScoped,
        bool requireWorkspaceRoot,
        string? existingTenantId,
        string? existingRunId)
    {
        if (string.IsNullOrWhiteSpace(binding.AgentSessionId))
            return "governed binding requires agent_session_id";
        if (string.IsNullOrWhiteSpace(binding.RunId))
            return "governed binding requires run_id";
        if (string.IsNullOrWhiteSpace(binding.ProjectRoot))
            return "governed binding requires project_root";
        if (string.IsNullOrWhiteSpace(binding.TenantId))
            return "governed binding requires tenant_id";

        // step_id required for Step-owned pane updates and for all remote bindings.
        if (paneScoped && string.IsNullOrWhiteSpace(binding.StepId))
            return "governed pane binding requires step_id";

        if (requireWorkspaceRoot && !IsUnderWorkspaceRoot(binding.ProjectRoot))
            return "remote binding project_root must be under /workspace";

        // Appendix E.1 / KD-6: one tenant and one governed Run per process.
        if (!string.IsNullOrWhiteSpace(existingTenantId) &&
            !string.Equals(existingTenantId, binding.TenantId, StringComparison.Ordinal))
        {
            return "tenant_id does not match process tenant";
        }

        if (!string.IsNullOrWhiteSpace(existingRunId) &&
            !string.Equals(existingRunId, binding.RunId, StringComparison.Ordinal))
        {
            return "run_id does not match process run";
        }

        return null;
    }

    /// <summary>
    /// Canonical absolute path under <c>/workspace</c>. Rejects escape via <c>..</c>.
    /// </summary>
    public static bool IsUnderWorkspaceRoot(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return false;

        var normalized = projectRoot.Trim().Replace('\\', '/');
        if (!normalized.StartsWith(WorkspaceRootPrefix, StringComparison.Ordinal))
            return false;

        if (normalized.Length > WorkspaceRootPrefix.Length &&
            normalized[WorkspaceRootPrefix.Length] != '/')
            return false;

        // Reject path segments that escape the root.
        var rest = normalized.Length == WorkspaceRootPrefix.Length
            ? string.Empty
            : normalized[(WorkspaceRootPrefix.Length + 1)..];

        if (rest.Length == 0)
            return true;

        var depth = 0;
        foreach (var segment in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "")
                continue;
            if (segment == "..")
            {
                depth--;
                if (depth < 0)
                    return false;
                continue;
            }

            depth++;
        }

        return true;
    }
}
