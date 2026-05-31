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
    private static readonly Regex Call = new(@"\b([A-Za-z_$][A-Za-z0-9_$]*)\s*(?:\.\s*([A-Za-z_$][A-Za-z0-9_$]*))?\s*\(", RegexOptions.Multiline);
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

    public static CodeStructureDocument Extract(CodeFileIdentity file, string content, ProviderProvenance provenance)
    {
        var pack = TreeSitterQueryRegistry.QueryPacks.GetValueOrDefault(file.Language, SyntacticQueryPack.CallsAndReferences);
        var symbols = ExtractSymbols(file, content, provenance).ToArray();
        var imports = pack.Imports ? ExtractImports(file, content, provenance).ToArray() : [];
        var references = imports
            .Concat(pack.Calls ? ExtractCalls(file, content, symbols, provenance).References : [])
            .Concat(pack.References ? ExtractIdentifierReferences(file, content, symbols, provenance) : [])
            .Concat(pack.Inheritance || pack.Implements ? ExtractTypeRelationshipReferences(file, content, provenance) : [])
            .Concat(pack.Overrides ? ExtractOverrideReferences(file, content, provenance) : [])
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToArray();

        var callFacts = pack.Calls ? ExtractCalls(file, content, symbols, provenance) : GraphFacts.Empty;
        var relationshipFacts = pack.Inheritance || pack.Implements ? ExtractTypeRelationshipEdges(file, content, symbols, provenance) : [];
        var overrideFacts = pack.Overrides ? ExtractOverrideEdges(file, content, symbols, provenance) : [];
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
                Provenance = provenance,
            })
            .Concat(symbols.Where(s => s.ParentId is not null).Select(s => new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(s.ParentId!, s.Id, "contains"),
                SourceId = s.ParentId!,
                TargetId = s.Id,
                Kind = "contains",
                Provenance = provenance,
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

    private static IEnumerable<CodeSymbol> ExtractSymbols(CodeFileIdentity file, string content, ProviderProvenance provenance)
    {
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
            yield return ToSymbol(file, content, match, nameGroup, InferKind(file.Language, match.Value), provenance);
        }

        if (file.Language is "c-sharp")
        {
            foreach (Match match in CSharpMember.Matches(content))
            {
                var nameGroup = FirstValueGroup(match);
                if (nameGroup is not null)
                    yield return ToSymbol(file, content, match, nameGroup, "method", provenance);
            }
        }
        else if (file.Language is "typescript" or "tsx" or "javascript" or "jsx")
        {
            foreach (Match match in JsFunction.Matches(content))
            {
                var nameGroup = FirstValueGroup(match);
                if (nameGroup is not null)
                    yield return ToSymbol(file, content, match, nameGroup, "function", provenance);
            }
        }
    }

    private static IEnumerable<CodeReference> ExtractImports(CodeFileIdentity file, string content, ProviderProvenance provenance)
    {
        foreach (Match match in Import.Matches(content))
        {
            var group = FirstValueGroup(match);
            if (group is null)
                continue;

            yield return new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "import", group.Value.Trim(), group.Index),
                FilePath = file.RelativePath,
                Kind = "import",
                Target = group.Value.Trim(),
                Span = SpanFor(content, group.Index, group.Length),
                Provenance = provenance,
            };
        }
    }

    private static GraphFacts ExtractCalls(CodeFileIdentity file, string content, IReadOnlyList<CodeSymbol> symbols, ProviderProvenance provenance)
    {
        var references = new List<CodeReference>();
        var edges = new List<CodeDependencyEdge>();
        var localSymbolsByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        foreach (Match match in Call.Matches(content))
        {
            var nameGroup = match.Groups[2].Success ? match.Groups[2] : match.Groups[1];
            var targetName = nameGroup.Value;
            if (Keywords.Contains(targetName) || IsDeclarationLikeCall(content, match.Index))
                continue;

            var span = SpanFor(content, nameGroup.Index, nameGroup.Length);
            references.Add(new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "call", targetName, nameGroup.Index),
                FilePath = file.RelativePath,
                Kind = "call",
                Target = targetName,
                Span = span,
                Provenance = provenance with { Confidence = Math.Min(provenance.Confidence, 0.72) },
            });

            var source = NearestContainingSymbol(symbols, match.Index);
            if (source is null)
                continue;

            var resolution = ResolveTarget(targetName, localSymbolsByName);
            edges.Add(new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(source.Id, resolution.TargetId, "calls", nameGroup.Index),
                SourceId = source.Id,
                TargetId = resolution.TargetId,
                Kind = "calls",
                SourceSpan = span,
                TargetName = targetName,
                TargetResolutionStatus = resolution.Status,
                Provenance = provenance with { Confidence = resolution.Status == "local-symbol" ? 0.78 : 0.62 },
            });
        }

        return new GraphFacts(references, edges);
    }

    private static IEnumerable<CodeReference> ExtractIdentifierReferences(CodeFileIdentity file, string content, IReadOnlyList<CodeSymbol> symbols, ProviderProvenance provenance)
    {
        var declarationStarts = symbols.Select(s => s.Span.StartByte).ToHashSet();
        foreach (Match match in Identifier.Matches(content))
        {
            var name = match.Value;
            if (Keywords.Contains(name) || declarationStarts.Contains(match.Index))
                continue;

            yield return new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "identifier", name, match.Index),
                FilePath = file.RelativePath,
                Kind = LooksLikeTypeName(name) ? "type-usage" : "identifier",
                Target = name,
                Span = SpanFor(content, match.Index, match.Length),
                Provenance = provenance with { Confidence = Math.Min(provenance.Confidence, 0.55) },
            };
        }
    }

    private static IEnumerable<CodeReference> ExtractTypeRelationshipReferences(CodeFileIdentity file, string content, ProviderProvenance provenance) =>
        ExtractTypeRelationshipCaptures(file.Language, content)
            .Select(c => new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, c.ReferenceKind, c.TargetName, c.StartByte),
                FilePath = file.RelativePath,
                Kind = c.ReferenceKind,
                Target = c.TargetName,
                Span = SpanFor(content, c.StartByte, c.TargetName.Length),
                Provenance = provenance with { Confidence = 0.74 },
            });

    private static IEnumerable<CodeDependencyEdge> ExtractTypeRelationshipEdges(CodeFileIdentity file, string content, IReadOnlyList<CodeSymbol> symbols, ProviderProvenance provenance)
    {
        var localSymbolsByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var capture in ExtractTypeRelationshipCaptures(file.Language, content))
        {
            var source = symbols.FirstOrDefault(s => s.Name == capture.SourceName);
            if (source is null)
                continue;

            var resolution = ResolveTarget(capture.TargetName, localSymbolsByName);
            yield return new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(source.Id, resolution.TargetId, capture.EdgeKind, capture.StartByte),
                SourceId = source.Id,
                TargetId = resolution.TargetId,
                Kind = capture.EdgeKind,
                SourceSpan = SpanFor(content, capture.StartByte, capture.TargetName.Length),
                TargetName = capture.TargetName,
                TargetResolutionStatus = resolution.Status,
                Provenance = provenance with { Confidence = resolution.Status == "local-symbol" ? 0.8 : 0.68 },
            };
        }
    }

    private static IEnumerable<CodeReference> ExtractOverrideReferences(CodeFileIdentity file, string content, ProviderProvenance provenance) =>
        ExtractOverrideCaptures(file.Language, content)
            .Select(c => new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "override", c.TargetName, c.StartByte),
                FilePath = file.RelativePath,
                Kind = "override",
                Target = c.TargetName,
                Span = SpanFor(content, c.StartByte, c.TargetName.Length),
                Provenance = provenance with { Confidence = 0.76 },
            });

    private static IEnumerable<CodeDependencyEdge> ExtractOverrideEdges(CodeFileIdentity file, string content, IReadOnlyList<CodeSymbol> symbols, ProviderProvenance provenance)
    {
        var localSymbolsByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var capture in ExtractOverrideCaptures(file.Language, content))
        {
            var source = NearestContainingSymbol(symbols, capture.StartByte);
            if (source is null)
                continue;

            var resolution = ResolveTarget(capture.TargetName, localSymbolsByName);
            yield return new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(source.Id, resolution.TargetId, "overrides", capture.StartByte),
                SourceId = source.Id,
                TargetId = resolution.TargetId,
                Kind = "overrides",
                SourceSpan = SpanFor(content, capture.StartByte, capture.TargetName.Length),
                TargetName = capture.TargetName,
                TargetResolutionStatus = resolution.Status == "local-symbol" ? "local-symbol" : "unresolved",
                Provenance = provenance with { Confidence = resolution.Status == "local-symbol" ? 0.78 : 0.64 },
            };
        }
    }

    private static CodeSymbol ToSymbol(CodeFileIdentity file, string content, Match match, Group nameGroup, string kind, ProviderProvenance provenance) =>
        new()
        {
            Id = CodeStableId.ForSymbol(file.RelativePath, kind, nameGroup.Value, nameGroup.Index),
            FilePath = file.RelativePath,
            Language = file.Language,
            Name = nameGroup.Value,
            Kind = kind,
            Span = SpanFor(content, match.Index, match.Length),
            Provenance = provenance,
        };

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

    private sealed record TypeRelationshipCapture(string SourceName, string TargetName, string EdgeKind, string ReferenceKind, int StartByte);

    private sealed record ReferenceCapture(string TargetName, int StartByte);

    private static readonly Regex MarkdownAtxHeading = new(@"^(#{1,6})\s+([^#\n]+)$", RegexOptions.Multiline);
    private static readonly Regex MarkdownCodeBlock = new(@"```(\w*)\n?([\s\S]*?)```", RegexOptions.Multiline);
    private static readonly Regex MarkdownFrontmatter = new(@"^---\r?\n([\s\S]*?)\r?\n---", RegexOptions.Multiline);
    private static readonly Regex MarkdownFrontmatterKey = new(@"^\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*:\s*(.+)$", RegexOptions.Multiline);
    private static readonly Regex MarkdownIdentifier = new(@"\b[a-zA-Z][a-zA-Z0-9_-]*\b", RegexOptions.Multiline);
    private static readonly Regex MarkdownHeadingLevelAtLine = new(@"^(#{1,6})\s+");
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^\)]+\)", RegexOptions.Multiline);
    private static readonly Regex MarkdownInlineCode = new(@"`([^`]+)`", RegexOptions.Multiline);
    private static readonly Regex MarkdownHeadingMarker = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.Multiline);
    private static readonly Regex MarkdownEmphasisMarkers = new(@"\*\*|\*|__|_", RegexOptions.Multiline);
    private static readonly Regex MarkdownNonAnchor = new(@"[^a-z0-9-]", RegexOptions.Multiline);
    private static readonly Regex MarkdownWhitespace = new(@"\s+", RegexOptions.Multiline);
    private static readonly Regex MarkdownCodeFenceLine = new(@"^```.*$", RegexOptions.Multiline);
    private static readonly Regex MarkdownBlankLines = new(@"(\r?\n){3,}", RegexOptions.Multiline);

    /// <summary>
    /// Extracts Markdown structure: headings, code blocks, frontmatter.
    /// </summary>
    public static CodeStructureDocument ExtractMarkdown(CodeFileIdentity file, string content, ProviderProvenance provenance)
    {
        var headings = ExtractMarkdownHeadings(file, content, provenance).ToArray();
        var codeBlocks = ExtractMarkdownCodeBlocks(file, content, provenance).ToArray();
        var symbols = headings.Concat(codeBlocks).ToArray();

        var references = ExtractIdentifierReferencesMarkdown(file, content, provenance)
            .Concat(ExtractMarkdownFrontmatter(file, content, provenance))
            .ToArray();

        var edges = ExtractMarkdownEdges(content, symbols).ToArray();
        var sections = ExtractMarkdownSections(file, content, headings, provenance);
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

    private static IReadOnlyList<MarkdownSection> ExtractMarkdownSections(CodeFileIdentity file, string content, IReadOnlyList<CodeSymbol> headings, ProviderProvenance provenance)
    {
        var ordered = headings.OrderBy(h => h.Span.StartByte).ToList();
        var sections = new List<MarkdownSection>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var heading = ordered[i];
            var headingLevel = GetMarkdownHeadingLevelAtOffset(content, heading.Span.StartByte);
            var endBoundary = content.Length;
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var next = ordered[j];
                var nextLevel = GetMarkdownHeadingLevelAtOffset(content, next.Span.StartByte);
                if (nextLevel <= headingLevel)
                {
                    endBoundary = next.Span.StartByte;
                    break;
                }
            }

            var headingPath = BuildMarkdownHeadingPath(content, ordered, heading);
            var sectionText = content[heading.Span.StartByte..Math.Max(heading.Span.StartByte, endBoundary)];
            var endPos = LineColumn(content, endBoundary);

            sections.Add(new MarkdownSection
            {
                Id = CodeStableId.ForSymbol(file.RelativePath, "section", headingPath, heading.Span.StartByte),
                FilePath = file.RelativePath,
                HeadingText = heading.Name,
                HeadingLevel = headingLevel,
                HeadingPath = headingPath,
                HeadingAnchor = ToMarkdownAnchor(heading.Name),
                StartLine = heading.Span.StartLine,
                EndLine = endPos.Line,
                StartByte = heading.Span.StartByte,
                EndByte = endBoundary,
                Text = sectionText,
                PlainText = ToMarkdownPlainText(sectionText, removeFrontmatter: false),
                Provenance = provenance,
            });
        }

        return sections;
    }

    private static string BuildMarkdownHeadingPath(string content, IReadOnlyList<CodeSymbol> allHeadings, CodeSymbol heading)
    {
        var stack = new Stack<string>();
        var cursor = heading;
        while (cursor is not null)
        {
            stack.Push(cursor.Name);
            cursor = FindClosestAncestor(content, allHeadings, cursor);
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

    private static IEnumerable<CodeSymbol> ExtractMarkdownHeadings(CodeFileIdentity file, string content, ProviderProvenance provenance)
    {
        foreach (Match match in MarkdownAtxHeading.Matches(content))
        {
            var headingText = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(headingText))
                continue;

            yield return new CodeSymbol
            {
                Id = CodeStableId.ForSymbol(file.RelativePath, "heading", headingText, match.Index),
                FilePath = file.RelativePath,
                Language = file.Language,
                Name = headingText,
                Kind = "heading",
                Span = SpanFor(content, match.Index, match.Length),
                Provenance = provenance with { Confidence = Math.Min(provenance.Confidence, 0.85) },
            };
        }
    }

    private static IEnumerable<CodeSymbol> ExtractMarkdownCodeBlocks(CodeFileIdentity file, string content, ProviderProvenance provenance)
    {
        foreach (Match match in MarkdownCodeBlock.Matches(content))
        {
            var languageInfo = match.Groups[1].Value.Trim();
            var blockLanguage = string.IsNullOrWhiteSpace(languageInfo) ? "unknown" : languageInfo;
            var representativeName = blockLanguage == "unknown" ? "code-block" : $"{blockLanguage}-block";

            yield return new CodeSymbol
            {
                Id = CodeStableId.ForSymbol(file.RelativePath, "code-block", representativeName, match.Index),
                FilePath = file.RelativePath,
                Language = file.Language,
                Name = representativeName,
                Kind = "code-block",
                Span = SpanFor(content, match.Index, match.Length),
                Provenance = provenance with { Confidence = Math.Min(provenance.Confidence, 0.8) },
            };
        }
    }

    private static IEnumerable<CodeReference> ExtractMarkdownFrontmatter(CodeFileIdentity file, string content, ProviderProvenance provenance)
    {
        var frontmatterMatch = MarkdownFrontmatter.Match(content);
        if (!frontmatterMatch.Success)
            yield break;

        var yamlContent = frontmatterMatch.Groups[1].Value;
        foreach (Match keyMatch in MarkdownFrontmatterKey.Matches(yamlContent))
        {
            var key = keyMatch.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(key))
                continue;

            yield return new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "frontmatter", key, frontmatterMatch.Index + keyMatch.Index),
                FilePath = file.RelativePath,
                Kind = "frontmatter",
                Target = key,
                Span = SpanFor(content, frontmatterMatch.Index + keyMatch.Index, keyMatch.Length),
                Provenance = provenance with { FactKind = "heuristic", Confidence = 0.85 },
            };
        }
    }

    private static IEnumerable<CodeReference> ExtractIdentifierReferencesMarkdown(CodeFileIdentity file, string content, ProviderProvenance provenance)
    {
        var declarationIndices = ExtractMarkdownHeadings(file, content, provenance)
            .Select(h => h.Span.StartByte)
            .ToHashSet();

        foreach (Match match in MarkdownIdentifier.Matches(content))
        {
            var name = match.Value;
            if (declarationIndices.Contains(match.Index))
                continue;

            if (name.Length > 1 && char.IsLower(name[0]))
            {
                yield return new CodeReference
                {
                    Id = CodeStableId.ForReference(file.RelativePath, "identifier", name, match.Index),
                    FilePath = file.RelativePath,
                    Kind = "identifier",
                    Target = name,
                    Span = SpanFor(content, match.Index, match.Length),
                    Provenance = provenance with { Confidence = Math.Min(provenance.Confidence, 0.5) },
                };
            }
        }
    }

    private static IEnumerable<CodeDependencyEdge> ExtractMarkdownEdges(string content, IReadOnlyList<CodeSymbol> symbols)
    {
        var headingSymbols = symbols.Where(s => s.Kind == "heading").OrderBy(s => s.Span.StartByte).ToList();

        var byLevel = headingSymbols
            .GroupBy(s => GetMarkdownHeadingLevelAtOffset(content, s.Span.StartByte))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var levelGroup in byLevel.Where(g => g.Key > 1))
        {
            foreach (var heading in levelGroup.Value)
            {
                var ancestor = FindClosestAncestor(content, headingSymbols, heading);
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

    private static CodeSymbol? FindClosestAncestor(string content, IReadOnlyList<CodeSymbol> allHeadings, CodeSymbol child)
    {
        var childIndex = -1;
        for (var i = 0; i < allHeadings.Count; i++)
        {
            if (allHeadings[i].Id == child.Id)
            {
                childIndex = i;
                break;
            }
        }

        if (childIndex <= 0)
            return null;

        var childLevel = GetMarkdownHeadingLevelAtOffset(content, child.Span.StartByte);

        for (var i = childIndex - 1; i >= 0; i--)
        {
            var ancestor = allHeadings[i];
            var ancestorLevel = GetMarkdownHeadingLevelAtOffset(content, ancestor.Span.StartByte);
            if (ancestorLevel < childLevel)
                return ancestor;
        }

        return null;
    }

    private static int GetMarkdownHeadingLevelAtOffset(string content, int startByte)
    {
        var lineEnd = content.IndexOf('\n', Math.Max(0, startByte));
        var line = lineEnd >= 0
            ? content[startByte..lineEnd]
            : content[startByte..];

        var match = MarkdownHeadingLevelAtLine.Match(line);
        return match.Success ? match.Groups[1].Value.Length : 1;
    }
}
