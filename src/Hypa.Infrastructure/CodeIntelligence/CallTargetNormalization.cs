namespace Hypa.Infrastructure.CodeIntelligence;

internal static class CallTargetNormalization
{
    /// <summary>
    /// Strip a leading <c>this.</c> or <c>base.</c> receiver so same-file leaf binding
    /// is not disabled by member-access dots. Anchored only — identifiers like
    /// <c>thisThing</c> / <c>baseUrl</c> are unchanged.
    /// </summary>
    internal static string StripThisBaseReceiver(string target)
    {
        if (target.StartsWith("global::", StringComparison.Ordinal))
            return target;

        if (target.StartsWith("this.", StringComparison.Ordinal))
            return target["this.".Length..];

        if (target.StartsWith("base.", StringComparison.Ordinal))
            return target["base.".Length..];

        return target;
    }
}