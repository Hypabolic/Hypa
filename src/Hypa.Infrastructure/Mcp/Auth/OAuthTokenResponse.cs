namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed record OAuthTokenResponse(
    string AccessToken,
    string TokenType,
    int? ExpiresIn,
    string? Scope);
