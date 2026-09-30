namespace Hypa.AgentRuntime.Application;

/// <summary>One TOML upsert. Path is dotted. Literal is a TOML value, not a quoted C# string.</summary>
public sealed record AttachConfigAssignment(string Path, string TomlLiteral);
