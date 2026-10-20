using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.StandardStreams;
using Hypa.AgentServer;

// A mux that starts from a shell with & writes to the user terminal.
// The .NET console stream would put that terminal in keypad mode.
StandardStreamWriters.Install();

// Opt-in: GC pauses and stalls of this process in the attach path trace.
using var pauses = ProcessPauseSampler.StartIfTracing();

return await AgentRuntimeHostBuilder.Run(args);
