using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Services;

public sealed record McpBrowserOAuthOptions(
    bool NoBrowser = false,
    TimeSpan? CallbackTimeout = null,
    bool Interactive = true,
    McpTlsConfig? Tls = null);
