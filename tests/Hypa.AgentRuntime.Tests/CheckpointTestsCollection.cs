using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Serializes checkpoint classes. Combined with assembly-level
/// <see cref="CollectionBehaviorAttribute.DisableTestParallelization"/> this
/// keeps SQLite + native walk/git work off the testhost parallel scheduler
/// that aborted aggregate FullyQualifiedName~Checkpoint runs.
/// </summary>
[CollectionDefinition("CheckpointTests")]
public sealed class CheckpointTestsCollection;
