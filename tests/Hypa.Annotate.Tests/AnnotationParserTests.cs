using System.Text.Json;
using Hypa.Annotate.Application;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotationParserTests
{
    [Fact]
    public void JsonNumberSpelling_doesNotChangeArchiveVersionSemantics()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "version": 1.0,
              "id": "archive-one",
              "archivedAt": "2026-08-26T23:32:00Z",
              "annotations": [
                {
                  "id": "one",
                  "selectedText": "selection one",
                  "comment": "comment one",
                  "capturedAt": "2026-08-08T00:00:00Z",
                  "createdAt": "2026-08-08T00:00:01Z",
                  "context": {}
                }
              ]
            }
            """);

        Assert.True(AnnotationParser.TryParseArchivedSet(document.RootElement, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal("archive-one", parsed.Id);
        Assert.Equal("one", parsed.Annotations[0].Id);
    }
}
