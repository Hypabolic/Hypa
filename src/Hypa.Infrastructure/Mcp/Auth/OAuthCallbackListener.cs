using System.Net;
using System.Net.Sockets;
using System.Text;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class OAuthCallbackListener : IOAuthCallbackListener
{
    private TcpListener? _portHolder;
    private HttpListener? _listener;
    private readonly int _port;
    private bool _started;

    private static readonly string SuccessHtml =
        "<!DOCTYPE html><html><body><h1>Authorization complete</h1>" +
        "<p>You may close this window and return to the terminal.</p></body></html>";

    public OAuthCallbackListener()
    {
        // Bind a TcpListener on port 0 to obtain and hold a free port.
        // The port remains bound (by TcpListener) until StartAsync switches to HttpListener,
        // satisfying the "publish only a bound URI" invariant. There is still a brief
        // stop/start transition inside StartAsync, but no other process can steal the port
        // between construction and that point.
        _portHolder = new TcpListener(IPAddress.Loopback, 0);
        _portHolder.Start();
        _port = ((IPEndPoint)_portHolder.LocalEndpoint).Port;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;

        // Release the TcpListener and immediately bind HttpListener on the same port.
        _portHolder?.Stop();
        _portHolder = null;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/callback/");
        _listener.Start();
        _started = true;

        return Task.CompletedTask;
    }

    public Uri GetRedirectUri()
    {
        if (!_started)
            throw new InvalidOperationException(
                "GetRedirectUri() called before StartAsync(). " +
                "The listener must be started to publish a bound redirect URI.");

        return new($"http://127.0.0.1:{_port}/callback");
    }

    public async Task<OAuthCallbackResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_listener is null)
            throw new InvalidOperationException("Listener not started.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var contextTask = _listener.GetContextAsync();
            await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (!contextTask.IsCompletedSuccessfully)
            {
                // Timeout: stop the listener to release resources and force the
                // abandoned GetContextAsync() to fault (its exception is observed
                // by the HttpListener infrastructure rather than left dangling).
                _listener?.Stop();
                return new OAuthCallbackResult(null, null, null);
            }

            var context = await contextTask;
            var rawQuery = context.Request.Url?.Query ?? string.Empty;
            var rawQs = rawQuery.TrimStart('?');

            ParseQueryString(rawQs, out var code, out var error, out var state);

            // Serve success page
            var response = context.Response;
            var bytes = Encoding.UTF8.GetBytes(SuccessHtml);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, CancellationToken.None);
            response.Close();

            return new OAuthCallbackResult(code, error, state);
        }
        catch (OperationCanceledException)
        {
            return new OAuthCallbackResult(null, null, null);
        }
        catch (HttpListenerException)
        {
            return new OAuthCallbackResult(null, null, null);
        }
    }

    public Task StopAsync()
    {
        try { _portHolder?.Stop(); } catch { }
        _portHolder = null;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _started = false;
        return Task.CompletedTask;
    }

    internal static void ParseQueryString(string rawQs, out string? code, out string? error, out string? state)
    {
        code = null;
        error = null;
        state = null;

        if (string.IsNullOrEmpty(rawQs))
            return;

        foreach (var part in rawQs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = Uri.UnescapeDataString(part[..eqIdx]);
            var value = Uri.UnescapeDataString(part[(eqIdx + 1)..]);

            if (string.Equals(key, "code", StringComparison.OrdinalIgnoreCase))
                code = value;
            else if (string.Equals(key, "error", StringComparison.OrdinalIgnoreCase))
                error = value;
            else if (string.Equals(key, "state", StringComparison.OrdinalIgnoreCase))
                state = value;
        }
    }

}
