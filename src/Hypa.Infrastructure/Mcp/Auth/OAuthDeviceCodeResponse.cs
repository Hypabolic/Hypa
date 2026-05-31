namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed record OAuthDeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int? Interval);
