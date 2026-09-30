using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Infrastructure.CodeIntelligence;

internal static class CodePatternExtractor
{
    private static readonly Regex CSharpType = new(@"\b(?:class|interface|struct|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline);
    private static readonly Regex CSharpMember = new(@"\b(?:public|private|protected|internal|static|async|virtual|override|sealed|partial|\s)+(?!class\b|interface\b|struct\b|record\b|enum\b)[A-Za-z_][A-Za-z0-9_<>,\[\]\?]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline);
    private static readonly Regex JsFunction = new(@"\b(?:export\s+)?(?:async\s+)?function\s+([A-Za-z_$][A-Za-z0-9_$]*)|\b(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s*)?\(", RegexOptions.Multiline);
    private static readonly Regex JsClass = new(@"\b(?:export\s+)?class\s+([A-Za-z_$][A-Za-z0-9_$]*)", RegexOptions.Multiline);
    private static readonly Regex PythonDef = new(@"^\s*(?:async\s+def|def|class)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline);
    private static readonly Regex GoSymbol = new(@"\bfunc\s+(?:\([^)]+\)\s*)?([A-Za-z_][A-Za-z0-9_]*)\s*\(|\btype\s+([A-Za-z_][A-Za-z0-9_]*)\s+", RegexOptions.Multiline);
    private static readonly Regex RustSymbol = new(@"\b(?:fn|struct|enum|trait|impl)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline);
    private static readonly Regex JavaSymbol = new(@"\b(?:class|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)|\b(?:public|private|protected|static|\s)+[A-Za-z_][A-Za-z0-9_<>,\[\]]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline);
    private static readonly Regex CFamilySymbol = new(@"\b(?:struct|enum|class)\s+([A-Za-z_][A-Za-z0-9_]*)|\b[A-Za-z_][A-Za-z0-9_\*\s]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\([^;]*\)\s*\{", RegexOptions.Multiline);
    private static readonly Regex ShellFunction = new(@"^\s*(?:function\s+)?([A-Za-z_][A-Za-z0-9_-]*)\s*(?:\(\))?\s*\{", RegexOptions.Multiline);
    private static readonly Regex JsonKey = new("\"([^\"]+)\"\\s*:", RegexOptions.Multiline);
    private static readonly Regex TomlKey = new(@"^\s*([A-Za-z0-9_.-]+)\s*=", RegexOptions.Multiline);
    private static readonly Regex YamlKey = new(@"^\s*([A-Za-z0-9_.-]+)\s*:", RegexOptions.Multiline);
    private static readonly Regex Import = new(@"^\s*(?:(?:using\s+(?!var\b))|import\s+|from\s+|package\s+|#include\s+|require\s+|source\s+)[""<']?([^""<';\n]+)", RegexOptions.Multiline);
    // Full dotted member-access chains, optional global:: alias
    // (global::System.Console.WriteLine / Console.WriteLine / Helper).
    // Whitespace around dots/:: is allowed in source and collapsed when building TargetName.
    private static readonly Regex Call = new(
        @"(?:\b(?<g>global)\s*::\s*|\b)(?<name>(?:[A-Za-z_$][A-Za-z0-9_$]*)(?:\s*\.\s*[A-Za-z_$][A-Za-z0-9_$]*)*)\s*\(",
        RegexOptions.Multiline);
    private static readonly Regex Identifier = new(@"\b[A-Za-z_$][A-Za-z0-9_$]*\b", RegexOptions.Multiline);
    private static readonly Regex CSharpBase = new(@"\b(?:class|record|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*([^{\n]+)", RegexOptions.Multiline);
    private static readonly Regex TsBase = new(@"\b(?:class|interface)\s+([A-Za-z_$][A-Za-z0-9_$]*)(?:\s+extends\s+([A-Za-z_$][A-Za-z0-9_$.]*))?(?:\s+implements\s+([^{]+))?", RegexOptions.Multiline);
    private static readonly Regex PythonBase = new(@"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)", RegexOptions.Multiline);
    private static readonly Regex JavaBase = new(@"\b(?:class|interface|record)\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s+extends\s+([A-Za-z_][A-Za-z0-9_.,\s]*))?(?:\s+implements\s+([A-Za-z_][A-Za-z0-9_.,\s]*))?", RegexOptions.Multiline);
    private static readonly Regex RustImpl = new(@"\bimpl\s+(?:(?<trait>[A-Za-z_][A-Za-z0-9_:<>]*)\s+for\s+)?(?<type>[A-Za-z_][A-Za-z0-9_:<>]*)", RegexOptions.Multiline);
    private static readonly Regex GoEmbedded = new(@"\btype\s+([A-Za-z_][A-Za-z0-9_]*)\s+(?:struct|interface)\s*\{([^}]*)\}", RegexOptions.Singleline);
    private static readonly Regex CSharpOverride = new(@"\boverride\s+[A-Za-z_][A-Za-z0-9_<>,\[\]\?]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline);
    private static readonly Regex TsOverride = new(@"\boverride\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(", RegexOptions.Multiline);
    private static readonly Regex JavaOverride = new(@"@Override\s+(?:\r?\n\s*)+(?:public|private|protected|static|\s)*[A-Za-z_][A-Za-z0-9_<>,\[\]]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Multiline);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "and", "as", "async", "await", "base", "break", "case", "catch", "class", "const", "continue", "def", "default",
        "delegate", "do", "else", "enum", "extends", "false", "finally", "for", "foreach", "from", "func", "function", "if",
        "impl", "implements", "import", "in", "interface", "let", "namespace", "new", "null", "override", "package", "private",
        "protected", "public", "return", "static", "struct", "switch", "this", "throw", "trait", "true", "try", "type", "using",
        "var", "virtual", "void", "while", "yield",
    };

    public static CodeStructureDocument Extract(CodeFileIdentity file, string content, ProviderProvenance provenance) =>
        Extract(file, SourceText.FromString(content), provenance);

    public static CodeStructureDocument Extract(CodeFileIdentity file, SourceText source, ProviderProvenance provenance)
    {
        var content = source.Text;
        var pack = TreeSitterQueryRegistry.QueryPacks.GetValueOrDefault(file.Language, SyntacticQueryPack.CallsAndReferences);
        var symbols = ExtractSymbols(file, source, provenance).ToArray();
        var imports = pack.Imports ? ExtractImports(file, source, provenance).ToArray() : [];
        var references = imports
            .Concat(pack.Calls ? ExtractCalls(file, source, symbols, provenance).References : [])
            .Concat(pack.References ? ExtractIdentifierReferences(file, source, symbols, provenance) : [])
            .Concat(pack.Inheritance || pack.Implements ? ExtractTypeRelationshipReferences(file, source, provenance) : [])
            .Concat(pack.Overrides ? ExtractOverrideReferences(file, source, provenance) : [])
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToArray();

        var callFacts = pack.Calls ? ExtractCalls(file, source, symbols, provenance) : GraphFacts.Empty;
        var relationshipFacts = pack.Inheritance || pack.Implements ? ExtractTypeRelationshipEdges(file, source, symbols, provenance) : [];
        var overrideFacts = pack.Overrides ? ExtractOverrideEdges(file, source, symbols, provenance) : [];
        var edges = imports
            .Select(r => new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(file.RelativePath, r.Target, "imports", r.Span.StartByte),
                SourceId = file.RelativePath,
                TargetId = r.Target,
                Kind = "imports",
                SourceSpan = r.Span,
                TargetName = r.Target,
                TargetResolutionStatus = "external-name",
                Provenance = ForFact(provenance, "import"),
            })
            .Concat(symbols.Where(s => s.ParentId is not null).Select(s => new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(s.ParentId!, s.Id, "contains"),
                SourceId = s.ParentId!,
                TargetId = s.Id,
                Kind = "contains",
                Provenance = ForFact(provenance, "containment"),
            }))
            .Concat(callFacts.Edges)
            .Concat(relationshipFacts)
            .Concat(overrideFacts)
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .ToArray();

        return new CodeStructureDocument
        {
            File = file,
            Provenance = provenance,
            Symbols = symbols,
            References = references,
            DependencyEdges = edges,
        };
    }

    private static IEnumerable<CodeSymbol> ExtractSymbols(CodeFileIdentity file, SourceText source, ProviderProvenance provenance)
    {
        var content = source.Text;
        var regex = file.Language switch
        {
            "c-sharp" => CSharpType,
            "typescript" or "tsx" or "javascript" or "jsx" => JsClass,
            "python" => PythonDef,
            "go" => GoSymbol,
            "rust" => RustSymbol,
            "java" => JavaSymbol,
            "c" or "cpp" => CFamilySymbol,
            "bash" => ShellFunction,
            "json" => JsonKey,
            "yaml" => YamlKey,
            "toml" => TomlKey,
            _ => CSharpType,
        };

        foreach (Match match in regex.Matches(content))
        {
            var nameGroup = FirstValueGroup(match);
            if (nameGroup is null)
                continue;
            yield return ToSymbol(file, source, match, nameGroup, InferKind(file.Language, match.Value), provenance);
        }

        if (file.Language is "c-sharp")
        {
            foreach (Match match in CSharpMember.Matches(content))
            {
                var nameGroup = FirstValueGroup(match);
                if (nameGroup is not null)
                    yield return ToSymbol(file, source, match, nameGroup, "method", provenance);
            }
        }
        else if (file.Language is "typescript" or "tsx" or "javascript" or "jsx")
        {
            foreach (Match match in JsFunction.Matches(content))
            {
                var nameGroup = FirstValueGroup(match);
                if (nameGroup is not null)
                    yield return ToSymbol(file, source, match, nameGroup, "function", provenance);
            }
        }
    }

    private static IEnumerable<CodeReference> ExtractImports(CodeFileIdentity file, SourceText source, ProviderProvenance provenance)
    {
        var content = source.Text;
        foreach (Match match in Import.Matches(content))
        {
            var group = FirstValueGroup(match);
            if (group is null)
                continue;

            yield return new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "import", group.Value.Trim(), source.ByteOffsetForChar(group.Index)),
                FilePath = file.RelativePath,
                Kind = "import",
                Target = group.Value.Trim(),
                Span = source.SpanFor(group.Index, group.Length),
                Provenance = ForFact(provenance, "import"),
            };
        }
    }

    private static GraphFacts ExtractCalls(CodeFileIdentity file, SourceText source, IReadOnlyList<CodeSymbol> symbols, ProviderProvenance provenance)
    {
        var content = source.Text;
        var references = new List<CodeReference>();
        var edges = new List<CodeDependencyEdge>();
        var localSymbolsByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        foreach (Match match in Call.Matches(content))
        {
            // Full chain in named group (Helper / Console.WriteLine / System.Console.WriteLine),
            // with optional global:: so Slice 3 can disable namespace-relative ranking.
            var nameGroup = match.Groups["name"];
            var targetName = CollapseDottedIdentifier(nameGroup.Value);
            if (match.Groups["g"].Success)
                targetName = "global::" + targetName;
            targetName = CallTargetNormalization.StripThisBaseReceiver(targetName);

            var leaf = targetName.Contains('.', StringComparison.Ordinal)
                ? targetName[(targetName.LastIndexOf('.') + 1)..]
                : targetName;
            if (leaf.StartsWith("global::", StringComparison.Ordinal))
                leaf = leaf["global::".Length..];
            if (Keywords.Contains(leaf) || Keywords.Contains(targetName) || IsDeclarationLikeCall(content, match.Index))
                continue;

            var isDotted = targetName.Contains('.', StringComparison.Ordinal)
                || targetName.StartsWith("global::", StringComparison.Ordinal);
            var nameStartChar = match.Groups["g"].Success ? match.Groups["g"].Index : nameGroup.Index;
            var nameEndChar = nameGroup.Index + nameGroup.Length;
            var nameLength = nameEndChar - nameStartChar;
            var startByte = source.ByteOffsetForChar(nameStartChar);
            var span = source.SpanFor(nameStartChar, nameLength);
            references.Add(new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "call", targetName, startByte),
                FilePath = file.RelativePath,
                Kind = "call",
                Target = targetName,
                Span = span,
                Provenance = ForFact(provenance, "call", 0.72),
            });

            var sourceSymbol = NearestContainingSymbol(symbols, source.ByteOffsetForChar(match.Index));
            if (sourceSymbol is null)
                continue;

            // Dotted / global:: member-access: no leaf-name local bind (BCL safety).
            var resolution = isDotted
                ? (TargetId: targetName, Status: "external-name")
                : ResolveTarget(targetName, localSymbolsByName);
            edges.Add(new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(sourceSymbol.Id, resolution.TargetId, "calls", startByte),
                SourceId = sourceSymbol.Id,
                TargetId = resolution.TargetId,
                Kind = "calls",
                SourceSpan = span,
                TargetName = targetName,
                TargetResolutionStatus = resolution.Status,
                Provenance = ForFact(provenance, "call", resolution.Status == "local-symbol" ? 0.78 : 0.62),
            });
        }

