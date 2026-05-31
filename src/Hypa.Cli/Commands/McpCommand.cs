using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.Cli.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging;

namespace Hypa.Cli.Commands;

public sealed class McpCommand(
    McpProxyService proxyService,
    IMcpServerDefinitionRepository serverDefinitionRepository,
    IMcpAuthProvider authProvider,
    McpServerConfigService mcpServerConfigService,
    ILogger<McpCommand> logger,
    IMcpServerImportService? mcpServerImportService = null,
    IMcpBrowserOAuthFlowProvider? browserOAuthFlowProvider = null)
{
    private static readonly HashSet<string> ValidAgentKeys =
        new(StringComparer.OrdinalIgnoreCase) { "claude", "codex", "all" };

    public Command Build()
    {
        var cmd = new Command("mcp", "Interact with configured upstream MCP servers.");
        cmd.AddCommand(BuildList());
        cmd.AddCommand(BuildAdd());
        cmd.AddCommand(BuildImport());
        cmd.AddCommand(BuildInvoke());
        cmd.AddCommand(BuildBatch());
        cmd.AddCommand(BuildSchema());
        cmd.AddCommand(BuildSearch());
        cmd.AddCommand(BuildTools());
        cmd.AddCommand(BuildAuth());
        return cmd;
    }

    private Command BuildImport()
    {
        var agentOpt = new Option<string>("--agent", () => "all", "Agent to import from: claude | codex | all.");
        var scopeOpt = new Option<string>("--scope", () => "global", "Scope: global | project | all.");
        var projectRootOpt = new Option<string?>("--project-root", "Project root path for project or all scope.");
        var dryRunOpt = new Option<bool>("--dry-run", "Report candidates without writing.");
        var replaceOpt = new Option<bool>("--replace", "Replace existing entries with the same name.");

        var cmd = new Command("import", "Import MCP servers from agent harness configuration files.");
        cmd.AddOption(agentOpt);
        cmd.AddOption(scopeOpt);
        cmd.AddOption(projectRootOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(replaceOpt);

        cmd.SetHandler(async context =>
        {
            var pr = context.ParseResult;
            var ct = context.GetCancellationToken();

            var agentArg = pr.GetValueForOption(agentOpt)!;
            var scopeArg = pr.GetValueForOption(scopeOpt)!;
            var projectRoot = pr.GetValueForOption(projectRootOpt);
            var dryRun = pr.GetValueForOption(dryRunOpt);
            var replace = pr.GetValueForOption(replaceOpt);

            if (!ValidAgentKeys.Contains(agentArg))
            {
                await Console.Error.WriteLineAsync(
                    $"error: UnknownAgent: '{agentArg}' is not a known agent. Use claude, codex, or all.");
                context.ExitCode = 1;
                return;
            }

            var scope = scopeArg.ToLowerInvariant() switch
            {
                "global" => McpImportScope.Global,
                "project" => McpImportScope.Project,
                "all" => McpImportScope.All,
                _ => (McpImportScope?)null,
            };

            if (scope is null)
            {
                await Console.Error.WriteLineAsync(
                    $"error: InvalidOption: '{scopeArg}' is not a valid scope. Use global, project, or all.");
                context.ExitCode = 1;
                return;
            }

            if ((scope == McpImportScope.Project || scope == McpImportScope.All) &&
                string.IsNullOrWhiteSpace(projectRoot))
            {
                await Console.Error.WriteLineAsync(
                    "error: MissingOption: --project-root is required when --scope is project or all.");
                context.ExitCode = 1;
                return;
            }

            if (dryRun)
                Console.WriteLine("Dry run — no servers will be written.");

            var svc = mcpServerImportService;
            if (svc is null)
            {
                await Console.Error.WriteLineAsync("error: Import service not available.");
                context.ExitCode = 1;
                return;
            }

            McpImportReport report;
            try
            {
                var agentKey = string.Equals(agentArg, "all", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : agentArg;
                var importResult = await svc.ImportAsync(
                    new McpImportRequest(agentKey, scope.Value, projectRoot, replace, dryRun), ct);
                if (!importResult.IsOk)
                {
                    await Console.Error.WriteLineAsync($"error: ImportFailed: {importResult.Error.Message}");
                    context.ExitCode = 1;
                    return;
                }
                report = importResult.Value;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"error: ImportFailed: {ex.Message}");
                context.ExitCode = 1;
                return;
            }

            PrintImportReport(report);
        });

        return cmd;
    }

    private static void PrintImportReport(McpImportReport report)
    {
        foreach (var source in report.Sources)
        {
            Console.WriteLine($"[{source.Agent}/{source.Scope}]");
            foreach (var conn in source.Connections)
            {
                var symbol = conn.Status switch
                {
                    McpImportCandidateStatus.Importable => "+",
                    McpImportCandidateStatus.SkippedSelf => "-",
                    McpImportCandidateStatus.SkippedUnsafeSecret => "!",
                    McpImportCandidateStatus.SkippedIncomplete => "!",
                    McpImportCandidateStatus.SkippedUnsupported => "=",
                    McpImportCandidateStatus.SkippedDuplicate => "=",
                    McpImportCandidateStatus.SkippedConflict => "~",
                    McpImportCandidateStatus.ParseError => "!",
                    _ => "?",
                };

                var label = conn.Status == McpImportCandidateStatus.Importable
                    ? $"imported {conn.SourceName}"
                    : $"skipped {conn.SourceName}";

                var detail = conn.Detail is not null ? $" ({conn.Detail})" : string.Empty;
                Console.WriteLine($"  {symbol} {label}{detail}");
            }
        }
    }

    private Command BuildAdd()
    {
        var nameArg = new Argument<string?>("name", () => null, "Upstream server name.");
        var transportOpt = new Option<string?>("--transport", "Transport: stdio | streamableHttp | sse | httpAutoDetect.");
        var endpointOpt = new Option<string?>("--endpoint", "Command (stdio) or URL (remote transports).");
        var authOpt = new Option<string?>("--auth", "Auth mode: none | bearer | apiKey | basic | oauth2ClientCredentials | oauth2DeviceCode | mtls.");
        var tokenRefOpt = new Option<string?>("--token-ref", "Secret reference for bearer token.");
        var headerNameOpt = new Option<string?>("--header-name", "Header name for apiKey auth.");
        var valueRefOpt = new Option<string?>("--value-ref", "Secret reference for apiKey value.");
        var inQueryStringOpt = new Option<bool>("--in-query-string", "Send apiKey in query string instead of header.");
        var usernameRefOpt = new Option<string?>("--username-ref", "Secret reference for basic auth username.");
        var passwordRefOpt = new Option<string?>("--password-ref", "Secret reference for basic auth password.");
        var tokenUrlOpt = new Option<string?>("--token-url", "Token URL for OAuth2 flows.");
        var clientIdRefOpt = new Option<string?>("--client-id-ref", "Secret reference for OAuth2 client ID.");
        var clientSecretRefOpt = new Option<string?>("--client-secret-ref", "Secret reference for OAuth2 client secret.");
        var scopesOpt = new Option<string?>("--scopes", "Comma-separated OAuth2 scopes.");
        var authUrlOpt = new Option<string?>("--auth-url", "Authorization URL for OAuth2 device-code flow.");
        var clientIdOpt = new Option<string?>("--client-id", "Client ID for OAuth2 device-code flow.");
        var clientCertRefOpt = new Option<string?>("--client-cert-ref", "Secret reference for mTLS client certificate.");
        var clientKeyRefOpt = new Option<string?>("--client-key-ref", "Secret reference for mTLS client key.");
        var caCertPathOpt = new Option<string?>("--ca-cert-path", "Path to CA certificate for TLS.");
        var clientCertPathOpt = new Option<string?>("--client-cert-path", "Path to TLS client certificate.");
        var clientKeyPathOpt = new Option<string?>("--client-key-path", "Path to TLS client key.");
        var connectTimeoutOpt = new Option<int?>("--connect-timeout-seconds", "Connection timeout in seconds.");
        var requestTimeoutOpt = new Option<int?>("--request-timeout-seconds", "Request timeout in seconds.");
        var replaceOpt = new Option<bool>("--replace", "Replace an existing server with the same name.");
        var dryRunOpt = new Option<bool>("--dry-run", "Validate and print generated config without writing.");
        var loginOpt = new Option<bool>("--login", "After adding, start OAuth2 device-code login.");
        var interactiveOpt = new Option<bool>("--interactive", "Prompt for all values interactively.");
        var noProbeOpt = new Option<bool>("--no-probe",
            "Skip the default remote-server reachability check before writing config.");
        var noBrowserOpt = new Option<bool>("--no-browser",
            "For MCP OAuth: display auth URL instead of opening browser.");
        var nonInteractiveOpt = new Option<bool>("--non-interactive",
            "Fail with exit code 4 if interactive OAuth or prompts are required.");
        var jsonOpt = new Option<bool>("--json", "Output result as JSON.");

        var cmd = new Command("add", "Add a new upstream MCP server to configuration.");
        cmd.AddArgument(nameArg);
        cmd.AddOption(transportOpt);
        cmd.AddOption(endpointOpt);
        cmd.AddOption(authOpt);
        cmd.AddOption(tokenRefOpt);
        cmd.AddOption(headerNameOpt);
        cmd.AddOption(valueRefOpt);
        cmd.AddOption(inQueryStringOpt);
        cmd.AddOption(usernameRefOpt);
        cmd.AddOption(passwordRefOpt);
        cmd.AddOption(tokenUrlOpt);
        cmd.AddOption(clientIdRefOpt);
        cmd.AddOption(clientSecretRefOpt);
        cmd.AddOption(scopesOpt);
        cmd.AddOption(authUrlOpt);
        cmd.AddOption(clientIdOpt);
        cmd.AddOption(clientCertRefOpt);
        cmd.AddOption(clientKeyRefOpt);
        cmd.AddOption(caCertPathOpt);
        cmd.AddOption(clientCertPathOpt);
        cmd.AddOption(clientKeyPathOpt);
        cmd.AddOption(connectTimeoutOpt);
        cmd.AddOption(requestTimeoutOpt);
        cmd.AddOption(replaceOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(loginOpt);
        cmd.AddOption(interactiveOpt);
        cmd.AddOption(noProbeOpt);
        cmd.AddOption(noBrowserOpt);
        cmd.AddOption(nonInteractiveOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async context =>
        {
            var pr = context.ParseResult;
            var ct = context.GetCancellationToken();

            var dryRun = pr.GetValueForOption(dryRunOpt);
            var login = pr.GetValueForOption(loginOpt);
            var interactive = pr.GetValueForOption(interactiveOpt);
            var noProbe = pr.GetValueForOption(noProbeOpt);
            var noBrowser = pr.GetValueForOption(noBrowserOpt);
            var nonInteractive = pr.GetValueForOption(nonInteractiveOpt);
            var json = pr.GetValueForOption(jsonOpt);

            if (dryRun && login)
            {
                await Console.Error.WriteLineAsync("error: InvalidOption: --dry-run cannot be combined with --login.");
                context.ExitCode = 1;
                return;
            }

            if (interactive && nonInteractive)
            {
                await Console.Error.WriteLineAsync("error: InvalidOption: --interactive cannot be combined with --non-interactive.");
                context.ExitCode = 1;
                return;
            }

            var authType = pr.GetValueForOption(authOpt);
            if (login && !string.IsNullOrWhiteSpace(authType) &&
                !string.Equals(authType, "oauth2DeviceCode", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(authType, "oauth2devicecode", StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync("error: InvalidOption: --login is only valid with --auth oauth2DeviceCode.");
                context.ExitCode = 1;
                return;
            }

            bool isInteractive = !nonInteractive && (interactive || !Console.IsInputRedirected);

            string? name = pr.GetValueForArgument(nameArg);
            string? transport = pr.GetValueForOption(transportOpt);
            string? endpoint = pr.GetValueForOption(endpointOpt);
            string? tokenRef = pr.GetValueForOption(tokenRefOpt);
            string? headerName = pr.GetValueForOption(headerNameOpt);
            string? valueRef = pr.GetValueForOption(valueRefOpt);
            bool inQueryString = pr.GetValueForOption(inQueryStringOpt);
            string? usernameRef = pr.GetValueForOption(usernameRefOpt);
            string? passwordRef = pr.GetValueForOption(passwordRefOpt);
            string? tokenUrl = pr.GetValueForOption(tokenUrlOpt);
            string? clientIdRef = pr.GetValueForOption(clientIdRefOpt);
            string? clientSecret = pr.GetValueForOption(clientSecretRefOpt);
            string? scopes = pr.GetValueForOption(scopesOpt);
            string? authUrl = pr.GetValueForOption(authUrlOpt);
            string? clientId = pr.GetValueForOption(clientIdOpt);
            string? clientCertRef = pr.GetValueForOption(clientCertRefOpt);
            string? clientKeyRef = pr.GetValueForOption(clientKeyRefOpt);
            string? caCertPath = pr.GetValueForOption(caCertPathOpt);
            string? clientCertPath = pr.GetValueForOption(clientCertPathOpt);
            string? clientKeyPath = pr.GetValueForOption(clientKeyPathOpt);
            int? connectTimeout = pr.GetValueForOption(connectTimeoutOpt);
            int? requestTimeout = pr.GetValueForOption(requestTimeoutOpt);
            bool replace = pr.GetValueForOption(replaceOpt);

            var authOptionsProvided =
                !string.IsNullOrWhiteSpace(tokenRef) ||
                !string.IsNullOrWhiteSpace(headerName) ||
                !string.IsNullOrWhiteSpace(valueRef) ||
                inQueryString ||
                !string.IsNullOrWhiteSpace(usernameRef) ||
                !string.IsNullOrWhiteSpace(passwordRef) ||
                !string.IsNullOrWhiteSpace(tokenUrl) ||
                !string.IsNullOrWhiteSpace(clientIdRef) ||
                !string.IsNullOrWhiteSpace(clientSecret) ||
                !string.IsNullOrWhiteSpace(scopes) ||
                !string.IsNullOrWhiteSpace(authUrl) ||
                !string.IsNullOrWhiteSpace(clientId) ||
                !string.IsNullOrWhiteSpace(clientCertRef) ||
                !string.IsNullOrWhiteSpace(clientKeyRef);

            if (string.IsNullOrWhiteSpace(authType) && !authOptionsProvided)
                authType = "none";

            // Normalize transport alias
            if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
                transport = "httpAutoDetect";

            if (isInteractive)
            {
                if (string.IsNullOrWhiteSpace(name))
                    name = Prompt("Server name");
                if (string.IsNullOrWhiteSpace(transport))
                    transport = Prompt("Transport [stdio/streamableHttp/sse/httpAutoDetect]");
                if (string.IsNullOrWhiteSpace(endpoint))
                    endpoint = Prompt("Endpoint");
                if (string.IsNullOrWhiteSpace(authType))
                    authType = Prompt("Auth [none/bearer/apiKey/basic/oauth2ClientCredentials/oauth2DeviceCode/mtls]");

                // Auth-mode-specific prompts
                switch (authType?.ToLowerInvariant())
                {
                    case "bearer":
                        if (string.IsNullOrWhiteSpace(tokenRef))
                            tokenRef = PromptRef("Token ref (e.g. env:MY_TOKEN)", "--token-ref");
                        break;
                    case "apikey":
                        if (string.IsNullOrWhiteSpace(headerName))
                            headerName = Prompt("Header name");
                        if (string.IsNullOrWhiteSpace(valueRef))
                            valueRef = PromptRef("Value ref (e.g. env:MY_KEY)", "--value-ref");
                        break;
                    case "basic":
                        if (string.IsNullOrWhiteSpace(usernameRef))
                            usernameRef = PromptRef("Username ref (e.g. env:MY_USER)", "--username-ref");
                        if (string.IsNullOrWhiteSpace(passwordRef))
                            passwordRef = PromptRef("Password ref (e.g. env:MY_PASS)", "--password-ref");
                        break;
                    case "oauth2clientcredentials":
                        if (string.IsNullOrWhiteSpace(tokenUrl))
                            tokenUrl = Prompt("Token URL");
                        if (string.IsNullOrWhiteSpace(clientIdRef))
                            clientIdRef = PromptRef("Client ID ref (e.g. env:CLIENT_ID)", "--client-id-ref");
                        if (string.IsNullOrWhiteSpace(clientSecret))
                            clientSecret = PromptRef("Client secret ref (e.g. env:CLIENT_SECRET)", "--client-secret-ref");
                        if (interactive && string.IsNullOrWhiteSpace(scopes))
                            scopes = Prompt("Scopes (comma-separated, optional)", required: false);
                        break;
                    case "oauth2devicecode":
                        if (string.IsNullOrWhiteSpace(authUrl))
                            authUrl = Prompt("Authorization URL");
                        if (string.IsNullOrWhiteSpace(tokenUrl))
                            tokenUrl = Prompt("Token URL");
                        if (string.IsNullOrWhiteSpace(clientId))
                            clientId = Prompt("Client ID");
                        if (interactive && string.IsNullOrWhiteSpace(scopes))
                            scopes = Prompt("Scopes (comma-separated, optional)", required: false);
                        if (interactive && !login)
                        {
                            Console.Write("Start OAuth2 login now? [Y/n]: ");
                            var loginAnswer = Console.ReadLine()?.Trim().ToLowerInvariant();
                            login = loginAnswer != "n" && loginAnswer != "no";
                        }
                        break;
                    case "mtls":
                        if (string.IsNullOrWhiteSpace(clientCertRef))
                            clientCertRef = PromptRef("Client cert ref (e.g. env:CLIENT_CERT)", "--client-cert-ref");
                        if (string.IsNullOrWhiteSpace(clientKeyRef))
                            clientKeyRef = PromptRef("Client key ref (e.g. env:CLIENT_KEY)", "--client-key-ref");
                        break;
                }

                // TLS prompts — only when explicitly requested via --interactive
                if (interactive)
                {
                    var t = transport?.ToLowerInvariant();
                    if (t is "streamablehttp" or "sse" or "httpautodetect" or "http")
                    {
                        if (string.IsNullOrWhiteSpace(caCertPath))
                            caCertPath = Prompt("CA cert path (optional)", required: false);
                        if (string.IsNullOrWhiteSpace(clientCertPath))
                            clientCertPath = Prompt("TLS client cert path (optional)", required: false);
                        if (!string.IsNullOrWhiteSpace(clientCertPath) && string.IsNullOrWhiteSpace(clientKeyPath))
                            clientKeyPath = Prompt("TLS client key path");
                    }
                }
            }
            else
            {
                // Non-interactive: validate required fields are present
                if (string.IsNullOrWhiteSpace(name))
                {
                    await Console.Error.WriteLineAsync("error: MissingOption: name is required when stdin is not interactive.");
                    context.ExitCode = 1;
                    return;
                }
                if (string.IsNullOrWhiteSpace(transport))
                {
                    await Console.Error.WriteLineAsync("error: MissingOption: --transport is required when stdin is not interactive.");
                    context.ExitCode = 1;
                    return;
                }
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    await Console.Error.WriteLineAsync("error: MissingOption: --endpoint is required when stdin is not interactive.");
                    context.ExitCode = 1;
                    return;
                }
                if (string.IsNullOrWhiteSpace(authType))
                {
                    await Console.Error.WriteLineAsync("error: MissingOption: --auth is required when stdin is not interactive.");
                    context.ExitCode = 1;
                    return;
                }
            }

            if (login && !string.Equals(authType, "oauth2DeviceCode", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(authType, "oauth2devicecode", StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync("error: InvalidOption: --login is only valid with --auth oauth2DeviceCode.");
                context.ExitCode = 1;
                return;
            }

            var scopeArray = string.IsNullOrWhiteSpace(scopes)
                ? null
                : scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var normCa = string.IsNullOrWhiteSpace(caCertPath) ? null : caCertPath;
            var normCert = string.IsNullOrWhiteSpace(clientCertPath) ? null : clientCertPath;
            var normKey = string.IsNullOrWhiteSpace(clientKeyPath) ? null : clientKeyPath;
            McpServerAddTlsOptions? tlsOptions = null;
            if (normCa is not null || normCert is not null || normKey is not null)
                tlsOptions = new McpServerAddTlsOptions(normCa, normCert, normKey);

            bool skipProbeForLogin = login &&
                string.Equals(authType, "oauth2DeviceCode", StringComparison.OrdinalIgnoreCase);

            var request = new McpServerAddRequest(
                Name: name ?? string.Empty,
                Transport: transport ?? string.Empty,
                Endpoint: endpoint ?? string.Empty,
                AuthType: authType ?? "none",
                Auth: new McpServerAddAuthOptions(
                    TokenRef: tokenRef,
                    HeaderName: headerName,
                    ValueRef: valueRef,
                    InQueryString: inQueryString ? true : null,
                    UsernameRef: usernameRef,
                    PasswordRef: passwordRef,
                    TokenUrl: tokenUrl,
                    ClientIdRef: clientIdRef,
                    ClientSecretRef: clientSecret,
                    Scopes: scopeArray,
                    AuthUrl: authUrl,
                    ClientId: clientId,
                    ClientCertRef: clientCertRef,
                    ClientKeyRef: clientKeyRef),
                Tls: tlsOptions,
                ConnectTimeoutSeconds: connectTimeout,
                RequestTimeoutSeconds: requestTimeout,
                Replace: replace,
                DryRun: dryRun,
                SkipProbe: noProbe || skipProbeForLogin,
                ForceProbeInDryRun: dryRun && browserOAuthFlowProvider is not null);

            var result = await mcpServerConfigService.AddAsync(request, ct);

            // Interactive: prompt to replace if duplicate (explicit --interactive only)
            if (!result.Success
                && interactive
                && result.Errors.Count == 1
                && result.Errors[0].StartsWith("DuplicateServer", StringComparison.Ordinal))
            {
                Console.Write($"Server '{request.Name}' already exists. Replace it? [y/N]: ");
                var replaceAnswer = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (replaceAnswer is "y" or "yes")
                {
                    request = request with { Replace = true };
                    result = await mcpServerConfigService.AddAsync(request, ct);
                }
            }

            // MCP OAuth two-call onboarding flow
            if (!result.Success
                && result.Probe?.Status == McpServerProbeStatus.AuthRequired
                && result.Probe.AuthGuidance?.SuggestedAuthMode == "mcpOAuth"
                && browserOAuthFlowProvider is not null)
            {
                if (nonInteractive)
                {
                    if (json)
                    {
                        var g = result.Probe?.AuthGuidance;
                        var authRequiredJson = new McpAddResultJson(
                            Success: false,
                            Error: "AuthRequired",
                            Guidance: new McpAddGuidanceJson(
                                SuggestedAuthMode: "mcpOAuth",
                                AuthorizationUrl: g?.AuthorizationUrl,
                                NextCommands: g?.NextCommands));
                        Console.WriteLine(JsonSerializer.Serialize(authRequiredJson, McpDryRunJsonContext.Default.McpAddResultJson));
                    }
                    else
                    {
                        await Console.Error.WriteLineAsync(
                            "error: AuthRequired: server requires OAuth login. Re-run without --non-interactive or supply --client-id.");
                    }
                    context.ExitCode = 4;
                    return;
                }

                if (dryRun)
                {
                    if (json)
                    {
                        var g = result.Probe?.AuthGuidance;
                        var dryRunOAuthJson = new McpAddResultJson(
                            Success: false,
                            Name: name,
                            Transport: NormalizeTransportForOutput(transport ?? string.Empty),
                            Endpoint: endpoint,
                            Auth: "mcpOAuth",
                            Error: "dry-run: would start browser OAuth flow",
                            Guidance: new McpAddGuidanceJson(
                                SuggestedAuthMode: "mcpOAuth"));
                        Console.WriteLine(JsonSerializer.Serialize(dryRunOAuthJson, McpDryRunJsonContext.Default.McpAddResultJson));
                    }
                    else
                    {
                        Console.WriteLine($"[dry-run] Would probe {endpoint}");
                        Console.WriteLine("[dry-run] Probe result: AuthRequired (MCP OAuth 2.0)");
                        Console.WriteLine("[dry-run] Would start browser OAuth flow (skipped in dry-run)");
                        Console.WriteLine($"[dry-run] Would write config: type=mcpOAuth, endpoint={endpoint}");
                        Console.WriteLine("No changes made.");
                    }
                    context.ExitCode = 0;
                    return;
                }

                if (!json)
                    Console.WriteLine("This server requires OAuth authorization (MCP OAuth 2.0).");

                try
                {
                    var oauthConfig = new McpOAuthConfig(
                        ClientId: clientId,
                        ClientSecretRef: clientSecret,
                        Scopes: string.IsNullOrWhiteSpace(scopes)
                            ? null
                            : scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

                    var flowOptions = new McpBrowserOAuthOptions(
                        NoBrowser: noBrowser,
                        Interactive: !json,
                        Tls: tlsOptions is null ? null : new McpTlsConfig(
                            tlsOptions.CaCertPath, tlsOptions.ClientCertPath, tlsOptions.ClientKeyPath));
                    var oauthProgress = json ? null : new Progress<string>(Console.WriteLine);
                    var flowResult = await browserOAuthFlowProvider.StartFlowAsync(
                        serverName: name ?? string.Empty,
                        endpoint: endpoint ?? string.Empty,
                        config: oauthConfig,
                        options: flowOptions,
                        ct: ct,
                        progress: oauthProgress);

                    if (!flowResult.Succeeded)
                    {
                        if (json)
                        {
                            var failJson = new McpAddResultJson(
                                Success: false,
                                Error: flowResult.Error ?? "Authorization did not complete.");
                            Console.WriteLine(JsonSerializer.Serialize(failJson, McpDryRunJsonContext.Default.McpAddResultJson));
                        }
                        else
                        {
                            await Console.Error.WriteLineAsync(
                                $"error: OAuthFailed: {FormatOAuthError(flowResult.Error)}");
                        }
                        context.ExitCode = 1;
                        return;
                    }

                    if (!json)
                        Console.WriteLine("Authorization complete.");

                    // Second AddAsync call: write config with mcpOAuth, skip probe.
                    // Use CompletedConfig from the flow result (may contain server-supplied ClientId from DCR).
                    var persistedConfig = flowResult.CompletedConfig ?? oauthConfig;
                    var oauthRequest = new McpServerAddRequest(
                        Name: name ?? string.Empty,
                        Transport: transport ?? string.Empty,
                        Endpoint: endpoint ?? string.Empty,
                        AuthType: "mcpOAuth",
                        Auth: new McpServerAddAuthOptions(
                            ClientId: persistedConfig.ClientId,
                            ClientSecretRef: persistedConfig.ClientSecretRef,
                            Scopes: persistedConfig.Scopes),
                        Tls: tlsOptions,
                        ConnectTimeoutSeconds: pr.GetValueForOption(connectTimeoutOpt),
                        RequestTimeoutSeconds: pr.GetValueForOption(requestTimeoutOpt),
                        Replace: replace,
                        DryRun: false,
                        SkipProbe: true);

                    var oauthAddResult = await mcpServerConfigService.AddAsync(oauthRequest, ct);
                    if (!oauthAddResult.Success)
                    {
                        if (json)
                        {
                            var errJson = new McpAddResultJson(
                                Success: false,
                                Error: oauthAddResult.Errors.Count > 0 ? oauthAddResult.Errors[0] : "AddFailed");
                            Console.WriteLine(JsonSerializer.Serialize(errJson, McpDryRunJsonContext.Default.McpAddResultJson));
                        }
                        else
                        {
                            foreach (var err in oauthAddResult.Errors)
                                await Console.Error.WriteLineAsync($"error: {err}");
                        }
                        context.ExitCode = 1;
                        return;
                    }

                    if (json)
                    {
                        var successJson = new McpAddResultJson(
                            Success: true,
                            Name: name,
                            Transport: NormalizeTransportForOutput(transport ?? string.Empty),
                            Endpoint: endpoint,
                            Auth: "mcpOAuth",
                            ToolCount: flowResult.ToolCount);
                        Console.WriteLine(JsonSerializer.Serialize(successJson, McpDryRunJsonContext.Default.McpAddResultJson));
                    }
                    else
                    {
                        Console.WriteLine($"{name} added ({flowResult.ToolCount} tools available)");
                        Console.WriteLine($"  Transport : {NormalizeTransportForOutput(transport ?? string.Empty)}");
                        Console.WriteLine($"  Endpoint  : {endpoint}");
                        Console.WriteLine($"  Auth      : MCP OAuth 2.0");
                    }
                }
                catch (OperationCanceledException)
                {
                    await Console.Error.WriteLineAsync("Authorization cancelled.");
                    context.ExitCode = 130;
                    return;
                }

                return;
            }

            if (!result.Success)
            {
                if (json)
                {
                    var g = result.Probe?.AuthGuidance;
                    var errJson = new McpAddResultJson(
                        Success: false,
                        Error: result.Errors.Count > 0 ? result.Errors[0] : "AddFailed",
                        Guidance: g is not null ? new McpAddGuidanceJson(
                            SuggestedAuthMode: g.SuggestedAuthMode,
                            AuthorizationUrl: g.AuthorizationUrl,
                            NextCommands: g.NextCommands) : null);
                    Console.WriteLine(JsonSerializer.Serialize(errJson, McpDryRunJsonContext.Default.McpAddResultJson));
                    context.ExitCode = 1;
                    return;
                }

                if (result.Probe is not null)
                {
                    foreach (var err in result.Errors)
                        await Console.Error.WriteLineAsync($"error: {err}");
                    await Console.Error.WriteLineAsync("No config was written.");

                    var guidance = result.Probe.AuthGuidance;
                    if (guidance?.NextCommands is { Count: > 0 })
                    {
                        await Console.Error.WriteLineAsync(string.Empty);
                        await Console.Error.WriteLineAsync("Try one of:");
                        foreach (var cmd2 in guidance.NextCommands)
                            await Console.Error.WriteLineAsync($"  {cmd2}");
                    }

                    var status = result.Probe.Status;
                    if (status is McpServerProbeStatus.Timeout
                        or McpServerProbeStatus.ConnectionFailed
                        or McpServerProbeStatus.Unknown)
                    {
                        await Console.Error.WriteLineAsync(
                            "Retry with --no-probe to persist the config offline.");
                    }
                    else if (status != McpServerProbeStatus.InvalidConfig)
                    {
                        await Console.Error.WriteLineAsync(string.Empty);
                        await Console.Error.WriteLineAsync(
                            "Use --no-probe only if you want to save the config before credentials are available.");
                    }
                }
                else
                {
                    foreach (var err in result.Errors)
                        await Console.Error.WriteLineAsync($"error: {err}");
                }
                context.ExitCode = 1;
                return;
            }

            if (dryRun)
            {
                var dryRunJson = BuildDryRunJson(request);
                Console.WriteLine(JsonSerializer.Serialize(dryRunJson, McpDryRunJsonContext.Default.McpAddDryRunJson));
                return;
            }

            if (login)
            {
                if (json)
                {
                    // Inline device-code login without human-readable output to keep stdout clean JSON.
                    var loadResult = await serverDefinitionRepository.LoadAsync(ct);
                    if (!loadResult.IsOk)
                    {
                        var errJson = new McpAddResultJson(Success: false, Name: request.Name, Error: "ConfigLoadFailed");
                        Console.WriteLine(JsonSerializer.Serialize(errJson, McpDryRunJsonContext.Default.McpAddResultJson));
                        context.ExitCode = 1;
                        return;
                    }

                    var loginDef = loadResult.Value.FirstOrDefault(s =>
                        string.Equals(s.Name, request.Name, StringComparison.OrdinalIgnoreCase));
                    if (loginDef is null)
                    {
                        var errJson = new McpAddResultJson(Success: false, Name: request.Name, Error: "ServerNotFound");
                        Console.WriteLine(JsonSerializer.Serialize(errJson, McpDryRunJsonContext.Default.McpAddResultJson));
                        context.ExitCode = 1;
                        return;
                    }

                    if (loginDef.Auth is OAuth2DeviceCodeConfig)
                    {
                        var errJson = new McpAddResultJson(
                            Success: false,
                            Name: request.Name,
                            Error: $"AuthLoginRequired: device-code login requires interaction. Run: hypa mcp auth login --server {request.Name}");
                        Console.WriteLine(JsonSerializer.Serialize(errJson, McpDryRunJsonContext.Default.McpAddResultJson));
                        context.ExitCode = 1;
                        return;
                    }

                    try
                    {
                        await authProvider.GetAuthContextAsync(loginDef, ct);
                        var successJson = new McpAddResultJson(
                            Success: true,
                            Name: request.Name,
                            Transport: NormalizeTransportForOutput(request.Transport),
                            Endpoint: request.Endpoint,
                            Auth: request.AuthType);
                        Console.WriteLine(JsonSerializer.Serialize(successJson, McpDryRunJsonContext.Default.McpAddResultJson));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        var errJson = new McpAddResultJson(Success: false, Name: request.Name, Error: "AuthLoginFailed");
                        Console.WriteLine(JsonSerializer.Serialize(errJson, McpDryRunJsonContext.Default.McpAddResultJson));
                        context.ExitCode = 1;
                    }

                    return;
                }

                Console.WriteLine($"Added MCP server: {request.Name}");
                Console.WriteLine($"Starting OAuth2 device-code login for {request.Name}...");
                var loginError = await DoAuthLoginAsync(request.Name, context, ct);
                if (loginError is null)
                {
                    Console.WriteLine($"Authenticated: {request.Name}");
                    Console.WriteLine($"Run: hypa mcp schema --server {request.Name}");
                }
                else
                {
                    await Console.Error.WriteLineAsync($"error: AuthLoginFailed: {loginError}");
                    await Console.Error.WriteLineAsync($"Run: hypa mcp auth login --server {request.Name}");
                    context.ExitCode = 1;
                }
                return;
            }

            if (json)
            {
                var successJson = new McpAddResultJson(
                    Success: true,
                    Name: request.Name,
                    Transport: NormalizeTransportForOutput(request.Transport),
                    Endpoint: request.Endpoint,
                    Auth: request.AuthType);
                Console.WriteLine(JsonSerializer.Serialize(successJson, McpDryRunJsonContext.Default.McpAddResultJson));
                return;
            }

            Console.WriteLine($"Added MCP server: {request.Name}");
            Console.WriteLine($"Run: hypa mcp auth check --server {request.Name}");
            Console.WriteLine($"Run: hypa mcp schema --server {request.Name}");
        });

        return cmd;
    }

    private static string Prompt(string label, bool required = true)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            if (!required)
                return string.Empty;
        }
    }

    private static string PromptRef(string label, string optionName, bool required = true)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!required) return string.Empty;
                continue;
            }
            if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return value;
            Console.Error.WriteLine($"error: InvalidSecretRef: {optionName} must use an explicit resolver prefix such as env: or file:.");
        }
    }

    private static McpAddDryRunJson BuildDryRunJson(McpServerAddRequest request)
    {
        var auth = BuildAuthDryRunJson(request.AuthType, request.Auth);
        McpAddTlsDryRunJson? tls = request.Tls is { } t
            ? new McpAddTlsDryRunJson(t.CaCertPath, t.ClientCertPath, t.ClientKeyPath)
            : null;

        return new McpAddDryRunJson(
            request.Name,
            NormalizeTransportForOutput(request.Transport),
            request.Endpoint,
            auth,
            tls,
            request.ConnectTimeoutSeconds,
            request.RequestTimeoutSeconds);
    }

    private static McpAddAuthDryRunJson BuildAuthDryRunJson(string authType, McpServerAddAuthOptions a)
    {
        return authType.ToLowerInvariant() switch
        {
            "bearer" => new McpAddAuthDryRunJson("bearer", TokenRef: a.TokenRef),
            "apikey" => new McpAddAuthDryRunJson("apiKey",
                HeaderName: a.HeaderName, ValueRef: a.ValueRef, InQueryString: a.InQueryString),
            "basic" => new McpAddAuthDryRunJson("basic",
                UsernameRef: a.UsernameRef, PasswordRef: a.PasswordRef),
            "oauth2clientcredentials" => new McpAddAuthDryRunJson("oauth2ClientCredentials",
                TokenUrl: a.TokenUrl, ClientIdRef: a.ClientIdRef, ClientSecretRef: a.ClientSecretRef, Scopes: a.Scopes),
            "oauth2devicecode" => new McpAddAuthDryRunJson("oauth2DeviceCode",
                AuthUrl: a.AuthUrl, TokenUrl: a.TokenUrl, ClientId: a.ClientId, Scopes: a.Scopes),
            "mtls" => new McpAddAuthDryRunJson("mtls",
                ClientCertRef: a.ClientCertRef, ClientKeyRef: a.ClientKeyRef),
            _ => new McpAddAuthDryRunJson("none"),
        };
    }

    private static string NormalizeTransportForOutput(string transport) =>
        transport.ToLowerInvariant() switch
        {
            "streamablehttp" => "streamableHttp",
            "httpautodetect" or "http" => "httpAutoDetect",
            "sse" => "sse",
            _ => transport,
        };

    private Command BuildInvoke()
    {
        var serverOpt = new Option<string>("--server", "Upstream server name.") { IsRequired = true };
        var toolOpt = new Option<string>("--tool", "Tool name on the server.") { IsRequired = true };
        var argumentsOpt = new Option<string?>("--arguments", "Tool arguments as a JSON object string.");
        var hintOpt = new Option<string?>("--hint", "Compression hint: raw | summary | structured.");
        var jsonOpt = new Option<bool>("--json", "Output result as JSON.");

        var cmd = new Command("invoke", "Invoke a tool on an upstream MCP server.");
        cmd.AddOption(serverOpt);
        cmd.AddOption(toolOpt);
        cmd.AddOption(argumentsOpt);
        cmd.AddOption(hintOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async context =>
        {
            var server = context.ParseResult.GetValueForOption(serverOpt)!;
            var tool = context.ParseResult.GetValueForOption(toolOpt)!;
            var arguments = context.ParseResult.GetValueForOption(argumentsOpt);
            var hint = context.ParseResult.GetValueForOption(hintOpt);
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ct = context.GetCancellationToken();

            var compressionHint = ParseHint(hint);
            var request = new McpProxyRequest(server, tool, new JsonPayload(arguments ?? "{}"), compressionHint);

            McpResult result;
            try
            {
                result = await proxyService.InvokeAsync(request, ct);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"error: {ex.Message}");
                context.ExitCode = 1;
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, McpJsonContext.Default.McpResult));
            }
            else
            {
                if (result.IsError)
                {
                    var code = result.Error?.Code ?? McpErrorCodes.ToolInvocationFailed;
                    await Console.Error.WriteLineAsync($"error ({code}): {result.Error?.Message}");
                    context.ExitCode = 1;
                    return;
                }

                Console.WriteLine(result.CompressedResponse);
                Console.Error.WriteLine($"duration: {result.Latency.Elapsed.TotalMilliseconds:F0}ms");
            }
        });

        return cmd;
    }

    private Command BuildBatch()
    {
        var serverOpt = new Option<string>("--server", "Default server name for requests that omit it.") { IsRequired = false };
        var fileOpt = new Option<FileInfo>("--file", "Path to a JSON file containing a batch requests array.") { IsRequired = true };
        var jsonOpt = new Option<bool>("--json", "Output results as JSON.");

        var cmd = new Command("batch", "Invoke multiple tools in parallel.");
        cmd.AddOption(serverOpt);
        cmd.AddOption(fileOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async context =>
        {
            var defaultServer = context.ParseResult.GetValueForOption(serverOpt);
            var file = context.ParseResult.GetValueForOption(fileOpt)!;
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ct = context.GetCancellationToken();

            if (!file.Exists)
            {
                await Console.Error.WriteLineAsync($"error: file not found: {file.FullName}");
                context.ExitCode = 1;
                return;
            }

            IReadOnlyList<McpProxyRequest> batch;
            try
            {
                var fileContent = await File.ReadAllTextAsync(file.FullName, ct);
                batch = ParseBatchFile(fileContent, defaultServer);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse batch file '{File}'", file.FullName);
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.InvalidRequest}): failed to parse batch file.");
                context.ExitCode = 1;
                return;
            }

            if (batch.Count == 0)
            {
                await Console.Error.WriteLineAsync("error: batch file contains no requests.");
                context.ExitCode = 1;
                return;
            }

            IReadOnlyList<McpResult> results;
            try
            {
                results = await proxyService.InvokeBatchAsync(batch, ct);
            }
            catch
            {
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.ToolInvocationFailed}): batch invocation failed.");
                context.ExitCode = 1;
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(results, McpJsonContext.Default.IReadOnlyListMcpResult));
            }
            else
            {
                var succeeded = results.Count(r => !r.IsError);
                var failed = results.Count(r => r.IsError);
                Console.WriteLine($"batch: {results.Count} request(s) — {succeeded} succeeded, {failed} failed.");
                foreach (var r in results)
                {
                    var status = r.IsError ? "ERROR" : "OK";
                    Console.WriteLine($"  {r.ServerName}/{r.ToolName} {status} ({r.Latency.Elapsed.TotalMilliseconds:F0}ms)");
                    if (r.IsError && r.Error is not null)
                        Console.WriteLine($"    {r.Error.Code}: {r.Error.Message}");
                }
                if (failed > 0)
                    context.ExitCode = 1;
            }
        });

        return cmd;
    }

    private Command BuildSchema()
    {
        var serverOpt = new Option<string?>("--server", "Filter schema to a single server.");
        var jsonOpt = new Option<bool>("--json", "Output schema as JSON.");

        var cmd = new Command("schema", "Show tool schemas for configured MCP servers.");
        cmd.AddOption(serverOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async context =>
        {
            var server = context.ParseResult.GetValueForOption(serverOpt);
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ct = context.GetCancellationToken();

            McpSchemaManifest manifest;
            try
            {
                manifest = await proxyService.GetSchemaAsync(ct);
            }
            catch
            {
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.SchemaUnavailable}): failed to retrieve schema.");
                context.ExitCode = 1;
                return;
            }

            var servers = string.IsNullOrWhiteSpace(server)
                ? manifest.Servers
                : manifest.Servers.Where(s => string.Equals(s.ServerName, server, StringComparison.OrdinalIgnoreCase)).ToList();

            var errors = string.IsNullOrWhiteSpace(server)
                ? manifest.Errors
                : manifest.Errors?.Where(e => string.Equals(e.ServerName, server, StringComparison.OrdinalIgnoreCase)).ToList();

            if (json)
            {
                var filtered = new McpSchemaManifest(servers, errors);
                Console.WriteLine(JsonSerializer.Serialize(filtered, McpJsonContext.Default.McpSchemaManifest));
                return;
            }

            if (servers.Count == 0 && (errors is null || errors.Count == 0))
            {
                Console.WriteLine(string.IsNullOrWhiteSpace(server)
                    ? "No MCP servers configured."
                    : $"Server '{server}' not found.");
                return;
            }

            foreach (var srv in servers)
            {
                Console.WriteLine($"{srv.ServerName} ({srv.Tools.Count} tool(s)):");
                foreach (var t in srv.Tools)
                    Console.WriteLine($"  {t.Name}: {t.Description}");
            }

            if (errors is { Count: > 0 })
            {
                foreach (var e in errors)
                    await Console.Error.WriteLineAsync($"warning ({e.Code}): {e.ServerName}: {e.Message}");
            }
        });

        return cmd;
    }

    private Command BuildSearch()
    {
        var queryOpt = new Option<string>("--query", "Search query text.") { IsRequired = true };
        var jsonOpt = new Option<bool>("--json", "Output results as JSON.");

        var cmd = new Command("search", "Search for tools across configured MCP servers.");
        cmd.AddOption(queryOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async context =>
        {
            var query = context.ParseResult.GetValueForOption(queryOpt)!;
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ct = context.GetCancellationToken();

            IReadOnlyList<McpToolSearchResult> results;
            try
            {
                results = await proxyService.SearchToolsAsync(query, ct);
            }
            catch
            {
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.SchemaUnavailable}): failed to search tools.");
                context.ExitCode = 1;
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(results, McpJsonContext.Default.IReadOnlyListMcpToolSearchResult));
                return;
            }

            if (results.Count == 0)
            {
                Console.WriteLine($"No tools matching '{query}'.");
                return;
            }

            Console.WriteLine($"Found {results.Count} tool(s) matching '{query}':");
            foreach (var r in results)
                Console.WriteLine($"  {r.ServerName}/{r.ToolName} (score={r.Score:F2}): {r.Description}");
        });

        return cmd;
    }

    private Command BuildList()
    {
        var jsonOpt = new Option<bool>("--json", "Output server list as JSON.");

        var cmd = new Command("list", "List configured upstream MCP servers.");
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async context =>
        {
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ct = context.GetCancellationToken();

            var loadResult = await serverDefinitionRepository.LoadAsync(ct);
            if (!loadResult.IsOk)
            {
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.ServerUnavailable}): failed to load server configuration: {loadResult.Error.Message}");
                context.ExitCode = 1;
                return;
            }

            var servers = loadResult.Value;

            if (json)
            {
                var items = servers
                    .Select(s => new McpServerListItemJson(
                        s.Name,
                        s.Transport.Kind.ToString(),
                        s.Transport.Endpoint,
                        s.Auth.GetType().Name.Replace("Config", string.Empty, StringComparison.Ordinal),
                        s.Tls is not null))
                    .ToList();
                Console.WriteLine(JsonSerializer.Serialize(
                    (IReadOnlyList<McpServerListItemJson>)items,
                    McpJsonContext.Default.IReadOnlyListMcpServerListItemJson));
                return;
            }

            if (servers.Count == 0)
            {
                Console.WriteLine("No MCP servers configured.");
                return;
            }

            foreach (var s in servers)
            {
                var auth = s.Auth.GetType().Name.Replace("Config", string.Empty, StringComparison.Ordinal);
                var endpoint = s.Transport.Endpoint ?? "—";
                Console.WriteLine($"  {s.Name}  {s.Transport.Kind}  {endpoint}  {auth}");
            }
        });

        return cmd;
    }

    private Command BuildTools()
    {
        var serverOpt = new Option<string?>("--server", "Filter tools to a single server.");
        var jsonOpt = new Option<bool>("--json", "Output tool list as JSON.");

        var cmd = new Command("tools", "List available tools across configured MCP servers.");
        cmd.AddOption(serverOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async context =>
        {
            var server = context.ParseResult.GetValueForOption(serverOpt);
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ct = context.GetCancellationToken();

            McpSchemaManifest manifest;
            try
            {
                manifest = await proxyService.GetSchemaAsync(ct);
            }
            catch
            {
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.SchemaUnavailable}): failed to retrieve tool list.");
                context.ExitCode = 1;
                return;
            }

            var servers = string.IsNullOrWhiteSpace(server)
                ? manifest.Servers
                : manifest.Servers.Where(s => string.Equals(s.ServerName, server, StringComparison.OrdinalIgnoreCase)).ToList();

            var errors = string.IsNullOrWhiteSpace(server)
                ? manifest.Errors
                : manifest.Errors?.Where(e => string.Equals(e.ServerName, server, StringComparison.OrdinalIgnoreCase)).ToList();

            if (json)
            {
                var entries = servers
                    .SelectMany(s => s.Tools.Select(t => new McpToolListEntryJson(s.ServerName, t.Name, t.Description)))
                    .ToList();
                Console.WriteLine(JsonSerializer.Serialize(
                    (IReadOnlyList<McpToolListEntryJson>)entries,
                    McpJsonContext.Default.IReadOnlyListMcpToolListEntryJson));

                if (errors is { Count: > 0 })
                    foreach (var e in errors)
                        await Console.Error.WriteLineAsync($"warning ({e.Code}): {e.ServerName}: {e.Message}");

                return;
            }

            if (servers.Count == 0 && (errors is null || errors.Count == 0))
            {
                Console.WriteLine(string.IsNullOrWhiteSpace(server)
                    ? "No tools found."
                    : $"Server '{server}' not found.");
                return;
            }

            foreach (var srv in servers)
                foreach (var t in srv.Tools)
                    Console.WriteLine($"  {srv.ServerName}/{t.Name} — {TruncateDescription(t.Description)}");

            if (errors is { Count: > 0 })
                foreach (var e in errors)
                    await Console.Error.WriteLineAsync($"warning ({e.Code}): {e.ServerName}: {e.Message}");
        });

        return cmd;
    }

    private static string TruncateDescription(string? desc, int max = 100) =>
        desc is null ? string.Empty :
        desc.Length <= max ? desc :
        string.Concat(desc.AsSpan(0, max), "…");

    private Command BuildAuth()
    {
        var authCmd = new Command("auth", "Authentication operations for upstream MCP servers.");
        authCmd.AddCommand(BuildAuthCheck());
        authCmd.AddCommand(BuildAuthLogin());
        return authCmd;
    }

    private Command BuildAuthCheck()
    {
        var serverOpt = new Option<string>("--server", "Server name to check.") { IsRequired = true };
        var jsonOpt = new Option<bool>("--json", "Output result as JSON.");

        var cmd = new Command("check", "Validate credentials for a configured MCP server.");
        cmd.AddOption(serverOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async context =>
        {
            var server = context.ParseResult.GetValueForOption(serverOpt)!;
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ct = context.GetCancellationToken();

            var loadResult = await serverDefinitionRepository.LoadAsync(ct);
            if (!loadResult.IsOk)
            {
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.SchemaUnavailable}): failed to load server configuration.");
                context.ExitCode = 1;
                return;
            }

            var definition = loadResult.Value.FirstOrDefault(s =>
                string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase));

            if (definition is null)
            {
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.UnknownServer}): server '{server}' not found in configuration.");
                context.ExitCode = 1;
                return;
            }

            try
            {
                var authContext = await authProvider.GetAuthContextAsync(definition, ct);
                var authMode = definition.Auth.GetType().Name.Replace("Config", string.Empty, StringComparison.Ordinal);

                if (json)
                {
                    var checkResult = new AuthCheckResult(server, authMode, Passed: true);
                    Console.WriteLine(JsonSerializer.Serialize(checkResult, McpJsonContext.Default.AuthCheckResult));
                }
                else
                {
                    Console.WriteLine($"Auth check passed for '{server}'.");
                    Console.WriteLine($"  mode:        {authMode}");
                    Console.WriteLine($"  headers:     {authContext.Headers.Count}");
                    Console.WriteLine($"  bearer:      {(authContext.BearerToken is not null ? "present" : "absent")}");
                    Console.WriteLine($"  certificate: {(authContext.ClientCertificatePath is not null ? "configured" : "none")}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (json)
                {
                    var checkResult = new AuthCheckResult(server, AuthMode: "unknown", Passed: false, Error: $"Auth check failed for '{server}'.");
                    Console.WriteLine(JsonSerializer.Serialize(checkResult, McpJsonContext.Default.AuthCheckResult));
                }
                else
                {
                    await Console.Error.WriteLineAsync($"error ({McpErrorCodes.AuthRequired}): auth check failed for '{server}'.");
                }
                context.ExitCode = 1;
            }
        });

        return cmd;
    }

    private Command BuildAuthLogin()
    {
        var serverOpt = new Option<string>("--server", "Server name to authenticate.") { IsRequired = true };

        var cmd = new Command("login", "Initiate OAuth2 device-code login for a server.");
        cmd.AddOption(serverOpt);

        cmd.SetHandler(async context =>
        {
            var server = context.ParseResult.GetValueForOption(serverOpt)!;
            var ct = context.GetCancellationToken();
            var loginError = await DoAuthLoginAsync(server, context, ct);
            if (loginError is not null)
                await Console.Error.WriteLineAsync($"error ({McpErrorCodes.AuthRequired}): {loginError}");
        });

        return cmd;
    }

    private async Task<string?> DoAuthLoginAsync(string server, InvocationContext context, CancellationToken ct)
    {
        var loadResult = await serverDefinitionRepository.LoadAsync(ct);
        if (!loadResult.IsOk)
        {
            await Console.Error.WriteLineAsync($"error ({McpErrorCodes.SchemaUnavailable}): failed to load server configuration.");
            context.ExitCode = 1;
            return "failed to load server configuration.";
        }

        var definition = loadResult.Value.FirstOrDefault(s =>
            string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            await Console.Error.WriteLineAsync($"error: server '{server}' not found in configuration.");
            context.ExitCode = 1;
            return $"server '{server}' not found in configuration.";
        }

        if (definition.Auth is McpOAuthConfig oauthConfig)
        {
            if (browserOAuthFlowProvider is null)
            {
                await Console.Error.WriteLineAsync("error: OAuth browser flow provider not available.");
                context.ExitCode = 1;
                return "OAuth browser flow provider not available.";
            }

            var oauthProgress = new Progress<string>(Console.WriteLine);
            McpBrowserOAuthFlowResult flowResult;
            try
            {
                flowResult = await browserOAuthFlowProvider.StartFlowAsync(
                    serverName: server,
                    endpoint: definition.Transport.Endpoint ?? string.Empty,
                    config: oauthConfig,
                    options: new McpBrowserOAuthOptions(Tls: definition.Tls),
                    ct: ct,
                    progress: oauthProgress);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.ExitCode = 1;
                return $"auth login failed for '{server}': {ex.Message}";
            }

            if (flowResult.Succeeded)
            {
                Console.WriteLine($"Login successful for '{server}'.");
                return null;
            }

            context.ExitCode = 1;
            return FormatOAuthError(flowResult.Error) ?? $"auth login failed for '{server}'.";
        }

        if (definition.Auth is not OAuth2DeviceCodeConfig)
        {
            var mode = definition.Auth.GetType().Name.Replace("Config", string.Empty, StringComparison.Ordinal);
            await Console.Error.WriteLineAsync(
                $"error: server '{server}' uses '{mode}' auth. 'auth login' only applies to oauth2DeviceCode and mcpOAuth servers.");
            context.ExitCode = 1;
            return $"server '{server}' uses '{mode}' auth.";
        }

        try
        {
            Console.WriteLine($"Initiating device-code login for '{server}'...");
            await authProvider.GetAuthContextAsync(definition, ct);
            Console.WriteLine($"Login successful for '{server}'.");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            context.ExitCode = 1;
            return $"auth login failed for '{server}'.";
        }
    }

    private static IReadOnlyList<McpProxyRequest> ParseBatchFile(string json, string? defaultServer)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Batch file must contain a JSON array.");

        var requests = new List<McpProxyRequest>(root.GetArrayLength());
        foreach (var element in root.EnumerateArray())
        {
            var server = (element.TryGetProperty("server", out var sEl) ? sEl.GetString() : null)
                ?? defaultServer
                ?? string.Empty;
            var tool = element.GetProperty("tool").GetString() ?? string.Empty;
            var argumentsJson = element.TryGetProperty("arguments", out var argsEl)
                ? argsEl.GetRawText()
                : "{}";
            var hintStr = element.TryGetProperty("hint", out var hintEl) ? hintEl.GetString() : null;
            requests.Add(new McpProxyRequest(server, tool, new JsonPayload(argumentsJson), ParseHint(hintStr)));
        }
        return requests;
    }

    private static CompressionHint? ParseHint(string? hint) =>
        hint?.ToLowerInvariant() switch
        {
            "raw" => CompressionHint.Raw,
            "summary" => CompressionHint.Summary,
            "structured" => CompressionHint.Structured,
            _ => null
        };

    // The MCP .NET SDK enforces RFC 9728: the `resource` field in the server's
    // Protected Resource Metadata must exactly match the endpoint URI. Some servers
    // declare the base domain (e.g. https://example.com) while their MCP path is
    // /mcp, causing this mismatch. It is a server-side spec compliance issue and
    // cannot be bypassed client-side.
    private static string FormatOAuthError(string? error)
    {
        if (error is not null && error.Contains("Resource URI in metadata", StringComparison.OrdinalIgnoreCase))
        {
            return $"{error}\n" +
                   "hint: The server's OAuth metadata declares a resource URI that does not match your endpoint.\n" +
                   "      This is a server-side spec compliance issue (RFC 9728). Contact the server operator.";
        }

        return error ?? "Authorization did not complete.";
    }
}
