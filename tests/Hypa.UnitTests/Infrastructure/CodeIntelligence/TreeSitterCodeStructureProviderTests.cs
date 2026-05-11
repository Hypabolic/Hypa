using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Sdk.CodeIntelligence;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.CodeIntelligence;

public sealed class TreeSitterCodeStructureProviderTests
{
    [Fact]
    public async Task ParseAsync_UsesTreeSitterForCSharp()
    {
        var provider = new TreeSitterCodeStructureProvider();
        var file = new CodeFileIdentity
        {
            ProjectRoot = "/repo",
            Path = "/repo/Sample.cs",
            RelativePath = "Sample.cs",
            Language = "c-sharp",
            ContentHash = "hash",
            SizeBytes = 64,
        };

        Assert.True(provider.CanHandle("c-sharp"));

        var document = await provider.ParseAsync(file, "public sealed class Sample { public void Run() { } }", CancellationToken.None);

        Assert.Contains(document.Symbols, s => s.Name == "Sample" && s.Provenance.ProviderId == "tree-sitter");
        Assert.All(document.Symbols, s => Assert.Equal("syntactic", s.Provenance.FactKind));
    }

    [Fact]
    public async Task ParseAsync_EmitsSyntacticGraphFactsForCSharp()
    {
        var provider = new TreeSitterCodeStructureProvider();
        var file = new CodeFileIdentity
        {
            ProjectRoot = "/repo",
            Path = "/repo/Sample.cs",
            RelativePath = "Sample.cs",
            Language = "c-sharp",
            ContentHash = "hash",
            SizeBytes = 256,
        };

        var document = await provider.ParseAsync(file, """
            using System;

            public interface IWorker { void Run(); }
            public class BaseWorker { public virtual void Run() { } }
            public class Sample : BaseWorker, IWorker
            {
                public override void Run()
                {
                    Helper();
                    Console.WriteLine("ok");
                }

                private void Helper() { }
            }
            """, CancellationToken.None);

        Assert.Contains(document.DependencyEdges, e => e.Kind == "calls" && e.TargetName == "Helper" && e.TargetResolutionStatus == "local-symbol");
        Assert.Contains(document.DependencyEdges, e => e.Kind == "calls" && e.TargetName == "WriteLine" && e.TargetResolutionStatus == "external-name");
        Assert.Contains(document.DependencyEdges, e => e.Kind == "inherits" && e.TargetName == "BaseWorker");
        Assert.Contains(document.DependencyEdges, e => e.Kind == "implements" && e.TargetName == "IWorker");
        Assert.Contains(document.DependencyEdges, e => e.Kind == "overrides" && e.TargetName == "Run");
        Assert.Contains(document.References, r => r.Kind == "call" && r.Target == "Helper");
        Assert.Contains(document.References, r => r.Kind == "override" && r.Target == "Run");
    }

    [Fact]
    public async Task ParseAsync_DoesNotTreatUsingDeclarationAsImport()
    {
        var provider = new TreeSitterCodeStructureProvider();
        var file = new CodeFileIdentity
        {
            ProjectRoot = "/repo",
            Path = "/repo/Sample.cs",
            RelativePath = "Sample.cs",
            Language = "c-sharp",
            ContentHash = "hash",
            SizeBytes = 128,
        };

        var document = await provider.ParseAsync(file, """
            using System;

            public class Sample
            {
                public void Run()
                {
                    using var resource = CreateResource();
                }
            }
            """, CancellationToken.None);

        Assert.Contains(document.DependencyEdges, e => e.Kind == "imports" && e.TargetName == "System");
        Assert.DoesNotContain(document.DependencyEdges, e => e.Kind == "imports" && e.TargetName?.Contains("resource", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ParseAsync_EmitsTypeScriptAndPythonRelationshipFacts()
    {
        var provider = new TreeSitterCodeStructureProvider();
        var tsFile = new CodeFileIdentity
        {
            ProjectRoot = "/repo",
            Path = "/repo/sample.ts",
            RelativePath = "sample.ts",
            Language = "typescript",
            ContentHash = "hash",
            SizeBytes = 128,
        };
        var pyFile = tsFile with { Path = "/repo/tool.py", RelativePath = "tool.py", Language = "python" };

        var ts = await provider.ParseAsync(tsFile, "class Widget extends BaseWidget implements Renderable { override render() { draw(); } }", CancellationToken.None);
        var py = await provider.ParseAsync(pyFile, "class Tool(BaseTool):\n    def run(self):\n        execute()\n", CancellationToken.None);

        Assert.Contains(ts.DependencyEdges, e => e.Kind == "inherits" && e.TargetName == "BaseWidget");
        Assert.Contains(ts.DependencyEdges, e => e.Kind == "implements" && e.TargetName == "Renderable");
        Assert.Contains(ts.DependencyEdges, e => e.Kind == "overrides" && e.TargetName == "render");
        Assert.Contains(py.DependencyEdges, e => e.Kind == "inherits" && e.TargetName == "BaseTool");
        Assert.Contains(py.DependencyEdges, e => e.Kind == "calls" && e.TargetName == "execute");
    }

    [Fact]
    public void CheckHealth_ReportsLoadedTreeSitter()
    {
        var health = new TreeSitterCodeStructureProvider().CheckHealth();

        Assert.Equal("tree-sitter", health.ProviderId);
        Assert.Equal("ok", health.Status);
    }
}
