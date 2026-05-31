using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Services;

public sealed class McpToolSearchIndex
{
    public IReadOnlyList<McpToolSearchResult> Search(McpSchemaManifest manifest, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var queryTokens = Tokenise(query);
        if (queryTokens.Count == 0)
            return [];

        var results = new List<(McpToolSearchResult Result, double Score)>();

        foreach (var server in manifest.Servers)
        {
            foreach (var tool in server.Tools)
            {
                var score = ScoreTool(tool, server.ServerName, queryTokens, query);
                if (score > 0.0)
                    results.Add((new McpToolSearchResult(server.ServerName, tool.Name, tool.Description, score), score));
            }
        }

        return results
            .OrderByDescending(x => x.Score)
            .Select(x => x.Result with { Score = x.Score })
            .ToList();
    }

    private static double ScoreTool(
        McpToolSchema tool,
        string serverName,
        HashSet<string> queryTokens,
        string rawQuery)
    {
        var toolText = $"{serverName} {tool.Name} {tool.Description} {tool.InputSchema.RawJson}";
        var toolTokens = Tokenise(toolText);

        if (toolTokens.Count == 0)
            return 0.0;

        var overlap = queryTokens.Count(t => toolTokens.Contains(t));
        var union = queryTokens.Count + toolTokens.Count - overlap;
        var jaccard = union > 0 ? (double)overlap / union : 0.0;

        var nameBonus = 0.0;
        var lowerName = tool.Name.ToLowerInvariant();
        var lowerQuery = rawQuery.Trim().ToLowerInvariant();

        if (string.Equals(lowerName, lowerQuery, StringComparison.Ordinal))
            nameBonus = 0.6;
        else if (queryTokens.All(t => Tokenise(tool.Name).Contains(t)))
            nameBonus = 0.4;

        return jaccard + nameBonus;
    }

    private static HashSet<string> Tokenise(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var span = text.AsSpan();
        var start = -1;

        for (var i = 0; i <= span.Length; i++)
        {
            var isAlNum = i < span.Length && char.IsLetterOrDigit(span[i]);
            if (isAlNum && start < 0)
            {
                start = i;
            }
            else if (!isAlNum && start >= 0)
            {
                tokens.Add(span[start..i].ToString().ToLowerInvariant());
                start = -1;
            }
        }

        return tokens;
    }
}
