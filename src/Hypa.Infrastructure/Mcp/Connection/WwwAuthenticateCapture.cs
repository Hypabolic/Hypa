namespace Hypa.Infrastructure.Mcp.Connection;

internal sealed class WwwAuthenticateCapture : DelegatingHandler
{
    public bool HasBearerChallenge { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode == 401
            && response.Headers.WwwAuthenticate.Any(
                h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)))
        {
            HasBearerChallenge = true;
        }
        return response;
    }
}
