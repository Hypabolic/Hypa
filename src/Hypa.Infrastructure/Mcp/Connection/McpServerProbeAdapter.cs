using System.Net;
using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Hypa.Infrastructure.Mcp.Connection;

internal sealed class McpServerProbeAdapter : IMcpServerProbe
{
    private readonly McpTransportBuilder _transportBuilder;
    private readonly IMcpSdkBridge _sdk;
    private readonly McpConfigValidationService _validator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpServerProbeAdapter> _logger;

    public McpServerProbeAdapter(
        McpTransportBuilder transportBuilder,
        IMcpSdkBridge sdk,
        McpConfigValidationService validator,
        ILoggerFactory loggerFactory,
        ILogger<McpServerProbeAdapter> logger)
    {
        _transportBuilder = transportBuilder;
        _sdk = sdk;
        _validator = validator;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    // Overrides WWW-Authenticate bearer challenge detection for unit tests; null = use captured value.
    internal bool? BearerChallengeOverride { get; set; }

    public async Task<McpServerProbeResult> ProbeAsync(McpServerDefinition server, CancellationToken ct)
    {
        // Pre-validate defensively; when called via McpServerConfigService this branch is
        // unreachable (service validates before calling ProbeAsync), but guards direct consumers.
        var validation = _validator.Validate([server]);
        if (!validation.IsOk)
            return new McpServerProbeResult(
                McpServerProbeStatus.InvalidConfig,
                "Invalid server config: " +
                string.Join("; ", validation.Error.Select(e => $"{e.Field}: {e.Message}")));

        using var timeoutCts = server.ConnectTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts is not null)
            timeoutCts.CancelAfter(server.ConnectTimeout!.Value);
        var probeCt = timeoutCts?.Token ?? ct;

        WwwAuthenticateCapture? wwwCapture = null;
        IProbeClientFacade? probeClient = null;
        try
        {
            var (transport, capture) = await _transportBuilder.BuildForProbeAsync(server, probeCt);
            wwwCapture = capture;
            // The probe intentionally triggers a 401 on OAuth-protected servers; the SDK logs
            // that as Error before throwing. Raise the minimum level for SDK categories to Critical
            // so expected 401 noise is suppressed while truly catastrophic SDK failures remain
            // visible. All exceptions are caught below, so no diagnostic information is lost.
            probeClient = await _sdk.CreateProbeClientAsync(
                transport, new McpClientOptions(),
                new CategoryMinLevelLoggerFactory(_loggerFactory, "ModelContextProtocol", LogLevel.Critical),
                probeCt);
            _ = await probeClient.ListToolsAsync(probeCt);
            return new McpServerProbeResult(
                McpServerProbeStatus.Reachable,
                "Reachable: tools/list succeeded.");
        }
        catch (McpCredentialResolutionException ex)
        {
            return new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                "Credential resolution failed: " + ex.Message,
                BuildGuidance(server));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new McpServerProbeResult(
                McpServerProbeStatus.Timeout,
                $"Connection to '{server.Name}' timed out.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized
                                              && server.Auth is NoneAuthConfig)
        {
            // Bearer challenge on a NoneAuth server → mcpOAuth guidance; SDK handles discovery.
            // BearerChallengeOverride is a test seam only.
            var hasBearerChallenge = BearerChallengeOverride ?? (wwwCapture?.HasBearerChallenge == true);
            return new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                $"Server returned {(int)ex.StatusCode!.Value} {ex.StatusCode}.",
                hasBearerChallenge ? BuildMcpOAuthGuidance(server) : BuildGuidance(server));
        }
        catch (HttpRequestException ex) when (IsAuthStatus(ex))
        {
            var hasBearerChallenge = BearerChallengeOverride ?? (wwwCapture?.HasBearerChallenge == true);
            return new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                $"Server returned {(int)ex.StatusCode!.Value} {ex.StatusCode}.",
                server.Auth is NoneAuthConfig && hasBearerChallenge
                    ? BuildMcpOAuthGuidance(server)
                    : BuildGuidance(server));
        }
        catch (HttpRequestException ex) when (!ex.StatusCode.HasValue && IsAuthSemantic(ex))
        {
            var hasBearerChallenge = BearerChallengeOverride ?? (wwwCapture?.HasBearerChallenge == true);
            return new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                ExtractSafeMessage(ex),
                server.Auth is NoneAuthConfig && hasBearerChallenge
                    ? BuildMcpOAuthGuidance(server)
                    : BuildGuidance(server));
        }
        catch (Exception ex) when (ex is not HttpRequestException && IsAuthSemantic(ex))
        {
            var hasBearerChallenge = BearerChallengeOverride ?? (wwwCapture?.HasBearerChallenge == true);
            return new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                ExtractSafeMessage(ex),
                server.Auth is NoneAuthConfig && hasBearerChallenge
                    ? BuildMcpOAuthGuidance(server)
                    : BuildGuidance(server));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Probe HTTP failure for server '{Server}'", server.Name);
            return new McpServerProbeResult(
                McpServerProbeStatus.ConnectionFailed,
                $"Failed to reach '{server.Name}': {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe failure for server '{Server}'", server.Name);
            return new McpServerProbeResult(
                McpServerProbeStatus.Unknown,
                $"Probe of '{server.Name}' failed: {ex.GetType().Name}");
        }
        finally
        {
            if (probeClient is not null)
                await probeClient.DisposeAsync();
        }
    }

    private static McpAuthGuidance BuildMcpOAuthGuidance(McpServerDefinition server) =>
        new(
            SuggestedAuthMode: "mcpOAuth",
            AuthorizationUrl: null,
            TokenUrl: null,
            ClientId: null,
            Scopes: null,
            NextCommands: [$"hypa mcp auth login --server {server.Name}"]);

    private static bool IsAuthStatus(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool IsAuthSemantic(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (ContainsAuthKeyword(current.Message))
                return true;
            current = current.InnerException;
        }
        return false;
    }

    private static bool ContainsAuthKeyword(string message) =>
        message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("forbidden", StringComparison.OrdinalIgnoreCase);

    private static string ExtractSafeMessage(Exception ex) => ex.GetType().Name;

    private static string TransportCliName(McpTransportKind kind) => kind switch
    {
        McpTransportKind.Http => "streamableHttp",
        McpTransportKind.Sse => "sse",
        _ => "http",
    };

    private static string ToEnvVarName(string name) =>
        string.Concat(name.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));

    // SuggestedAuthMode uses lowercase CLI flag aliases (e.g. "bearer", "oauth2DeviceCode"),
    // not McpAuthMode enum member names, so programmatic comparison must use the same aliases.
    private static McpAuthGuidance BuildGuidance(McpServerDefinition server) =>
        server.Auth switch
        {
            McpOAuthConfig => new McpAuthGuidance(
                SuggestedAuthMode: "mcpOAuth",
                AuthorizationUrl: null,
                TokenUrl: null,
                ClientId: null,
                Scopes: null,
                NextCommands: [$"hypa mcp auth login --server {server.Name}"]),

            NoneAuthConfig => new McpAuthGuidance(
                SuggestedAuthMode: null,
                AuthorizationUrl: null,
                TokenUrl: null,
                ClientId: null,
                Scopes: null,
                NextCommands:
                [
                    $"hypa mcp add {server.Name} --transport {TransportCliName(server.Transport.Kind)} --endpoint {server.Transport.Endpoint} --auth bearer --token-ref env:{ToEnvVarName(server.Name)}_TOKEN",
                    $"hypa mcp add {server.Name} --transport {TransportCliName(server.Transport.Kind)} --endpoint {server.Transport.Endpoint} --auth oauth2DeviceCode --auth-url <auth-url> --token-url <token-url> --client-id <client-id> --login",
                ]),

            BearerAuthConfig => new McpAuthGuidance(
                SuggestedAuthMode: "bearer",
                AuthorizationUrl: null,
                TokenUrl: null,
                ClientId: null,
                Scopes: null,
                NextCommands: [$"hypa mcp auth check --server {server.Name}"]),

            OAuth2DeviceCodeConfig dc => new McpAuthGuidance(
                SuggestedAuthMode: "oauth2DeviceCode",
                AuthorizationUrl: dc.AuthUrl,
                TokenUrl: dc.TokenUrl,
                ClientId: dc.ClientId,
                Scopes: dc.Scopes,
                NextCommands: [$"hypa mcp auth login --server {server.Name}"]),

            OAuth2ClientCredentialsConfig cc => new McpAuthGuidance(
                SuggestedAuthMode: "oauth2ClientCredentials",
                AuthorizationUrl: null,
                TokenUrl: cc.TokenUrl,
                ClientId: null,
                Scopes: null,
                NextCommands: [$"hypa mcp auth check --server {server.Name}"]),

            ApiKeyAuthConfig => new McpAuthGuidance(
                SuggestedAuthMode: "apiKey",
                AuthorizationUrl: null,
                TokenUrl: null,
                ClientId: null,
                Scopes: null,
                NextCommands: [$"hypa mcp auth check --server {server.Name}"]),

            BasicAuthConfig => new McpAuthGuidance(
                SuggestedAuthMode: "basic",
                AuthorizationUrl: null,
                TokenUrl: null,
                ClientId: null,
                Scopes: null,
                NextCommands: [$"hypa mcp auth check --server {server.Name}"]),

            MtlsConfig => new McpAuthGuidance(
                SuggestedAuthMode: "mtls",
                AuthorizationUrl: null,
                TokenUrl: null,
                ClientId: null,
                Scopes: null,
                NextCommands: [$"hypa mcp auth check --server {server.Name}"]),

            _ => new McpAuthGuidance(
                SuggestedAuthMode: null,
                AuthorizationUrl: null,
                TokenUrl: null,
                ClientId: null,
                Scopes: null,
                NextCommands: null),
        };
}

/// <summary>
/// Wraps a logger factory and applies a minimum log level to all categories that start
/// with <paramref name="categoryPrefix"/>. Used to suppress expected SDK Error noise
/// (e.g. 401 responses) while keeping Critical failures visible.
/// </summary>
internal sealed class CategoryMinLevelLoggerFactory(
    ILoggerFactory inner,
    string categoryPrefix,
    LogLevel minimumLevel) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName)
    {
        var logger = inner.CreateLogger(categoryName);
        return categoryName.StartsWith(categoryPrefix, StringComparison.Ordinal)
            ? new MinLevelLogger(logger, minimumLevel)
            : logger;
    }

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() { } // inner is owned by the DI container
}

internal sealed class MinLevelLogger(ILogger inner, LogLevel minimum) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel >= minimum && inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= minimum)
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
