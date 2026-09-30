using Hypa.AgentRuntime.Application;

namespace Hypa.AgentIntelligence;

// / <summary>BCL HTTP fetch. HYPA catalog URL only.
public sealed class HttpAgentManifestFetcher : IAgentManifestTextFetcher
{
    public AgentManifestFetchResult Fetch(string url, int maxBytes)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = client.Send(request);
            if (!response.IsSuccessStatusCode)
                return new AgentManifestFetchResult(false, "", $"failed to fetch {url}");

            using var stream = response.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            var total = 0;
            while (true)
            {
                var n = stream.Read(chunk, 0, chunk.Length);
                if (n == 0)
                    break;
                total += n;
                if (total > maxBytes)
                    return new AgentManifestFetchResult(false, "", $"response from {url} exceeded {maxBytes} bytes");
                buffer.Write(chunk, 0, n);
            }

            return new AgentManifestFetchResult(true, System.Text.Encoding.UTF8.GetString(buffer.ToArray()), null);
        }
        catch (Exception ex)
        {
            return new AgentManifestFetchResult(false, "", ex.Message);
        }
    }
}
