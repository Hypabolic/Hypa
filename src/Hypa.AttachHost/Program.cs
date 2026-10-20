using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.StandardStreams;
using Hypa.Cli.Mux;

// Write stdout and stderr on the raw descriptors before any other output.
// The .NET console stream would put the terminal in keypad mode.
StandardStreamWriters.Install();

// Opt-in: GC pauses and stalls of this process in the attach path trace.
using var pauses = ProcessPauseSampler.StartIfTracing();

// Lean attach argv role. Not a product brand.
return await AttachHostEntry.Run(args);
