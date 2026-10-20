using Xunit;

namespace Hypa.Continuity.Tests.Impairment;

/// <summary>
/// The opt-in switch of the remote typing bench. The remote typing matrix,
/// the bandwidth cap cells and the echo probe through the impairment proxy
/// run for many minutes. They run only when
/// <c>HYPA_REMOTE_TYPING_BENCH=1</c>, so a plain <c>dotnet test</c> stays
/// fast. <c>scripts/verify-remote-typing-bench.sh</c> sets the variable.
/// </summary>
internal static class RemoteBenchOptIn
{
    public const string Variable = "HYPA_REMOTE_TYPING_BENCH";

    public static bool IsSet =>
        string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal);

    /// <summary>Skips the test when the bench is not opted in.</summary>
    public static void Require() =>
        Skip.IfNot(IsSet, $"Set {Variable}=1 to run the remote typing bench.");
}
