namespace Hypa.Runtime.Domain.Rewrite;

public sealed record RewriteContext(
    bool IsHypaDisabled,
    string[] ExcludeCommands,
    bool GenericWrapperEnabled);
