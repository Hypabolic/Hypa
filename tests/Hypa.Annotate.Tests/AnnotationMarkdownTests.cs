using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotationMarkdownTests
{
    [Fact]
    public void Format_usesLongerFenceWhenSelectionContainsBackticks()
    {
        var output = AnnotationMarkdown.Format([SampleAnnotation("```example```")]);

        Assert.Contains("````\n```example```\n````", output);
    }

    [Fact]
    public void Format_namesSourcePaneAndAgentWhenPresent()
    {
        var output = AnnotationMarkdown.Format(
        [
            SampleAnnotation(
                "failed to connect",
                workspaceLabel: "api",
                tabLabel: "server",
                agent: "codex"),
        ]);

        Assert.Contains("# Annotated context", output);
        Assert.Contains("Source: api / server", output);
        Assert.Contains("Agent: codex", output);
        Assert.Contains("failed to connect", output);
        Assert.Contains("Check the database first.", output);
    }

    private static Annotation SampleAnnotation(
        string selectedText,
        string workspaceLabel = "api",
        string tabLabel = "server",
        string? agent = null) =>
        new()
        {
            SelectedText = selectedText,
            CapturedAt = "captured",
            Context = new CaptureContext
            {
                WorkspaceLabel = workspaceLabel,
                TabLabel = tabLabel,
                FocusedPaneAgent = agent,
            },
            Id = "one",
            Comment = "Check the database first.",
            CreatedAt = "created",
        };
}
