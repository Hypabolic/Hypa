namespace Hypa.Runtime.Domain.Parsers;

public readonly record struct ParseResult<T>(T? Value, ParseTier Tier, bool Matched);