        return new GraphFacts(references, edges);
    }

    private static IEnumerable<CodeReference> ExtractIdentifierReferences(CodeFileIdentity file, SourceText source, IReadOnlyList<CodeSymbol> symbols, ProviderProvenance provenance)
    {
        var content = source.Text;
        var declarationStarts = symbols.Select(s => s.Span.StartByte).ToHashSet();
        foreach (Match match in Identifier.Matches(content))
        {
            var name = match.Value;
            var startByte = source.ByteOffsetForChar(match.Index);
            if (Keywords.Contains(name) || declarationStarts.Contains(startByte))
                continue;

            yield return new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "identifier", name, startByte),
                FilePath = file.RelativePath,
                Kind = LooksLikeTypeName(name) ? "type-usage" : "identifier",
                Target = name,
                Span = source.SpanFor(match.Index, match.Length),
                Provenance = ForFact(provenance, "identifier-reference", 0.55),
            };
        }
    }

    private static IEnumerable<CodeReference> ExtractTypeRelationshipReferences(CodeFileIdentity file, SourceText source, ProviderProvenance provenance) =>
        ExtractTypeRelationshipCaptures(file.Language, source.Text)
            .Select(c => new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, c.ReferenceKind, c.TargetName, source.ByteOffsetForChar(c.StartChar)),
                FilePath = file.RelativePath,
                Kind = c.ReferenceKind,
                Target = c.TargetName,
                Span = source.SpanFor(c.StartChar, c.TargetName.Length),
                Provenance = ForFact(provenance, "type-relationship", 0.74),
            });

    private static IEnumerable<CodeDependencyEdge> ExtractTypeRelationshipEdges(CodeFileIdentity file, SourceText source, IReadOnlyList<CodeSymbol> symbols, ProviderProvenance provenance)
    {
        var localSymbolsByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var capture in ExtractTypeRelationshipCaptures(file.Language, source.Text))
        {
            var sourceSymbol = symbols.FirstOrDefault(s => s.Name == capture.SourceName);
            if (sourceSymbol is null)
                continue;

            var resolution = ResolveTarget(capture.TargetName, localSymbolsByName);
            var captureStartByte = source.ByteOffsetForChar(capture.StartChar);
            yield return new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(sourceSymbol.Id, resolution.TargetId, capture.EdgeKind, captureStartByte),
                SourceId = sourceSymbol.Id,
                TargetId = resolution.TargetId,
                Kind = capture.EdgeKind,
                SourceSpan = source.SpanFor(capture.StartChar, capture.TargetName.Length),
                TargetName = capture.TargetName,
                TargetResolutionStatus = resolution.Status,
                Provenance = ForFact(provenance, "type-relationship", resolution.Status == "local-symbol" ? 0.79 : 0.68),
            };
        }
    }

    private static IEnumerable<CodeReference> ExtractOverrideReferences(CodeFileIdentity file, SourceText source, ProviderProvenance provenance) =>
        ExtractOverrideCaptures(file.Language, source.Text)
            .Select(c => new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "override", c.TargetName, source.ByteOffsetForChar(c.StartChar)),
                FilePath = file.RelativePath,
                Kind = "override",
                Target = c.TargetName,
                Span = source.SpanFor(c.StartChar, c.TargetName.Length),
                Provenance = ForFact(provenance, "override", 0.76),
            });

    private static IEnumerable<CodeDependencyEdge> ExtractOverrideEdges(CodeFileIdentity file, SourceText source, IReadOnlyList<CodeSymbol> symbols, ProviderProvenance provenance)
    {
        var localSymbolsByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var capture in ExtractOverrideCaptures(file.Language, source.Text))
        {
            var startByte = source.ByteOffsetForChar(capture.StartChar);
            var sourceSymbol = NearestContainingSymbol(symbols, startByte);
            if (sourceSymbol is null)
                continue;

            var resolution = ResolveTarget(capture.TargetName, localSymbolsByName);
            yield return new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(sourceSymbol.Id, resolution.TargetId, "overrides", startByte),
                SourceId = sourceSymbol.Id,
                TargetId = resolution.TargetId,
                Kind = "overrides",
                SourceSpan = source.SpanFor(capture.StartChar, capture.TargetName.Length),
                TargetName = capture.TargetName,
                TargetResolutionStatus = resolution.Status == "local-symbol" ? "local-symbol" : "unresolved",
                Provenance = ForFact(provenance, "override", resolution.Status == "local-symbol" ? 0.78 : 0.64),
            };
        }
    }

    private static CodeSymbol ToSymbol(CodeFileIdentity file, SourceText source, Match match, Group nameGroup, string kind, ProviderProvenance provenance)
    {
        // Regex path has no AST scope/signature. Keep name for readability, but append @startByte
        // so same-name symbols in one file stay unique (avoids SQLite INSERT OR REPLACE loss).
        // C# AST monikers remain pure scope-path (no occurrence key).
        var startByte = source.ByteOffsetForChar(nameGroup.Index);
        var scope = $"{nameGroup.Value}@{startByte}";
        var parameterTypes = kind is "method" or "function" or "constructor" ? "" : null;
        return new CodeSymbol
        {
            Id = CodeStableId.ForSymbol(file.RelativePath, kind, scope, parameterTypes),
            FilePath = file.RelativePath,
            Language = file.Language,
            Name = nameGroup.Value,
            Kind = kind,
            Span = source.SpanFor(match.Index, match.Length),
            Provenance = ForFact(provenance, "symbol-declaration"),
        };
    }

    private static string InferKind(string language, string text)
    {
        if (language is "json" or "yaml" or "toml")
            return "key";
        if (text.Contains("class", StringComparison.Ordinal))
            return "class";
        if (text.Contains("interface", StringComparison.Ordinal))
            return "interface";
        if (text.Contains("struct", StringComparison.Ordinal))
            return "struct";
        if (text.Contains("enum", StringComparison.Ordinal))
            return "enum";
        if (text.Contains("type", StringComparison.Ordinal))
            return "type";
        return language is "bash" ? "function" : "function";
    }

    private static IEnumerable<TypeRelationshipCapture> ExtractTypeRelationshipCaptures(string language, string content)
    {
        if (language is "c-sharp")
        {
            foreach (Match match in CSharpBase.Matches(content))
            {
                var source = match.Groups[1].Value;
                foreach (var (target, index, ordinal) in SplitTypeList(match.Groups[2]))
                    yield return new TypeRelationshipCapture(source, target, ordinal == 0 && !target.StartsWith('I') ? "inherits" : "implements", ordinal == 0 && !target.StartsWith('I') ? "inheritance" : "implementation", index);
            }
        }
        else if (language is "typescript" or "tsx")
        {
            foreach (Match match in TsBase.Matches(content))
            {
                var source = match.Groups[1].Value;
                if (match.Groups[2].Success)
                    yield return new TypeRelationshipCapture(source, CleanTypeName(match.Groups[2].Value), "inherits", "inheritance", match.Groups[2].Index);
                if (match.Groups[3].Success)
                    foreach (var (target, index, _) in SplitTypeList(match.Groups[3]))
                        yield return new TypeRelationshipCapture(source, target, "implements", "implementation", index);
            }
        }
        else if (language is "python")
        {
            foreach (Match match in PythonBase.Matches(content))
                foreach (var (target, index, _) in SplitTypeList(match.Groups[2]))
                    yield return new TypeRelationshipCapture(match.Groups[1].Value, target, "inherits", "inheritance", index);
        }
        else if (language is "java")
        {
            foreach (Match match in JavaBase.Matches(content))
            {
                var source = match.Groups[1].Value;
                if (match.Groups[2].Success)
                    foreach (var (target, index, _) in SplitTypeList(match.Groups[2]))
                        yield return new TypeRelationshipCapture(source, target, "inherits", "inheritance", index);
                if (match.Groups[3].Success)
                    foreach (var (target, index, _) in SplitTypeList(match.Groups[3]))
                        yield return new TypeRelationshipCapture(source, target, "implements", "implementation", index);
            }
        }
        else if (language is "rust")
        {
            foreach (Match match in RustImpl.Matches(content))
                if (match.Groups["trait"].Success)
                    yield return new TypeRelationshipCapture(CleanTypeName(match.Groups["type"].Value), CleanTypeName(match.Groups["trait"].Value), "implements", "implementation", match.Groups["trait"].Index);
        }
        else if (language is "go")
        {
            foreach (Match match in GoEmbedded.Matches(content))
            {
                foreach (Match name in Identifier.Matches(match.Groups[2].Value))
                {
                    if (!Keywords.Contains(name.Value) && LooksLikeTypeName(name.Value))
                        yield return new TypeRelationshipCapture(match.Groups[1].Value, name.Value, "implements", "implementation", match.Groups[2].Index + name.Index);
                }
            }
        }
    }

    private static IEnumerable<ReferenceCapture> ExtractOverrideCaptures(string language, string content)
    {
        var regex = language switch
        {
            "c-sharp" => CSharpOverride,
            "typescript" or "tsx" => TsOverride,
            "java" => JavaOverride,
            _ => null,
        };

        if (regex is null)
            yield break;

        foreach (Match match in regex.Matches(content))
        {
            var group = FirstValueGroup(match);
            if (group is not null)
                yield return new ReferenceCapture(group.Value, group.Index);
        }
    }

    private static IEnumerable<(string Target, int Index, int Ordinal)> SplitTypeList(Group group)
    {
        var ordinal = 0;
        foreach (var part in group.Value.Split(','))
        {
            var cleaned = CleanTypeName(part);
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            var relativeIndex = group.Value.IndexOf(part, StringComparison.Ordinal);
            yield return (cleaned, group.Index + Math.Max(0, relativeIndex + part.IndexOf(cleaned, StringComparison.Ordinal)), ordinal++);
        }
    }

    private static string CleanTypeName(string value)
    {
        var match = Identifier.Match(value.Trim());
        return match.Success ? match.Value : value.Trim();
    }

    private static CodeSymbol? NearestContainingSymbol(IReadOnlyList<CodeSymbol> symbols, int startByte) =>
        symbols
            .Where(s => s.Kind is "function" or "method" or "constructor" && s.Span.StartByte <= startByte)
            .OrderByDescending(s => s.Span.StartByte)
            .FirstOrDefault()
        ?? symbols
            .Where(s => s.Span.StartByte <= startByte)
            .OrderByDescending(s => s.Span.StartByte)
            .FirstOrDefault();

    private static (string TargetId, string Status) ResolveTarget(string targetName, IReadOnlyDictionary<string, CodeSymbol[]> localSymbolsByName)
    {
        if (localSymbolsByName.TryGetValue(targetName, out var matches) && matches.Length == 1)
            return (matches[0].Id, "local-symbol");

        return (targetName, matches is { Length: > 1 } ? "unresolved" : "external-name");
    }

    /// <summary>Collapse whitespace around dots in a member-access chain (<c>A . B</c> → <c>A.B</c>).</summary>
    private static string CollapseDottedIdentifier(string raw)
    {
        var hasWhitespace = false;
        foreach (var c in raw)
        {
            if (!char.IsWhiteSpace(c))
                continue;
            hasWhitespace = true;
            break;
        }

        if (!hasWhitespace)
            return raw;

        var buffer = new char[raw.Length];
        var n = 0;
        foreach (var c in raw)
        {
            if (!char.IsWhiteSpace(c))
                buffer[n++] = c;
        }

        return new string(buffer, 0, n);
    }

    private static bool LooksLikeTypeName(string name) => name.Length > 0 && char.IsUpper(name[0]);

    private static bool IsDeclarationLikeCall(string content, int startByte)
    {
        var lineStart = content.LastIndexOf('\n', Math.Max(0, startByte - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var prefix = content[lineStart..startByte];
        return prefix.Contains("class ", StringComparison.Ordinal)
            || prefix.Contains("interface ", StringComparison.Ordinal)
            || prefix.Contains(" function ", StringComparison.Ordinal)
            || prefix.TrimStart().StartsWith("function ", StringComparison.Ordinal)
            || prefix.TrimStart().StartsWith("def ", StringComparison.Ordinal)
            || prefix.TrimStart().StartsWith("async def ", StringComparison.Ordinal)
            || prefix.Contains(" void ", StringComparison.Ordinal)
            || prefix.Contains(" public ", StringComparison.Ordinal)
            || prefix.Contains(" private ", StringComparison.Ordinal)
            || prefix.Contains(" protected ", StringComparison.Ordinal);
    }

    private static ProviderProvenance ForFact(ProviderProvenance provenance, string factKind, double? confidence = null)
    {
        var requested = confidence ?? provenance.Confidence;
        var clamped = provenance.ProviderId switch
        {
            "hypa-pattern" => Math.Min(requested, 0.79),
            "regex-fallback" => Math.Min(requested, 0.49),
            "markdown" => Math.Min(requested, provenance.Confidence),
            _ => requested,
        };

        return provenance with { FactKind = factKind, Confidence = clamped };
    }

    private static Group? FirstValueGroup(Match match)
    {
        for (var i = 1; i < match.Groups.Count; i++)
        {
            if (match.Groups[i].Success && !string.IsNullOrWhiteSpace(match.Groups[i].Value))
                return match.Groups[i];
        }

        return null;
    }

    private static SourceSpan SpanFor(string content, int start, int length)
    {
        var startPos = LineColumn(content, start);
        var endPos = LineColumn(content, Math.Min(content.Length, start + length));
        return new SourceSpan
        {
            StartLine = startPos.Line,
            StartColumn = startPos.Column,
            EndLine = endPos.Line,
            EndColumn = endPos.Column,
            StartByte = start,
            EndByte = start + length,
        };
    }

    private static (int Line, int Column) LineColumn(string text, int offset)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    private sealed record GraphFacts(IReadOnlyList<CodeReference> References, IReadOnlyList<CodeDependencyEdge> Edges)
    {
        public static GraphFacts Empty { get; } = new([], []);
    }

    private sealed record TypeRelationshipCapture(string SourceName, string TargetName, string EdgeKind, string ReferenceKind, int StartChar);

    private sealed record ReferenceCapture(string TargetName, int StartChar);

    private static readonly Regex MarkdownAtxHeading = new(@"^(#{1,6})\s+([^#\n]+)$", RegexOptions.Multiline);
    private static readonly Regex MarkdownCodeBlock = new(@"```(\w*)\n?([\s\S]*?)```", RegexOptions.Multiline);
    private static readonly Regex MarkdownFrontmatter = new(@"^---\r?\n([\s\S]*?)\r?\n---", RegexOptions.Multiline);
    private static readonly Regex MarkdownFrontmatterKey = new(@"^\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*:\s*(.+)$", RegexOptions.Multiline);
    private static readonly Regex MarkdownIdentifier = new(@"\b[a-zA-Z][a-zA-Z0-9_-]*\b", RegexOptions.Multiline);
    private static readonly Regex MarkdownHeadingLevelAtLine = new(@"^(#{1,6})\s+");
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^\)]+\)", RegexOptions.Multiline);
    /// <summary>Link with capture group 2 = href (for doc-section file references).</summary>
    private static readonly Regex MarkdownLinkWithTarget = new(@"\[([^\]]*)\]\(([^)\s]+)\)", RegexOptions.Multiline);
    private static readonly Regex MarkdownInlineCode = new(@"`([^`]+)`", RegexOptions.Multiline);
    private static readonly Regex MarkdownHeadingMarker = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.Multiline);
    private static readonly Regex MarkdownEmphasisMarkers = new(@"\*\*|\*|__|_", RegexOptions.Multiline);
    private static readonly Regex MarkdownNonAnchor = new(@"[^a-z0-9-]", RegexOptions.Multiline);
    /// <summary>Identifier or dotted qualified name (optional leading underscore per segment).</summary>
    private static readonly Regex DocSymbolName = new(
        @"^_?[A-Za-z][A-Za-z0-9_]*(?:\._?[A-Za-z][A-Za-z0-9_]*)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Bounds for untrusted markdown: prevent O(n²) edge blow-ups on generated docs.
    private const int MaxDocSectionEdgesPerSection = 64;
    private const int MaxDocSectionEdgesPerFile = 256;

    /// <summary>
    /// Common prose tokens that appear in backticks but are not code symbols.
    /// Keywords are already filtered via <see cref="Keywords"/>.
    /// </summary>
    private static readonly HashSet<string> DocProseTokens = new(StringComparer.Ordinal)
    {
        "npm", "npx", "yarn", "pnpm", "dotnet", "docker", "kubectl", "git", "bash", "zsh",
        "true", "false", "null", "none", "note", "todo", "fixme", "hack", "warning", "error",
        "info", "ok", "yes", "no", "on", "off", "http", "https", "json", "yaml", "toml",
        "md", "cs", "ts", "js", "py", "go", "rs", "sh", "cli", "api", "url", "uri", "uuid",
        "id", "ids", "src", "lib", "bin", "obj", "tmp", "log", "logs", "test", "tests",
    };
    private static readonly Regex MarkdownWhitespace = new(@"\s+", RegexOptions.Multiline);
    private static readonly Regex MarkdownCodeFenceLine = new(@"^```.*$", RegexOptions.Multiline);
    private static readonly Regex MarkdownBlankLines = new(@"(\r?\n){3,}", RegexOptions.Multiline);

    /// <summary>
    /// Extracts Markdown structure: headings, code blocks, frontmatter.
    /// </summary>
    public static CodeStructureDocument ExtractMarkdown(CodeFileIdentity file, string content, ProviderProvenance provenance) =>
        ExtractMarkdown(file, SourceText.FromString(content), provenance);

    public static CodeStructureDocument ExtractMarkdown(CodeFileIdentity file, SourceText source, ProviderProvenance provenance)
    {
        var content = source.Text;
        var headings = ExtractMarkdownHeadings(file, source, provenance).ToArray();
        var codeBlocks = ExtractMarkdownCodeBlocks(file, source, provenance).ToArray();
        var symbols = headings.Concat(codeBlocks).ToArray();

        var sections = ExtractMarkdownSections(file, source, headings, provenance);
        var docFacts = ExtractMarkdownDocSectionReferences(file, source, sections, provenance);

        var references = ExtractIdentifierReferencesMarkdown(file, source, provenance)
            .Concat(ExtractMarkdownFrontmatter(file, source, provenance))
            .Concat(docFacts.References)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToArray();

        var edges = ExtractMarkdownEdges(source, symbols)
            .Concat(docFacts.Edges)
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .ToArray();
        var frontmatterYaml = ExtractMarkdownFrontmatterYaml(content);
        var plainText = ToMarkdownPlainText(content, removeFrontmatter: true);

        return new CodeStructureDocument
        {
            File = file,
            Provenance = provenance,
            Symbols = symbols,
            References = references,
            DependencyEdges = edges,
            Sections = sections,
            FrontmatterYaml = frontmatterYaml,
            PlainText = plainText,
        };
    }

    /// <summary>
    /// KG-H2: span-grounded section→symbol/file reference candidates from inline code
    /// and markdown file links. Targets stay unbound here; project-wide resolution runs
    /// in <see cref="CrossFileDependencyResolver"/>.
    /// </summary>
    /// <remarks>
    /// Only heading-bounded <see cref="MarkdownSection"/> rows own edges. Prose before the
    /// first heading (preamble with no ATX headings) intentionally yields no documents edges —
    /// there is no section moniker to attach affinity to without inventing a synthetic page.
    /// Each occurrence is attributed to the <b>innermost</b> containing section only so nested
    /// headings do not double-count the same span.
    /// </remarks>
    private static GraphFacts ExtractMarkdownDocSectionReferences(
        CodeFileIdentity file,
        SourceText source,
        IReadOnlyList<MarkdownSection> sections,
        ProviderProvenance provenance)
    {
        if (sections.Count == 0)
            return GraphFacts.Empty;

        var content = source.Text;
        var fenceMask = BuildMarkdownFenceMask(content);
        // Neutralize fenced regions before the inline-code regex runs so fence-line
        // backticks cannot pair with later prose `` `symbol` `` spans (Regex.Matches is
        // non-overlapping and would otherwise consume the prose opener). Indices stay
        // aligned with the original content for span attribution.
        var inlineScan = content.ToCharArray();
        for (var fi = 0; fi < inlineScan.Length; fi++)
        {
            if (fenceMask[fi])
                inlineScan[fi] = ' ';
        }

        var references = new List<CodeReference>();
        var edges = new List<CodeDependencyEdge>();
        // Dedup by (section, kind, target, startByte) via stable ids.
        var seenEdgeIds = new HashSet<string>(StringComparer.Ordinal);
        // Per-section edge counts for the per-section cap.
        var sectionEdgeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalEdges = 0;

        // 1) Inline code: `SymbolName`, `Namespace.Type.Method` — scan once, own by innermost section.
        foreach (Match match in MarkdownInlineCode.Matches(new string(inlineScan)))
        {
            if (totalEdges >= MaxDocSectionEdgesPerFile)
                break;
            // Defense in depth: skip any match that still touches a fenced index
            // (e.g. cross-fence pairing if newline policy in the symbol normalizer loosens).
            if (MatchOverlapsFenceMask(fenceMask, match.Index, match.Length))
                continue;

            var startByte = source.ByteOffsetForChar(match.Index);
            var section = FindInnermostSection(sections, startByte);
            if (section is null)
                continue;
            sectionEdgeCounts.TryGetValue(section.Id, out var sectionEdges);
            if (sectionEdges >= MaxDocSectionEdgesPerSection)
                continue;

            var raw = match.Groups[1].Value.Trim();
            if (!TryNormalizeDocSymbolName(raw, out var symbolName))
                continue;

            var span = source.SpanFor(match.Index, match.Length);
            var refId = CodeStableId.ForReference(file.RelativePath, "doc-symbol", symbolName, startByte);
            references.Add(new CodeReference
            {
                Id = refId,
                FilePath = file.RelativePath,
                Kind = "doc-symbol",
                Target = symbolName,
                Span = span,
                Provenance = ForFact(provenance, "doc-section-reference", 0.7),
            });

            var edgeId = CodeStableId.ForEdge(section.Id, symbolName, "documents", startByte);
            if (!seenEdgeIds.Add(edgeId))
                continue;

            edges.Add(new CodeDependencyEdge
            {
                Id = edgeId,
                SourceId = section.Id,
                TargetId = symbolName,
                Kind = "documents",
                SourceSpan = span,
                TargetName = symbolName,
                TargetResolutionStatus = "unresolved",
                Provenance = ForFact(provenance, "doc-section-reference", 0.7),
            });
            sectionEdgeCounts[section.Id] = sectionEdges + 1;
            totalEdges++;
        }

        // 2) Markdown file links: [text](path/to/File.cs) — not images ![alt](…).
        foreach (Match match in MarkdownLinkWithTarget.Matches(content))
        {
            if (totalEdges >= MaxDocSectionEdgesPerFile)
                break;
            if (fenceMask[match.Index])
                continue;
            // Image syntax: ![alt](href) — '!' immediately before '['.
            if (match.Index > 0 && content[match.Index - 1] == '!')
                continue;

            var startByte = source.ByteOffsetForChar(match.Index);
            var section = FindInnermostSection(sections, startByte);
            if (section is null)
                continue;
            sectionEdgeCounts.TryGetValue(section.Id, out var sectionEdges);
            if (sectionEdges >= MaxDocSectionEdgesPerSection)
                continue;

            var href = match.Groups[2].Value.Trim();
            if (!TryNormalizeDocFileLink(file.RelativePath, href, out var relativeTarget))
                continue;
            // Only code/indexable paths — skip bare anchors and non-language files.
            if (CodeLanguageRegistry.GetLanguage(relativeTarget) is null)
                continue;

            var span = source.SpanFor(match.Index, match.Length);
            var refId = CodeStableId.ForReference(file.RelativePath, "doc-file", relativeTarget, startByte);
            references.Add(new CodeReference
            {
                Id = refId,
                FilePath = file.RelativePath,
                Kind = "doc-file",
                Target = relativeTarget,
                Span = span,
                Provenance = ForFact(provenance, "doc-section-reference", 0.72),
            });

            var edgeId = CodeStableId.ForEdge(section.Id, relativeTarget, "documents", startByte);
            if (!seenEdgeIds.Add(edgeId))
                continue;

            edges.Add(new CodeDependencyEdge
            {
                Id = edgeId,
                SourceId = section.Id,
                TargetId = relativeTarget,
                Kind = "documents",
                SourceSpan = span,
                TargetName = relativeTarget,
                TargetResolutionStatus = "unresolved",
                Provenance = ForFact(provenance, "doc-section-reference", 0.72),
            });
            sectionEdgeCounts[section.Id] = sectionEdges + 1;
            totalEdges++;
        }

        return new GraphFacts(references, edges);
    }

    /// <summary>
    /// Innermost section containing <paramref name="bytePos"/>: among sections with
    /// <c>StartByte ≤ bytePos &lt; EndByte</c>, the one with the greatest <c>StartByte</c>
    /// (child headings start later than their parents).
    /// </summary>
    private static MarkdownSection? FindInnermostSection(IReadOnlyList<MarkdownSection> sections, int bytePos)
    {
        MarkdownSection? best = null;
        foreach (var section in sections)
        {
            if (bytePos < section.StartByte || bytePos >= section.EndByte)
                continue;
            if (best is null || section.StartByte > best.StartByte)
                best = section;
        }

        return best;
    }

    /// <summary>
    /// True when any character index in <paramref name="start"/>..<paramref name="start"/>+length
    /// lies inside a fenced region.
    /// </summary>
    private static bool MatchOverlapsFenceMask(bool[] fenceMask, int start, int length)
    {
        var end = start + length;
        if (start < 0 || length <= 0 || start >= fenceMask.Length)
            return false;
        if (end > fenceMask.Length)
            end = fenceMask.Length;
        for (var i = start; i < end; i++)
        {
            if (fenceMask[i])
                return true;
        }

        return false;
    }

    /// <summary>
    /// True for each character index that lies inside a fenced code block body
    /// (including the fence lines). Recognizes CommonMark <c>```</c> and <c>~~~</c>
    /// fences (three or more of the same delimiter, ≤3 leading U+0020 spaces).
    /// Closing requires the same delimiter character, a run length <b>at least</b> as
    /// long as the opening fence, and only spaces after the run. Opening backtick
    /// fences reject info strings that contain backticks. Used to suppress false
    /// doc-symbol edges from examples.
    /// </summary>
    private static bool[] BuildMarkdownFenceMask(string content)
    {
        var mask = new bool[content.Length];
        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;
        var i = 0;
        while (i < content.Length)
        {
            var lineStart = i;
            var lineEnd = content.IndexOf('\n', i);
            if (lineEnd < 0)
                lineEnd = content.Length;
            var line = content.AsSpan(lineStart, lineEnd - lineStart);
            // Strip trailing CR for CRLF files.
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

            if (TryParseFenceLine(line, inFence, fenceChar, fenceLength,
                    out var delim, out var delimLen, out var isClose))
            {
                // Opening fence locks delimiter char + run length; closing requires the
                // same char, length >= opening, and spaces-only after the run.
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = delim;
                    fenceLength = delimLen;
                }
                else if (isClose)
                {
                    inFence = false;
                    fenceChar = '\0';
                    fenceLength = 0;
                }

                for (var k = lineStart; k < lineEnd && k < mask.Length; k++)
                    mask[k] = true;
            }
            else if (inFence)
            {
                for (var k = lineStart; k < lineEnd && k < mask.Length; k++)
                    mask[k] = true;
            }

            i = lineEnd < content.Length ? lineEnd + 1 : content.Length;
        }

        return mask;
    }

    /// <summary>
    /// CommonMark fence line parser. Opening: ≤3 leading spaces, 3+ <c>`</c>/<c>~</c>,
    /// optional info string (no <c>`</c> in info when delimiter is backtick). Closing
    /// (when <paramref name="inFence"/>): same char, length ≥ open, only U+0020 after the run.
    /// </summary>
    private static bool TryParseFenceLine(
        ReadOnlySpan<char> line,
        bool inFence,
        char openFenceChar,
        int openFenceLength,
        out char delimiter,
        out int length,
        out bool isClose)
    {
        delimiter = '\0';
        length = 0;
        isClose = false;

        // CommonMark: fence marker may be indented by at most three spaces.
        var spaces = 0;
        while (spaces < line.Length && line[spaces] == ' ')
            spaces++;
        if (spaces > 3)
            return false;

        var rest = line[spaces..];
        if (rest.Length < 3)
            return false;
        var c = rest[0];
        if (c is not ('`' or '~'))
            return false;
        var n = 0;
        while (n < rest.Length && rest[n] == c)
            n++;
        if (n < 3)
            return false;

        var after = rest[n..];

        if (inFence)
        {
            // Closing fence: same character, length >= opening, only spaces after the run.
            if (c != openFenceChar || n < openFenceLength)
                return false;
            for (var j = 0; j < after.Length; j++)
            {
                if (after[j] != ' ')
                    return false;
            }

            delimiter = c;
            length = n;
            isClose = true;
            return true;
        }

        // Opening fence: backtick info strings must not contain backticks (CommonMark).
        if (c == '`')
        {
            for (var j = 0; j < after.Length; j++)
            {
                if (after[j] == '`')
                    return false;
            }
        }

        delimiter = c;
        length = n;
        isClose = false;
        return true;
    }

    /// <summary>
    /// Accept identifier-like or dotted qualified names from inline code; reject prose,
    /// paths, URLs, and multi-token fragments. Controlled signature forms
    /// (<c>Run(string)</c>, <c>Corpus.Worker.Run(int)</c>, <c>Foo(Dictionary&lt;string,int&gt;)</c>)
    /// are preserved so overload/ctor disambiguation is reachable from markdown. Empty
    /// <c>()</c> is still stripped as a prose call suffix (<c>Run()</c> → <c>Run</c>).
    /// </summary>
    private static bool TryNormalizeDocSymbolName(string raw, out string symbolName)
    {
        symbolName = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        // Reject multi-line / multi-token prose / path-like / URL-like fragments.
        // Spaces inside a trailing signature list are handled below (not free-form prose).
        if (trimmed.Length is < 2 or > 256)
            return false;
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
            return false;
        if (trimmed.Contains("://", StringComparison.Ordinal) || trimmed.Contains('/') || trimmed.Contains('\\'))
            return false;
        if (trimmed.StartsWith('@') || trimmed.StartsWith('#'))
            return false;

        string? signatureParams = null;
        var open = trimmed.IndexOf('(');
        if (open > 0 && trimmed.EndsWith(')'))
        {
            var inside = trimmed[(open + 1)..^1];
            var namePart = trimmed[..open];
            // Spaces outside the signature list remain invalid (e.g. `Call Run(string)`).
            if (namePart.Contains(' '))
                return false;

            if (inside.Length == 0)
            {
                // Prose call suffix: `Run()` → Run (do not pin parameterless moniker).
                trimmed = namePart;
            }
            else if (TryNormalizeDocParameterList(inside, out var normalizedParams))
            {
                signatureParams = normalizedParams;
                trimmed = namePart;
            }
            else
            {
                return false;
            }
        }
        else if (trimmed.Contains(' '))
        {
            return false;
        }

        // Strip simple generic arity on the name only: Foo<T> / Foo<int,string>
        // (parameter-list generics are preserved inside signatureParams).
        var lt = trimmed.IndexOf('<');
        if (lt > 0 && trimmed.EndsWith('>'))
            trimmed = trimmed[..lt];

        if (!DocSymbolName.IsMatch(trimmed))
            return false;

        // Keywords and pure-lowercase single tokens are usually prose, not symbols.
        // Qualified names (A.B) and PascalCase/camelCase identifiers remain.
        if (!trimmed.Contains('.', StringComparison.Ordinal))
        {
            if (Keywords.Contains(trimmed))
                return false;
            if (DocProseTokens.Contains(trimmed))
                return false;
            // Require at least one uppercase letter OR a leading underscore style (_field),
            // so `npm`/`true`/`note` stay out while `Worker`/`runWorker`/`_count` remain.
            if (trimmed[0] != '_' && !trimmed.Any(char.IsUpper))
                return false;
        }

        symbolName = signatureParams is null ? trimmed : $"{trimmed}({signatureParams})";
        return true;
    }

    /// <summary>
    /// Validate and normalize a moniker-style parameter-type list from doc prose.
    /// Allows identifier-like / dotted type tokens, nested generics, and optional <c>[]</c>;
    /// collapses whitespace. Rejects paths, schemes, mid-token spaces (<c>foo bar</c>),
    /// and other dangerous shapes.
    /// </summary>
    private static bool TryNormalizeDocParameterList(string inside, out string normalized)
    {
        normalized = string.Empty;
        if (inside.Length is 0 or > 200)
            return false;

        // Reject path/scheme/injection characters; allow only type-token alphabet + whitespace.
        for (var i = 0; i < inside.Length; i++)
        {
            var c = inside[i];
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or ',' or '<' or '>' or '[' or ']')
                continue;
            if (c is ' ' or '\t')
            {
                // Whitespace only as a separator next to structural punctuation (comma/brackets).
                // Reject mid-identifier spaces like `foo bar` that would collapse into one token.
                var leftOk = i > 0 && IsDocParamListStructural(inside[i - 1]);
                var rightOk = i + 1 < inside.Length && IsDocParamListStructural(inside[i + 1]);
                if (leftOk || rightOk)
                    continue;
                return false;
            }

            return false;
        }

        var compact = string.Concat(inside.Where(static c => !char.IsWhiteSpace(c)));
        if (compact.Length == 0 || compact.Length > 200)
            return false;
        if (!IsValidDocTypeList(compact))
            return false;

        normalized = compact;
        return true;
    }

    private static bool IsDocParamListStructural(char c) =>
        c is ',' or '<' or '>' or '[' or ']';

    /// <summary>Top-level comma-separated type list with balanced angle brackets.</summary>
    private static bool IsValidDocTypeList(string compact)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < compact.Length; i++)
        {
            var c = compact[i];
            switch (c)
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    if (depth < 0)
                        return false;
                    break;
                case ',' when depth == 0:
                    if (!IsValidDocType(compact[start..i]))
                        return false;
                    start = i + 1;
                    break;
            }
        }

        return depth == 0 && IsValidDocType(compact[start..]);
    }

    /// <summary>
    /// Single type token: qualified name, optional nested generic args, optional trailing arrays.
    /// </summary>
    private static bool IsValidDocType(string type)
    {
        if (type.Length == 0)
            return false;

        while (type.EndsWith("[]", StringComparison.Ordinal))
            type = type[..^2];
        if (type.Length == 0)
            return false;

        var lt = type.IndexOf('<');
        if (lt >= 0)
        {
            if (!type.EndsWith('>') || lt == 0)
                return false;
            var name = type[..lt];
            var args = type[(lt + 1)..^1];
            return DocSymbolName.IsMatch(name) && args.Length > 0 && IsValidDocTypeList(args);
        }

        return DocSymbolName.IsMatch(type);
    }

    /// <summary>
    /// Normalize a markdown link href to a workspace-relative path. Rejects schemes,
    /// absolute paths, and any traversal that escapes the workspace root.
    /// </summary>
    private static bool TryNormalizeDocFileLink(string markdownRelativePath, string href, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(href))
            return false;

        var t = href.Trim();
        // Angle-bracket autolinks are not produced by our link regex; still strip wrappers.
        if (t.StartsWith('<') && t.EndsWith('>') && t.Length > 2)
            t = t[1..^1].Trim();

        // Drop fragment / query (File.cs#L10 → File.cs).
        var hash = t.IndexOf('#');
        if (hash >= 0)
            t = t[..hash];
        var query = t.IndexOf('?');
        if (query >= 0)
            t = t[..query];
        t = t.Trim();
        if (t.Length is 0 or > 512)
            return false;

        // External / protocol-relative / absolute — never emit.
        if (t.Contains("://", StringComparison.Ordinal))
            return false;
        if (t.StartsWith("//", StringComparison.Ordinal))
            return false;
        if (t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        // Unix absolute or Windows drive-absolute.
        if (t.StartsWith('/') || t.StartsWith('\\'))
            return false;
        if (t.Length >= 3 && char.IsLetter(t[0]) && t[1] == ':' && (t[2] == '/' || t[2] == '\\'))
            return false;

        // Percent-decoding is intentionally not applied — keep path bytes deterministic
        // and avoid %2e%2e traversal tricks.
        if (t.Contains('%', StringComparison.Ordinal))
            return false;

        var segments = new List<string>();
        var mdNorm = markdownRelativePath.Replace('\\', '/');
        var slash = mdNorm.LastIndexOf('/');
        if (slash >= 0)
        {
            foreach (var part in mdNorm[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries))
                segments.Add(part);
        }

        foreach (var part in t.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;
            if (part == "..")
            {
                // Escape past workspace root → reject (path traversal).
                if (segments.Count == 0)
                    return false;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            // Reject empty/odd segments and anything that looks like a scheme mid-path.
            if (part.Contains(':') || part is "~" || part.Contains('\0'))
                return false;
            segments.Add(part);
        }

        if (segments.Count == 0)
            return false;

        relativePath = string.Join('/', segments);
        return true;
    }

    private static IReadOnlyList<MarkdownSection> ExtractMarkdownSections(CodeFileIdentity file, SourceText source, IReadOnlyList<CodeSymbol> headings, ProviderProvenance provenance)
    {
        var content = source.Text;
        var ordered = headings.OrderBy(h => h.Span.StartByte).ToList();
        var sections = new List<MarkdownSection>(ordered.Count);
        var pathCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < ordered.Count; i++)
        {
            var heading = ordered[i];
            var headingStartChar = source.CharOffsetForByte(heading.Span.StartByte);
            var headingLevel = GetMarkdownHeadingLevelAtOffset(source, heading.Span.StartByte);
            var endBoundary = content.Length;
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var next = ordered[j];
                var nextLevel = GetMarkdownHeadingLevelAtOffset(source, next.Span.StartByte);
                if (nextLevel <= headingLevel)
                {
                    endBoundary = source.CharOffsetForByte(next.Span.StartByte);
                    break;
                }
            }

            var headingPath = BuildMarkdownHeadingPath(source, ordered, heading);
            var sectionText = content[headingStartChar..Math.Max(headingStartChar, endBoundary)];
            var endPos = LineColumn(content, endBoundary);

            // Match heading moniker disambiguation for repeated hierarchical paths.
            pathCounts.TryGetValue(headingPath, out var seen);
            pathCounts[headingPath] = seen + 1;
            var sectionScope = seen == 0 ? headingPath : $"{headingPath}#{seen}";

            sections.Add(new MarkdownSection
            {
                Id = CodeStableId.ForSymbol(file.RelativePath, "section", sectionScope),
                FilePath = file.RelativePath,
                HeadingText = heading.Name,
                HeadingLevel = headingLevel,
                HeadingPath = headingPath,
                HeadingAnchor = ToMarkdownAnchor(heading.Name),
                StartLine = heading.Span.StartLine,
                EndLine = endPos.Line,
                StartByte = heading.Span.StartByte,
                EndByte = source.ByteOffsetForChar(endBoundary),
                Text = sectionText,
                PlainText = ToMarkdownPlainText(sectionText, removeFrontmatter: false),
                Provenance = ForFact(provenance, "markdown-section"),
            });
        }

        return sections;
    }

    private static string BuildMarkdownHeadingPath(SourceText source, IReadOnlyList<CodeSymbol> allHeadings, CodeSymbol heading)
    {
        var stack = new Stack<string>();
        var cursor = heading;
        while (cursor is not null)
        {
            stack.Push(cursor.Name);
            cursor = FindClosestAncestor(source, allHeadings, cursor);
        }

        return string.Join('/', stack);
    }

    private static string ToMarkdownAnchor(string headingText)
    {
        var lower = headingText.ToLowerInvariant().Trim();
        var hyphenated = MarkdownWhitespace.Replace(lower, "-");
        var filtered = MarkdownNonAnchor.Replace(hyphenated, string.Empty);
        return filtered.Trim('-');
    }

    private static string? ExtractMarkdownFrontmatterYaml(string content)
    {
        var match = MarkdownFrontmatter.Match(content);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string ToMarkdownPlainText(string markdown, bool removeFrontmatter)
    {
        var text = markdown;
        if (removeFrontmatter)
            text = MarkdownFrontmatter.Replace(text, string.Empty);

        text = MarkdownHeadingMarker.Replace(text, string.Empty);
        text = MarkdownCodeFenceLine.Replace(text, string.Empty);
        text = MarkdownLink.Replace(text, "$1");
        text = MarkdownInlineCode.Replace(text, "$1");
        text = MarkdownEmphasisMarkers.Replace(text, string.Empty);
        text = MarkdownBlankLines.Replace(text, Environment.NewLine + Environment.NewLine);

        return text.Trim();
    }

    private static IEnumerable<CodeSymbol> ExtractMarkdownHeadings(CodeFileIdentity file, SourceText source, ProviderProvenance provenance)
    {
        var content = source.Text;
        // Provisional symbols keyed by span; Ids are rewritten once heading paths are known so
        // monikers are path-stable and FindClosestAncestor can match by span (not raw name).
        var drafts = new List<CodeSymbol>();
        foreach (Match match in MarkdownAtxHeading.Matches(content))
        {
            var headingText = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(headingText))
                continue;

            drafts.Add(new CodeSymbol
            {
                // Temporary span-unique id used only while building the ancestor chain.
                Id = $"tmp_{source.ByteOffsetForChar(match.Index)}",
                FilePath = file.RelativePath,
                Language = file.Language,
                Name = headingText,
                Kind = "heading",
                Span = source.SpanFor(match.Index, match.Length),
                Provenance = ForFact(provenance, "markdown-heading", 0.85),
            });
        }

        var ordered = drafts.OrderBy(h => h.Span.StartByte).ToList();
        var pathCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var heading in ordered)
        {
            var headingPath = BuildMarkdownHeadingPath(source, ordered, heading);
            // Unique hierarchical paths stay moniker-stable; repeated sibling paths get an
            // occurrence ordinal (not raw startByte) so duplicate headings remain distinct.
            pathCounts.TryGetValue(headingPath, out var seen);
            pathCounts[headingPath] = seen + 1;
            var scope = seen == 0 ? headingPath : $"{headingPath}#{seen}";
            yield return heading with
            {
                Id = CodeStableId.ForSymbol(file.RelativePath, "heading", scope),
            };
        }
    }

    private static IEnumerable<CodeSymbol> ExtractMarkdownCodeBlocks(CodeFileIdentity file, SourceText source, ProviderProvenance provenance)
    {
        var content = source.Text;
        foreach (Match match in MarkdownCodeBlock.Matches(content))
        {
            var languageInfo = match.Groups[1].Value.Trim();
            var blockLanguage = string.IsNullOrWhiteSpace(languageInfo) ? "unknown" : languageInfo;
            var representativeName = blockLanguage == "unknown" ? "code-block" : $"{blockLanguage}-block";
            // Occurrence-level: code blocks share a representative name, so include start byte
            // in the moniker scope (not a stability target for Slice 2).
            var startByte = source.ByteOffsetForChar(match.Index);
            var scope = $"{representativeName}@{startByte}";

            yield return new CodeSymbol
            {
                Id = CodeStableId.ForSymbol(file.RelativePath, "code-block", scope),
                FilePath = file.RelativePath,
                Language = file.Language,
                Name = representativeName,
                Kind = "code-block",
                Span = source.SpanFor(match.Index, match.Length),
                Provenance = ForFact(provenance, "markdown-code-block", 0.8),
            };
        }
    }

    private static IEnumerable<CodeReference> ExtractMarkdownFrontmatter(CodeFileIdentity file, SourceText source, ProviderProvenance provenance)
    {
        var content = source.Text;
        var frontmatterMatch = MarkdownFrontmatter.Match(content);
        if (!frontmatterMatch.Success)
            yield break;

        var yamlContent = frontmatterMatch.Groups[1].Value;
        foreach (Match keyMatch in MarkdownFrontmatterKey.Matches(yamlContent))
        {
            var key = keyMatch.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var startChar = frontmatterMatch.Index + keyMatch.Index;
            yield return new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "frontmatter", key, source.ByteOffsetForChar(startChar)),
                FilePath = file.RelativePath,
                Kind = "frontmatter",
                Target = key,
                Span = source.SpanFor(startChar, keyMatch.Length),
                Provenance = ForFact(provenance, "frontmatter", 0.85),
            };
        }
    }

    private static IEnumerable<CodeReference> ExtractIdentifierReferencesMarkdown(CodeFileIdentity file, SourceText source, ProviderProvenance provenance)
    {
        var content = source.Text;
        var declarationIndices = ExtractMarkdownHeadings(file, source, provenance)
            .Select(h => h.Span.StartByte)
            .ToHashSet();

        foreach (Match match in MarkdownIdentifier.Matches(content))
        {
            var name = match.Value;
            var startByte = source.ByteOffsetForChar(match.Index);
            if (declarationIndices.Contains(startByte))
                continue;

            if (name.Length > 1 && char.IsLower(name[0]))
            {
                yield return new CodeReference
                {
                    Id = CodeStableId.ForReference(file.RelativePath, "identifier", name, startByte),
                    FilePath = file.RelativePath,
                    Kind = "identifier",
                    Target = name,
                    Span = source.SpanFor(match.Index, match.Length),
                    Provenance = ForFact(provenance, "identifier-reference", 0.5),
                };
            }
        }
    }

    private static IEnumerable<CodeDependencyEdge> ExtractMarkdownEdges(SourceText source, IReadOnlyList<CodeSymbol> symbols)
    {
        var headingSymbols = symbols.Where(s => s.Kind == "heading").OrderBy(s => s.Span.StartByte).ToList();

        var byLevel = headingSymbols
            .GroupBy(s => GetMarkdownHeadingLevelAtOffset(source, s.Span.StartByte))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var levelGroup in byLevel.Where(g => g.Key > 1))
        {
            foreach (var heading in levelGroup.Value)
            {
                var ancestor = FindClosestAncestor(source, headingSymbols, heading);
                if (ancestor is null)
                    continue;

                yield return new CodeDependencyEdge
                {
                    Id = CodeStableId.ForEdge(ancestor.Id, heading.Id, "child-of", heading.Span.StartByte),
                    SourceId = ancestor.Id,
                    TargetId = heading.Id,
                    Kind = "child-of",
                    Provenance = heading.Provenance,
                };
            }
        }
    }

    private static CodeSymbol? FindClosestAncestor(SourceText source, IReadOnlyList<CodeSymbol> allHeadings, CodeSymbol child)
    {
        // Match by span so hierarchy wiring works before/after moniker id assignment and
        // remains correct when two headings share the same display text.
        var childIndex = -1;
        for (var i = 0; i < allHeadings.Count; i++)
        {
            if (allHeadings[i].Span.StartByte == child.Span.StartByte)
            {
                childIndex = i;
                break;
            }
        }

        if (childIndex <= 0)
            return null;

        var childLevel = GetMarkdownHeadingLevelAtOffset(source, child.Span.StartByte);

        for (var i = childIndex - 1; i >= 0; i--)
        {
            var ancestor = allHeadings[i];
            var ancestorLevel = GetMarkdownHeadingLevelAtOffset(source, ancestor.Span.StartByte);
            if (ancestorLevel < childLevel)
                return ancestor;
        }

        return null;
    }

    private static int GetMarkdownHeadingLevelAtOffset(SourceText source, int startByte)
    {
        var content = source.Text;
        var startChar = source.CharOffsetForByte(startByte);
        var lineEnd = content.IndexOf('\n', Math.Max(0, startChar));
        var line = lineEnd >= 0
            ? content[startChar..lineEnd]
            : content[startChar..];

        var match = MarkdownHeadingLevelAtLine.Match(line);
        return match.Success ? match.Groups[1].Value.Length : 1;
    }
}
