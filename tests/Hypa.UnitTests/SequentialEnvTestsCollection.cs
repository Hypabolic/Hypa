using Xunit;

namespace Hypa.UnitTests;

/// <summary>
/// xUnit collection that forces sequential execution for tests that mutate process-wide
/// environment variables (CODEX_HOME, HYPA_STORAGE_PATH, etc.).
/// All classes decorated with [Collection("SequentialEnvTests")] run in one thread,
/// preventing env-var races when the full test suite runs in parallel.
/// </summary>
[CollectionDefinition("SequentialEnvTests")]
public sealed class SequentialEnvTestsCollection;
