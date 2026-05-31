using System.Web;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class HypaBrowserOAuthDelegate
{
    private readonly IBrowserLauncher _browserLauncher;
    private readonly IOAuthCallbackListener _callbackListener;
    private readonly IProgress<string>? _progress;
    private readonly TimeSpan _callbackTimeout;
    private readonly bool _interactive;
    private bool _noBrowser;

    public HypaBrowserOAuthDelegate(
        IBrowserLauncher browserLauncher,
        IOAuthCallbackListener callbackListener,
        IProgress<string>? progress = null,
        bool noBrowser = false,
        TimeSpan? callbackTimeout = null,
        bool interactive = true)
    {
        _browserLauncher = browserLauncher;
        _callbackListener = callbackListener;
        _progress = progress;
        _noBrowser = noBrowser;
        _callbackTimeout = callbackTimeout ?? TimeSpan.FromMinutes(5);
        _interactive = interactive;
    }

    public async Task<string?> HandleAsync(Uri authorizationUri, Uri redirectUri, CancellationToken ct)
    {
        // The listener must be started before this delegate is called from the SDK;
        // in the McpTransportBuilder / McpBrowserOAuthFlowProvider paths StartAsync is
        // called before ClientOAuthOptions is constructed.  Guard here for safety.
        await _callbackListener.StartAsync(ct);

        var expectedState = ExtractQueryParam(authorizationUri, "state");

        try
        {
            var authUrl = authorizationUri.ToString();
            var redirectUrl = redirectUri.ToString();

            if (!_noBrowser)
            {
                bool browserOpened = _browserLauncher.TryOpen(authUrl);
                if (!browserOpened)
                    _noBrowser = true;
            }

            if (_noBrowser)
            {
                _progress?.Report("Authorization URL:");
                _progress?.Report($"  {authUrl}");
                _progress?.Report(string.Empty);
                _progress?.Report($"After authorizing, your browser will redirect to {redirectUrl}");
                if (!_interactive)
                    throw new InvalidOperationException(
                        "Browser OAuth requires interaction. Omit --no-browser or run without --json.");
            }
            else
            {
                _progress?.Report("Opening browser for authorization...");
                _progress?.Report("Authorization URL (if browser didn't open):");
                _progress?.Report($"  {authUrl}");
            }

            _progress?.Report("Waiting for authorization (Ctrl+C to cancel)...");

            if (_noBrowser && _interactive && !Console.IsInputRedirected)
                return await RacePasteAndListenerAsync(redirectUri, expectedState, ct);

            var callbackResult = await _callbackListener.WaitForCallbackAsync(_callbackTimeout, ct);
            return ValidateAndExtractCode(callbackResult, expectedState);
        }
        finally
        {
            await _callbackListener.StopAsync();
        }
    }

    private async Task<string?> RacePasteAndListenerAsync(
        Uri redirectUri, string? expectedState, CancellationToken ct)
    {
        _progress?.Report("If the redirect doesn't fire, paste the full callback URL here:");

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        raceCts.CancelAfter(_callbackTimeout);

        var listenerTask = _callbackListener.WaitForCallbackAsync(_callbackTimeout, ct);

        while (!raceCts.Token.IsCancellationRequested)
        {
            var pasteTask = ReadPastedInputAsync(raceCts.Token);
            var completed = await Task.WhenAny(pasteTask, listenerTask);

            if (completed == listenerTask)
            {
                // Automatic redirect completed.
                var listenerResult = await listenerTask;
                return ValidateAndExtractCode(listenerResult, expectedState);
            }

            // Paste attempt arrived first.
            string? line;
            try { line = await pasteTask; }
            catch (OperationCanceledException) { break; }

            if (string.IsNullOrWhiteSpace(line))
            {
                _progress?.Report("Input was empty — still waiting.");
                continue;
            }

            if (!Uri.TryCreate(line.Trim(), UriKind.Absolute, out var pastedUri))
            {
                _progress?.Report("Not a valid URL — still waiting.");
                continue;
            }

            if (!MatchesRedirectUri(pastedUri, redirectUri))
            {
                _progress?.Report($"URL does not match expected redirect URI — still waiting.");
                continue;
            }

            var rawQs = pastedUri.Query.TrimStart('?');
            OAuthCallbackListener.ParseQueryString(rawQs, out var code, out _, out var receivedState);

            var stateError = ValidateState(expectedState, receivedState);
            if (stateError is not null)
            {
                _progress?.Report($"{stateError} — still waiting.");
                continue;
            }

            if (string.IsNullOrEmpty(code))
            {
                _progress?.Report("No authorization code in URL — still waiting.");
                continue;
            }

            return code;
        }

        // Timed out or cancelled; let the listener result determine the final outcome.
        if (listenerTask.IsCompletedSuccessfully)
            return ValidateAndExtractCode(await listenerTask, expectedState);

        return null;
    }

    private static string? ValidateAndExtractCode(OAuthCallbackResult result, string? expectedState)
    {
        if (string.IsNullOrEmpty(result.Code))
            return null;

        var stateError = ValidateState(expectedState, result.State);
        if (stateError is not null)
            return null;

        return result.Code;
    }

    private static string? ValidateState(string? expected, string? received)
    {
        if (string.IsNullOrEmpty(expected))
            return null; // No state in auth URL; nothing to validate.

        if (string.IsNullOrEmpty(received))
            return "OAuth state missing from callback";

        if (!string.Equals(expected, received, StringComparison.Ordinal))
            return "OAuth state mismatch";

        return null;
    }

    private static bool MatchesRedirectUri(Uri pasted, Uri expected) =>
        string.Equals(pasted.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(pasted.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
        pasted.Port == expected.Port &&
        string.Equals(pasted.AbsolutePath.TrimEnd('/'), expected.AbsolutePath.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static string? ExtractQueryParam(Uri uri, string name)
    {
        var qs = uri.Query.TrimStart('?');
        OAuthCallbackListener.ParseQueryString(qs, out _, out _, out var state);
        // state is the only param we currently need; for other params extend ParseQueryString.
        if (name == "state") return state;

        // Fallback for any other param via HttpUtility (no trim loss of state special-casing).
        var parsed = HttpUtility.ParseQueryString(qs);
        return parsed[name];
    }

    private static Task<string?> ReadPastedInputAsync(CancellationToken ct) =>
        Task.Run<string?>(() =>
        {
            Console.Write("> ");
            return Console.ReadLine();
        }, ct);
}
