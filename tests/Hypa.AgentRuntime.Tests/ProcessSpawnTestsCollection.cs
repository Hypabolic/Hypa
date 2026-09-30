using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Required for `dotnet test -m:1` and default parallel. Do not share one
/// testhost thread with unrelated collections if assembly parallel is later
/// turned on. Process-spawn and real PTY classes join this collection.
/// </summary>
[CollectionDefinition("ProcessSpawnTests", DisableParallelization = true)]
public sealed class ProcessSpawnTestsCollection;
