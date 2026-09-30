using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;
using TreeSitter;

namespace Hypa.Infrastructure.CodeIntelligence;

/// <summary>
/// Executes a tree-sitter query pack against a parsed AST and maps captures to
/// <see cref="CodeStructureDocument"/> facts. C# (Slice 1) and TypeScript/TSX (Slice 4).
/// </summary>
internal static class TreeSitterQueryExtractor
{
    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "struct", "enum", "record", "type-alias",
    };

    private static readonly HashSet<string> CallContainerKinds = new(StringComparer.Ordinal)
    {
        "method", "constructor", "property", "function",
    };

    /// <summary>
    /// Cap in-progress query matches so pathological packs cannot grow without bound
    /// (defense against native cursor blow-ups on large C# / TS files — issue #92).
    /// </summary>
    internal const uint DefaultMatchLimit = 250_000;

    public static CodeStructureDocument Extract(
        CodeFileIdentity file,
        SourceText source,
        Tree tree,
        Query query,
        ProviderProvenance provenance)
    {
        using var cursor = query.Execute(tree.RootNode, new QueryOptions { MatchLimit = DefaultMatchLimit });

        var pending = new List<PendingSymbol>();
        var bases = new List<BaseCapture>();
        var calls = new List<NameSpan>();
        var imports = new List<NameSpan>();
        var overrides = new List<NameSpan>();
        var namedExports = new List<ExportNameSpan>();
        var primaryCtors = new List<PrimaryCtorCapture>();
        var recordPositionals = new List<RecordPositionalCapture>();

        foreach (var match in cursor.Matches)
        {
            string? defKind = null;
            Node? defNode = null;
            Node? nameNode = null;
            Node? sourceNameNode = null;
            Node? primaryCtorNode = null;
            Node? baseDeclNode = null;
            string? ownerName = null;
            Node? exportNameNode = null;
            Node? exportDefaultNode = null;
            var isRecordPositional = false;
            string? refKind = null;

            foreach (var capture in match.Captures)
            {
                switch (capture.Name)
                {
                    case "definition.class":
                        defKind = "class"; defNode = capture.Node; break;
                    case "definition.interface":
                        defKind = "interface"; defNode = capture.Node; break;
                    case "definition.struct":
                        defKind = "struct"; defNode = capture.Node; break;
                    case "definition.enum":
                        defKind = "enum"; defNode = capture.Node; break;
                    case "definition.record":
                        defKind = "record"; defNode = capture.Node; break;
                    case "definition.method":
                        defKind = "method"; defNode = capture.Node; break;
                    case "definition.function":
                        defKind = "function"; defNode = capture.Node; break;
                    case "definition.constructor":
                        defKind = "constructor"; defNode = capture.Node; break;
                    case "definition.property":
                        defKind = "property"; defNode = capture.Node; break;
                    case "definition.field":
                        defKind = "field"; defNode = capture.Node; break;
                    case "definition.event":
                        defKind = "event"; defNode = capture.Node; break;
                    case "definition.enum_member":
                        defKind = "enum-member"; defNode = capture.Node; break;
                    case "definition.namespace":
                        defKind = "namespace"; defNode = capture.Node; break;
                    case "definition.type_alias":
                        defKind = "type-alias"; defNode = capture.Node; break;
                    case "definition.type_parameter":
                        defKind = "type-parameter"; defNode = capture.Node; break;
                    case "definition.record_positional":
                        isRecordPositional = true; defNode = capture.Node; break;
                    case "name":
                        nameNode = capture.Node; break;
                    case "source":
                        sourceNameNode = capture.Node; break;
                    case "owner":
                        ownerName = capture.Node.Text; break;
                    case "primary.ctor":
                        primaryCtorNode = capture.Node; break;
                    case "export.name":
                        exportNameNode = capture.Node; break;
                    case "export.default":
                        exportDefaultNode = capture.Node; break;
                    case "reference.base":
                        refKind = "base";
                        baseDeclNode = capture.Node;
                        break;
                    case "reference.implements":
                        refKind = "implements";
                        baseDeclNode = capture.Node;
                        break;
                    case "reference.call":
                        refKind = "call"; break;
                    case "reference.import":
                        refKind = "import"; break;
                    case "reference.override":
                        refKind = "override"; break;
                }
            }

            if (exportDefaultNode is not null && !string.IsNullOrWhiteSpace(exportDefaultNode.Text))
            {
                // Separated `export default Identifier` — never a re-export-from form.
                namedExports.Add(new ExportNameSpan(exportDefaultNode.Text!, exportDefaultNode, IsDefault: true));
                continue;
            }

            if (exportNameNode is not null && !string.IsNullOrWhiteSpace(exportNameNode.Text))
            {
                // Skip `export { name } from 'module'` re-exports: they do not bind local names.
                var exportStatement = FindAncestorOfType(exportNameNode, "export_statement");
                if (exportStatement is not null && TryGetChildForField(exportStatement, "source") is not null)
                    continue;

                var isDefaultAlias = IsExportSpecifierDefaultAlias(exportNameNode);
                namedExports.Add(new ExportNameSpan(exportNameNode.Text!, exportNameNode, isDefaultAlias));
                continue;
            }

            if (refKind is not null)
            {
                if (nameNode is null || string.IsNullOrWhiteSpace(nameNode.Text))
                    continue;

                switch (refKind)
                {
                    case "base" when sourceNameNode is not null:
                        // Preserve qualified text AND global:: so Slice 3 can disable
                        // namespace-relative ranking. Same-file ResolveTarget still strips
                        // via SimpleTypeName / NormalizeTypeName for leaf lookup.
                        bases.Add(new BaseCapture(
                            sourceNameNode.Text ?? "",
                            PreserveTypeReferenceText(nameNode),
                            nameNode,
                            baseDeclNode,
                            PreferImplements: false));
                        break;
                    case "implements" when sourceNameNode is not null:
                        bases.Add(new BaseCapture(
                            sourceNameNode.Text ?? "",
                            PreserveTypeReferenceText(nameNode),
                            nameNode,
                            baseDeclNode,
                            PreferImplements: true));
                        break;
                    case "call":
                        // Prefer alias-qualified parent text when present (global::System.Console.WriteLine).
                        calls.Add(new NameSpan(PreserveCallTargetText(nameNode), nameNode));
                        break;
                    case "import":
                        imports.Add(new NameSpan(NormalizeImportName(nameNode.Text!), nameNode));
                        break;
                    case "override":
                        // C#: re-check modifier text so a binding that skips #eq? cannot mis-tag methods.
                        // TS: override_modifier is a dedicated node already required by the query.
                        var methodDecl = FindAncestorOfType(nameNode, "method_declaration")
                                         ?? FindAncestorOfType(nameNode, "method_definition");
                        if (methodDecl is not null
                            && (HasModifier(methodDecl, "override") || HasOverrideModifierNode(methodDecl)))
                            overrides.Add(new NameSpan(nameNode.Text!, nameNode));
                        break;
                }

                continue;
            }

            if (nameNode is null || string.IsNullOrWhiteSpace(nameNode.Text))
                continue;

            var name = nameNode.Text!;

            if (isRecordPositional)
            {
                var paramNode = nameNode;
                while (paramNode.Parent is not null && paramNode.Type != "parameter")
                    paramNode = paramNode.Parent;
                // defNode is the owning record_declaration (@definition.record_positional).
                // Prefer the AST node over OwnerName so same-named records in different
                // namespaces/nestings parent correctly (byDefNode lookup).
                var ownerNode = defNode ?? FindAncestorOfType(nameNode, "record_declaration");
                if (ownerNode is null)
                    continue;
                recordPositionals.Add(new RecordPositionalCapture(
                    ownerName ?? "",
                    name,
                    paramNode.Type == "parameter" ? paramNode : nameNode,
                    nameNode,
                    ownerNode));
                continue;
            }

            if (defKind is null || defNode is null)
                continue;

            // TS: method_definition named "constructor" is a constructor; get/set → property.
            if (defKind == "method" && defNode.Type is "method_definition" or "method_signature" or "abstract_method_signature")
            {
                if (name is "constructor")
                    defKind = "constructor";
                else if (HasAnonymousChildKeyword(defNode, "get") || HasAnonymousChildKeyword(defNode, "set"))
                    defKind = "property";
            }

            // Prefer the binding name for assigned function expressions:
            // `let named = function namedFn() {}` emits only `named`, not dual `namedFn`.
            if (defKind == "function"
                && defNode.Type is "function_expression" or "generator_function"
                && IsAssignedCallableValue(defNode))
            {
                continue;
            }

            var spanNode = defNode;
            if (defKind is "field" or "event")
            {
                // Prefer the variable_declarator span so multi-declarator fields don't share one span.
                // TS public_field_definition is already one field per node.
                if (defNode.Type is "public_field_definition")
                {
                    spanNode = defNode;
                }
                else
                {
                    var n = nameNode;
                    while (n.Parent is not null
                           && n.Type != "variable_declarator"
                           && n.Type != "field_declaration"
                           && n.Type != "event_field_declaration"
                           && n.Type != "event_declaration")
                        n = n.Parent;
                    if (n.Type is "variable_declarator" or "event_declaration")
                        spanNode = n;
                }
            }
            else if (defKind is "type-parameter")
            {
                spanNode = nameNode.Parent is { Type: "type_parameter" } tp ? tp : nameNode;
            }
            else if (defKind is "function"
                     && defNode.Type is "lexical_declaration" or "variable_declaration")
            {
                // Prefer the variable_declarator so multi-declarators don't share one span.
                var n = nameNode;
                while (n.Parent is not null && n.Type != "variable_declarator")
                    n = n.Parent;
                if (n.Type == "variable_declarator")
                    spanNode = n;
            }

            pending.Add(new PendingSymbol(defKind, name, spanNode, nameNode, defNode));

            if (primaryCtorNode is not null && defKind is "class" or "struct" or "record")
                primaryCtors.Add(new PrimaryCtorCapture(name, primaryCtorNode, defNode));
        }

        // Build symbols. Same-file partial types merge by (kind, name, arity, nesting) → one symbol.
        // Span comes from the first declaration (lowest name start); Id is a scope-path moniker
        // (no byte offset) so partials and whitespace edits share a stable id. All partial
        // DefNodes map to that canonical entry so members re-parent under one id.
        var seenExact = new HashSet<(string Kind, string Name, int Start)>();
        // moniker → first entry index; duplicates map byDefNode to the canonical entry (namespaces).
        var monikerCanonical = new Dictionary<string, int>(StringComparer.Ordinal);
        var typeCanonical = new Dictionary<(string Kind, string Name, int Arity, string Nesting), int>();
        var entries = new List<SymbolEntry>();
        var byDefNode = new Dictionary<NodeKey, int>();
        var namedExportBindings = BuildNamedExportBindings(namedExports);

        foreach (var p in pending.OrderBy(x => x.NameNode.StartIndex).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            if (TypeKinds.Contains(p.Kind))
            {
                var nesting = NestingKey(p.DefNode);
                var arity = CountTypeParameters(p.DefNode);
                var mergeKey = (p.Kind, p.Name, arity, nesting);
                if (typeCanonical.TryGetValue(mergeKey, out var canonIdx))
                {
                    // Additional partial declaration of an already-emitted type.
                    byDefNode[NodeKey.From(p.DefNode)] = canonIdx;
                    MergePartialTypeSurface(entries, canonIdx, file.Language, p, namedExportBindings);
                    continue;
                }

                var moniker = BuildSymbolMoniker(file.RelativePath, p.Kind, p.Name, p.DefNode, signature: null);
                if (monikerCanonical.TryGetValue(moniker, out var existingTypeIdx))
                {
                    byDefNode[NodeKey.From(p.DefNode)] = existingTypeIdx;
                    typeCanonical[mergeKey] = existingTypeIdx;
                    MergePartialTypeSurface(entries, existingTypeIdx, file.Language, p, namedExportBindings);
                    continue;
                }

                var surface = ExtractSymbolSurface(file.Language, p, namedExportBindings);
                var symbol = new CodeSymbol
                {
                    Id = CodeStableId.ForSymbol(moniker),
                    FilePath = file.RelativePath,
                    Language = file.Language,
                    Name = p.Name,
                    Kind = p.Kind,
                    Span = SpanFromNode(source, p.SpanNode),
                    Provenance = ForFact(provenance, "symbol-declaration"),
                    Accessibility = surface.Accessibility,
                    ExportStatus = surface.ExportStatus,
                    Modifiers = surface.Modifiers,
                    Signature = surface.Signature,
                };
                entries.Add(new SymbolEntry(symbol, p.DefNode, p.NameNode));
                var idx = entries.Count - 1;
                byDefNode[NodeKey.From(p.DefNode)] = idx;
                typeCanonical[mergeKey] = idx;
                monikerCanonical[moniker] = idx;
                seenExact.Add((p.Kind, p.Name, p.NameNode.StartIndex));
                continue;
            }

            var exactKey = (p.Kind, p.Name, p.NameNode.StartIndex);
            if (!seenExact.Add(exactKey))
                continue;

            var signature = NeedsSignature(p.Kind)
                ? ExtractCallableSignature(p.DefNode)
                : null;
            var memberMoniker = BuildSymbolMoniker(file.RelativePath, p.Kind, p.Name, p.DefNode, signature);
            // Same moniker (e.g. repeated namespace blocks): keep first span, map DefNode so children
            // under later declarations still resolve ParentId / contains.
            if (monikerCanonical.TryGetValue(memberMoniker, out var existingMemberIdx))
            {
                // Multi-declarator fields/events and assigned functions are keyed by their
                // variable_declarator (SpanNode), not the shared declaration DefNode.
                if (p.Kind is "field" or "event" || UsesDeclaratorSpan(p))
                    byDefNode[NodeKey.From(p.SpanNode)] = existingMemberIdx;
                else
                    byDefNode[NodeKey.From(p.DefNode)] = existingMemberIdx;
                continue;
            }

            var memberSurface = ExtractSymbolSurface(file.Language, p, namedExportBindings);
            var member = new CodeSymbol
            {
                Id = CodeStableId.ForSymbol(memberMoniker),
                FilePath = file.RelativePath,
                Language = file.Language,
                Name = p.Name,
                Kind = p.Kind,
                Span = SpanFromNode(source, p.SpanNode),
                Provenance = ForFact(provenance, "symbol-declaration"),
                Accessibility = memberSurface.Accessibility,
                ExportStatus = memberSurface.ExportStatus,
                Modifiers = memberSurface.Modifiers,
                Signature = memberSurface.Signature,
            };
            entries.Add(new SymbolEntry(member, p.DefNode, p.NameNode));
            var memberIdx = entries.Count - 1;

            // Key multi-declarator fields/events and assigned function bindings by their span
            // node (declarator), not the shared field_declaration / lexical_declaration, so
            // later siblings don't clobber ParentId lookups (Issue 13 / multi-declarator T/U).
            if (p.Kind is "field" or "event" || UsesDeclaratorSpan(p))
                byDefNode[NodeKey.From(p.SpanNode)] = memberIdx;
            else
                byDefNode[NodeKey.From(p.DefNode)] = memberIdx;
            monikerCanonical[memberMoniker] = memberIdx;
        }

        // Primary constructors.
        foreach (var ctor in primaryCtors.OrderBy(c => c.ParameterList.StartIndex))
        {
            if (!byDefNode.TryGetValue(NodeKey.From(ctor.TypeNode), out var typeIdx))
                continue;

            var typeSymbol = entries[typeIdx].Symbol;
            var startChar = ctor.ParameterList.StartIndex;
            var key = ("constructor", ctor.TypeName, startChar);
            if (!seenExact.Add(key))
                continue;

            // Primary ctor is attached to the type node (not a constructor_declaration), so
            // scope must include the type name then the ctor member name (type name again).
            var ctorSignature = ExtractCallableSignature(ctor.ParameterList);
            // Member segment is the type name; enclosing type segment already carries `N arity.
            var ctorScope = BuildChildScopePath(ctor.TypeNode, ctor.TypeName);
            var ctorMoniker = CodeSymbolMoniker.Build(
                file.RelativePath, "constructor", ctorScope, ctorSignature);
            if (monikerCanonical.TryGetValue(ctorMoniker, out var existingCtorIdx))
            {
                byDefNode[NodeKey.From(ctor.ParameterList)] = existingCtorIdx;
                continue;
            }

            var ctorSymbol = new CodeSymbol
            {
                Id = CodeStableId.ForSymbol(ctorMoniker),
                FilePath = file.RelativePath,
                Language = file.Language,
                Name = ctor.TypeName,
                Kind = "constructor",
                ParentId = typeSymbol.Id,
                Span = SpanFromNode(source, ctor.ParameterList),
                Provenance = ForFact(provenance, "symbol-declaration"),
                Accessibility = typeSymbol.Accessibility,
                Signature = ctor.ParameterList.Text,
            };
            entries.Add(new SymbolEntry(ctorSymbol, ctor.ParameterList, ctor.ParameterList));
            var ctorIdx = entries.Count - 1;
            byDefNode[NodeKey.From(ctor.ParameterList)] = ctorIdx;
            monikerCanonical[ctorMoniker] = ctorIdx;
        }

        // Record positional params → properties.
        foreach (var pos in recordPositionals.OrderBy(p => p.NameNode.StartIndex))
        {
            var startChar = pos.NameNode.StartIndex;
            if (!seenExact.Add(("property", pos.Name, startChar)))
                continue;

            // Resolve parent via the owning record_declaration node (same pattern as primary ctors).
            string? parentId = null;
            if (byDefNode.TryGetValue(NodeKey.From(pos.OwnerNode), out var ownerIdx))
                parentId = entries[ownerIdx].Symbol.Id;

            // Scope under the owning record so same-named positionals in different records differ.
            var propScope = BuildChildScopePath(pos.OwnerNode, pos.Name);
            var propMoniker = CodeSymbolMoniker.Build(
                file.RelativePath, "property", propScope, parameterTypes: null);
            if (monikerCanonical.ContainsKey(propMoniker))
                continue;

            var prop = new CodeSymbol
            {
                Id = CodeStableId.ForSymbol(propMoniker),
                FilePath = file.RelativePath,
                Language = file.Language,
                Name = pos.Name,
                Kind = "property",
                ParentId = parentId,
                Span = SpanFromNode(source, pos.ParamNode),
                Provenance = ForFact(provenance, "symbol-declaration"),
                Accessibility = ExtractRecordPositionalAccessibility(pos.ParamNode),
                Signature = ExtractTypedSignature(pos.ParamNode),
            };
            entries.Add(new SymbolEntry(prop, pos.ParamNode, pos.NameNode));
            monikerCanonical[propMoniker] = entries.Count - 1;
        }

        CodeSymbol? fileScopedNs = null;
        foreach (var e in entries)
        {
            if (e.Symbol.Kind == "namespace" && e.DefNode.Type == "file_scoped_namespace_declaration")
            {
                fileScopedNs = e.Symbol;
                break;
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Symbol.ParentId is not null)
                continue;

            var parentId = ResolveParentId(entry, entries, byDefNode, fileScopedNs);
            if (parentId is not null)
                entries[i] = entry with { Symbol = entry.Symbol with { ParentId = parentId } };
        }

        var finalSymbols = entries
            .Select(e => e.Symbol)
            .OrderBy(s => s.Span.StartByte)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Kind, StringComparer.Ordinal)
            .ToArray();

        var localByName = finalSymbols
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var references = new List<CodeReference>();
        var edges = new List<CodeDependencyEdge>();

        foreach (var s in finalSymbols.Where(s => s.ParentId is not null))
        {
            edges.Add(new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(s.ParentId!, s.Id, "contains"),
                SourceId = s.ParentId!,
                TargetId = s.Id,
                Kind = "contains",
                Provenance = ForFact(provenance, "containment"),
            });
        }

        foreach (var imp in imports.OrderBy(i => i.Node.StartIndex).ThenBy(i => i.Name, StringComparer.Ordinal))
        {
            var span = SpanFromNode(source, imp.Node);
            references.Add(new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "import", imp.Name, span.StartByte),
                FilePath = file.RelativePath,
                Kind = "import",
                Target = imp.Name,
                Span = span,
                Provenance = ForFact(provenance, "import"),
            });
            edges.Add(new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(file.RelativePath, imp.Name, "imports", span.StartByte),
                SourceId = file.RelativePath,
                TargetId = imp.Name,
                Kind = "imports",
                SourceSpan = span,
                TargetName = imp.Name,
                TargetResolutionStatus = "external-name",
                Provenance = ForFact(provenance, "import"),
            });
        }

        foreach (var b in bases.OrderBy(x => x.TargetNode.StartIndex))
        {
            CodeSymbol? sourceSymbol = null;
            if (b.SourceDeclNode is not null
                && byDefNode.TryGetValue(NodeKey.From(b.SourceDeclNode), out var srcIdx))
            {
                sourceSymbol = entries[srcIdx].Symbol;
            }
            else
            {
                sourceSymbol = finalSymbols.FirstOrDefault(s =>
                    TypeKinds.Contains(s.Kind) && s.Name == b.SourceName);
            }

            if (sourceSymbol is null)
                continue;

            // Lookup with normalized name; keep raw TargetName (may include global::) on the edge.
            var resolution = ResolveTarget(b.TargetName, localByName);
            CodeSymbol? resolvedTarget = null;
            if (resolution.Status == "local-symbol")
            {
                // Prefer Id match (works for FQN bases resolved via simple-name fallback).
                resolvedTarget = finalSymbols.FirstOrDefault(s => s.Id == resolution.TargetId);
                if (resolvedTarget is null
                    && localByName.TryGetValue(SimpleTypeName(b.TargetName), out var matches)
                    && matches.Length == 1)
                {
                    resolvedTarget = matches[0];
                }
            }

            var edgeKind = b.PreferImplements
                ? "implements"
                : InferRelationshipEdgeKind(sourceSymbol.Kind, b.TargetName, resolvedTarget);
            var refKindName = edgeKind == "inherits" ? "inheritance" : "implementation";
            var span = SpanFromNode(source, b.TargetNode);
            // Prefer the wider alias-qualified span when TargetName includes global::.
            var spanNode = b.TargetNode.Parent is { Type: "alias_qualified_name" } alias
                ? alias
                : b.TargetNode;
            span = SpanFromNode(source, spanNode);

            references.Add(new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, refKindName, b.TargetName, span.StartByte),
                FilePath = file.RelativePath,
                Kind = refKindName,
                Target = b.TargetName,
                Span = span,
                Provenance = ForFact(provenance, "type-relationship", 0.88),
            });
            edges.Add(new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(sourceSymbol.Id, resolution.TargetId, edgeKind, span.StartByte),
                SourceId = sourceSymbol.Id,
                TargetId = resolution.TargetId,
                Kind = edgeKind,
                SourceSpan = span,
                TargetName = b.TargetName,
                TargetResolutionStatus = resolution.Status,
                Provenance = ForFact(provenance, "type-relationship", resolution.Status == "local-symbol" ? 0.9 : 0.8),
            });
        }

        foreach (var o in overrides.OrderBy(x => x.Node.StartIndex))
        {
            var span = SpanFromNode(source, o.Node);
            var sourceSymbol = NearestContainingSymbol(finalSymbols, span.StartByte, preferCallContainers: true);
            if (sourceSymbol is null)
                continue;

            // Override target is a base-type member — never the overriding method itself.
            // Exclude the source from same-file resolution so a sole Run() does not self-loop;
            // cross-file base methods stay unresolved until Phase 3.
            var resolution = ResolveOverrideTarget(o.Name, localByName, sourceSymbol);
            references.Add(new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "override", o.Name, span.StartByte),
                FilePath = file.RelativePath,
                Kind = "override",
                Target = o.Name,
                Span = span,
                Provenance = ForFact(provenance, "override", 0.88),
            });
            edges.Add(new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(sourceSymbol.Id, resolution.TargetId, "overrides", span.StartByte),
                SourceId = sourceSymbol.Id,
                TargetId = resolution.TargetId,
                Kind = "overrides",
                SourceSpan = span,
                TargetName = o.Name,
                TargetResolutionStatus = resolution.Status,
                Provenance = ForFact(provenance, "override", resolution.Status == "local-symbol" ? 0.9 : 0.75),
            });
        }

        // Calls only — deliberately no per-token identifier reference firehose.
        foreach (var c in calls.OrderBy(x => x.Node.StartIndex).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            if (IsKeyword(c.Name))
                continue;

            var span = SpanFromNode(source, c.Node);
            references.Add(new CodeReference
            {
                Id = CodeStableId.ForReference(file.RelativePath, "call", c.Name, span.StartByte),
                FilePath = file.RelativePath,
                Kind = "call",
                Target = c.Name,
                Span = span,
                Provenance = ForFact(provenance, "call", 0.85),
            });

            var sourceSymbol = NearestContainingSymbol(finalSymbols, span.StartByte, preferCallContainers: true);
            if (sourceSymbol is null)
                continue;

            // Member-access targets (Console.WriteLine) must not fall back to simple-leaf
            // lookup — that falsely binds BCL calls to same-file methods named WriteLine.
            var resolution = ResolveTarget(
                c.Name,
                localByName,
                allowSimpleLeafFallback: !c.Name.Contains('.', StringComparison.Ordinal));
            edges.Add(new CodeDependencyEdge
            {
                Id = CodeStableId.ForEdge(sourceSymbol.Id, resolution.TargetId, "calls", span.StartByte),
                SourceId = sourceSymbol.Id,
                TargetId = resolution.TargetId,
                Kind = "calls",
                SourceSpan = span,
                TargetName = c.Name,
                TargetResolutionStatus = resolution.Status,
                Provenance = ForFact(provenance, "call", resolution.Status == "local-symbol" ? 0.9 : 0.75),
            });
        }

        return new CodeStructureDocument
        {
            File = file,
            Provenance = provenance,
            Symbols = finalSymbols,
            References = references
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .OrderBy(r => r.Span.StartByte)
                .ThenBy(r => r.Kind, StringComparer.Ordinal)
                .ThenBy(r => r.Target, StringComparer.Ordinal)
                .ToArray(),
            DependencyEdges = edges
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .OrderBy(e => e.SourceSpan?.StartByte ?? int.MaxValue)
                .ThenBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.TargetName ?? e.TargetId, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static string? ResolveParentId(
        SymbolEntry self,
        List<SymbolEntry> entries,
        Dictionary<NodeKey, int> byDefNode,
        CodeSymbol? fileScopedNs)
    {
        var node = self.DefNode.Parent;
        while (node is not null)
        {
            if (byDefNode.TryGetValue(NodeKey.From(node), out var idx))
            {
                var parent = entries[idx].Symbol;
                if (parent.Id != self.Symbol.Id)
                    return parent.Id;
            }

            node = node.Parent;
        }

        // File-scoped namespace is a sibling of top-level types (and nested namespace
        // declarations) under compilation_unit — not an AST ancestor.
        if (fileScopedNs is not null
            && self.Symbol.Id != fileScopedNs.Id
            && (TypeKinds.Contains(self.Symbol.Kind) || self.Symbol.Kind == "namespace")
            && self.DefNode.Parent?.Type == "compilation_unit")
        {
            return fileScopedNs.Id;
        }

        return null;
    }

    /// <summary>
    /// Nesting path for same-file partial merge. Types with the same kind+name under different
    /// enclosing types (e.g. two nested Nested classes) do not merge. Arity is tracked separately
    /// on the merge key so <c>Box&lt;T&gt;</c> ≠ <c>Box&lt;T,U&gt;</c>.
    /// </summary>
    private static string NestingKey(Node defNode)
    {
        var parts = new List<string>();
        var node = defNode.Parent;
        while (node is not null)
        {
            if (node.Type is "class_declaration" or "abstract_class_declaration"
                or "interface_declaration" or "struct_declaration"
                or "enum_declaration" or "record_declaration"
                or "type_alias_declaration"
                or "namespace_declaration" or "file_scoped_namespace_declaration"
                or "internal_module" or "module"
                or "function_declaration" or "method_definition")
            {
                var name = GetScopedContainerName(node) ?? "?";
                parts.Add(node.Type + ":" + name);
            }

            node = node.Parent;
        }

        parts.Reverse();
        return string.Join('/', parts);
    }

    private static bool NeedsSignature(string kind) => kind is "method" or "constructor" or "function";

    /// <summary>
    /// Canonical moniker for a declaration whose <paramref name="defNode"/> is the declaration
    /// of <paramref name="name"/> (class/method/field/… body). Scope = enclosing namespaces/types
    /// (/methods for type-parameters) + name; methods/ctors also get a callable signature.
    /// Types/methods encode generic arity as <c>Name`N</c> (metadata-style).
    /// </summary>
    private static string BuildSymbolMoniker(
        string filePath, string kind, string name, Node defNode, string? signature)
    {
        var scopedName = FormatScopedName(name, CountTypeParameters(defNode));
        // Only the file-scoped namespace declaration itself must skip the sibling prepend
        // (would double its own name). Nested `namespace Nested` under `namespace Outer;`
        // still needs Outer prepended.
        var includeFileScopedSibling = defNode.Type is not "file_scoped_namespace_declaration";
        var scopePath = BuildScopePath(defNode, scopedName, includeFileScopedSibling);
        return CodeSymbolMoniker.Build(filePath, kind, scopePath, signature);
    }

    /// <summary>
    /// Dot-separated scope path: enclosing named containers then <paramref name="scopedName"/>.
    /// File-scoped and block namespaces both contribute their name field text (e.g. <c>A.B</c>).
    /// </summary>
    private static string BuildScopePath(Node defNode, string scopedName, bool includeFileScopedSibling = true)
    {
        var parts = CollectEnclosingScopeNames(defNode.Parent, includeFileScopedSibling);
        parts.Add(scopedName);
        return string.Join('.', parts);
    }

    /// <summary>
    /// Scope path for a synthetic child of an already-named declaration (primary ctor under a
    /// type, record positional under a record). Includes the enclosing declaration's own name.
    /// </summary>
    private static string BuildChildScopePath(Node enclosingDefNode, string childName)
    {
        var parts = CollectEnclosingScopeNames(enclosingDefNode.Parent, includeFileScopedSibling: true);
        var enclosingName = GetScopedContainerName(enclosingDefNode);
        if (!string.IsNullOrEmpty(enclosingName))
            parts.Add(enclosingName);
        parts.Add(childName);
        return string.Join('.', parts);
    }

    private static List<string> CollectEnclosingScopeNames(Node? startParent, bool includeFileScopedSibling)
    {
        var parts = new List<string>();
        var node = startParent;
        Node? compilationUnit = null;
        while (node is not null)
        {
            if (node.Type == "compilation_unit")
                compilationUnit = node;

            if (IsNamedScopeContainer(node))
            {
                var n = GetScopedContainerName(node);
                if (!string.IsNullOrEmpty(n))
                    parts.Add(n);
            }

            node = node.Parent;
        }

        parts.Reverse();

        // File-scoped namespaces are siblings of top-level types under compilation_unit
        // (not ancestors). Prepend their name so monikers match block-scoped equivalents.
        // Skip when building the namespace symbol itself (would double the name).
        if (includeFileScopedSibling && compilationUnit is not null)
        {
            var fileScopedName = FindFileScopedNamespaceName(compilationUnit);
            if (!string.IsNullOrEmpty(fileScopedName)
                && (parts.Count == 0 || !string.Equals(parts[0], fileScopedName, StringComparison.Ordinal)))
            {
                parts.Insert(0, fileScopedName);
            }
        }

        return parts;
    }

    private static string? FindFileScopedNamespaceName(Node compilationUnit)
    {
        foreach (var child in compilationUnit.NamedChildren)
        {
            if (child.Type == "file_scoped_namespace_declaration")
                return GetNameFieldText(child);
        }

        return null;
    }

    private static bool IsNamedScopeContainer(Node node) => node.Type is
        "class_declaration" or "abstract_class_declaration"
        or "interface_declaration" or "struct_declaration"
        or "enum_declaration" or "record_declaration"
        or "type_alias_declaration"
        or "namespace_declaration" or "file_scoped_namespace_declaration"
        or "internal_module" or "module"
        // Methods/ctors enclose type-parameters so T on MethodA ≠ T on MethodB.
        or "method_declaration" or "constructor_declaration"
        or "local_function_statement"
        or "method_definition" or "function_declaration" or "function_expression"
        // Assigned generators (`const g = function* <T>() {}`) need the same scope
        // treatment as arrow_function / function_expression so type-params get a binding segment.
        or "generator_function_declaration" or "generator_function" or "arrow_function";

    /// <summary>
    /// Container name for moniker segments. Types append <c>`N</c> when generic. Methods also
    /// embed their callable signature so type-parameters under overloaded generic methods
    /// (e.g. two <c>M&lt;T&gt;</c> overloads) do not share a moniker path.
    /// </summary>
    private static string? GetScopedContainerName(Node node)
    {
        // TS arrows / assigned function expressions have no reliable name field on the
        // callable node. Prefer the nearest variable_declarator binding so type-parameters
        // under `const a = <T>() => {}` and `const b = <T>() => {}` keep distinct monikers.
        if (node.Type is "arrow_function" or "function_expression" or "generator_function")
        {
            var binding = GetAssignedBindingName(node);
            var innerName = GetNameFieldText(node);
            // Prefer binding when present (assigned form); otherwise the expression's own name.
            var callableName = !string.IsNullOrEmpty(binding) ? binding : innerName;
            if (string.IsNullOrEmpty(callableName))
                return callableName;

            var arityName = FormatScopedName(callableName, CountTypeParameters(node));
            var sig = ExtractCallableSignature(node);
            return $"{arityName}({sig})";
        }

        var name = GetNameFieldText(node);
        // TS constructors use property_identifier "constructor" without a name field on the parent.
        if (string.IsNullOrEmpty(name)
            && node.Type is "method_definition" or "method_signature" or "abstract_method_signature")
        {
            name = FindNamedChild(node, "property_identifier")?.Text;
        }

        if (string.IsNullOrEmpty(name))
            return name;

        if (node.Type is "class_declaration" or "abstract_class_declaration"
            or "interface_declaration" or "struct_declaration"
            or "record_declaration" or "type_alias_declaration")
        {
            return FormatScopedName(name, CountTypeParameters(node));
        }

        if (node.Type is "method_declaration" or "local_function_statement" or "constructor_declaration"
            or "method_definition" or "function_declaration"
            or "generator_function_declaration")
        {
            var arityName = FormatScopedName(name, CountTypeParameters(node));
            var sig = ExtractCallableSignature(node);
            return $"{arityName}({sig})";
        }

        return name;
    }

    /// <summary>
    /// Binding identifier for a callable that is the <c>value</c> of a <c>variable_declarator</c>
    /// (e.g. <c>const helper = () =&gt; {}</c> → <c>helper</c>).
    /// </summary>
    private static string? GetAssignedBindingName(Node callableNode)
    {
        var parent = callableNode.Parent;
        if (parent is null || parent.Type != "variable_declarator")
            return null;

        try
        {
            var value = parent.GetChildForField("value");
            if (value is null
                || value.StartIndex != callableNode.StartIndex
                || value.EndIndex != callableNode.EndIndex)
            {
                return null;
            }

            return GetNameFieldText(parent);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="callableNode"/> is the value of a variable declarator
    /// (assigned function expression / generator).
    /// </summary>
    private static bool IsAssignedCallableValue(Node callableNode) =>
        GetAssignedBindingName(callableNode) is not null;

    /// <summary>
    /// True when the pending symbol's span was narrowed to a <c>variable_declarator</c>
    /// (assigned function / multi-declarator binding) so ParentId lookups must key that node
    /// rather than the shared <c>lexical_declaration</c> / <c>variable_declaration</c>.
    /// </summary>
    private static bool UsesDeclaratorSpan(PendingSymbol p) =>
        p.SpanNode.Type == "variable_declarator"
        && p.SpanNode.StartIndex != p.DefNode.StartIndex;

    private static string FormatScopedName(string name, int arity) =>
        arity > 0 ? $"{name}`{arity}" : name;

    private static int CountTypeParameters(Node declaration)
    {
        // C#: type_parameter_list; TS/TSX: type_parameters
        var list = FindNamedChild(declaration, "type_parameter_list")
                   ?? FindNamedChild(declaration, "type_parameters");
        if (list is null)
            return 0;

        var count = 0;
        foreach (var child in list.NamedChildren)
        {
            if (child.Type == "type_parameter")
                count++;
        }

        return count;
    }

    /// <summary>
    /// Callable signature: comma-separated parameter descriptors (modifiers + type text).
    /// Method type-parameter arity is encoded on the scope-path name segment (<c>M`N</c>), not here.
    /// </summary>
    private static string ExtractCallableSignature(Node declarationOrParameterList)
    {
        var paramList = declarationOrParameterList.Type is "parameter_list" or "formal_parameters"
            ? declarationOrParameterList
            : FindNamedChild(declarationOrParameterList, "parameter_list")
              ?? FindNamedChild(declarationOrParameterList, "formal_parameters");

        if (paramList is null)
            return "";

        var parts = new List<string>();
        foreach (var child in paramList.NamedChildren)
        {
            // Standard parameters (C# parameter; TS required/optional/rest).
            if (child.Type is "parameter" or "required_parameter" or "optional_parameter" or "rest_parameter")
            {
                parts.Add(GetParameterDescriptor(child));
                continue;
            }

            // tree-sitter-c-sharp often models `params T[] name` as parameter_array, not parameter.
            if (child.Type is "parameter_array" or "params_parameter")
            {
                parts.Add(GetParameterArrayDescriptor(child));
                continue;
            }

            // Some grammar versions surface params as a bare array_type + identifier under the list,
            // or as a single named node whose text starts with "params".
            if (child.Type is "array_type" or "nullable_type")
            {
                // Look for a preceding "params" keyword among all children (not only named).
                if (ParameterListHasParamsKeyword(paramList))
                    parts.Add("params " + NormalizeTypeText(child.Text ?? "?"));
                continue;
            }
        }

        // If the list has a params keyword but we still emitted nothing useful, fall back to
        // scanning all children for a params-prefixed span.
        if (parts.Count == 0 && ParameterListHasParamsKeyword(paramList))
        {
            var fallback = TryExtractParamsFromParameterList(paramList);
            if (fallback is not null)
                parts.Add(fallback);
        }

        var signature = string.Join(',', parts);
        if (declarationOrParameterList.Type == "constructor_declaration"
            && HasModifier(declarationOrParameterList, "static"))
        {
            return signature.Length == 0 ? "static" : $"static {signature}";
        }

        return signature;
    }

    private static bool ParameterListHasParamsKeyword(Node paramList)
    {
        foreach (var child in paramList.Children)
        {
            if (child.Type == "params" || (child.Text == "params" && !child.IsNamed))
                return true;
            if (child.Type == "modifier" && child.Text == "params")
                return true;
            if (child.IsNamed && (child.Text?.StartsWith("params ", StringComparison.Ordinal) ?? false))
                return true;
        }

        return false;
    }

    private static string? TryExtractParamsFromParameterList(Node paramList)
    {
        // Reconstruct "params <type>" from the parameter_list text when structure is opaque.
        var raw = NormalizeTypeText(paramList.Text ?? "");
        // Strip surrounding parens from parameter_list text "(params int[] xs)".
        if (raw.StartsWith('(') && raw.EndsWith(')'))
            raw = raw[1..^1].Trim();
        if (!raw.StartsWith("params ", StringComparison.Ordinal))
            return null;

        var body = raw["params ".Length..].Trim();
        // Drop parameter name (last identifier token).
        var lastSpace = body.LastIndexOf(' ');
        var typeText = lastSpace > 0 ? body[..lastSpace] : body;
        return typeText.Length == 0 ? null : "params " + typeText;
    }

    private static string GetParameterArrayDescriptor(Node parameterArray)
    {
        // Prefer explicit type field; fall back to first array_type child / full text.
        string typeText;
        try
        {
            var typeNode = parameterArray.GetChildForField("type");
            typeText = typeNode is not null && !string.IsNullOrWhiteSpace(typeNode.Text)
                ? NormalizeTypeText(typeNode.Text!)
                : "";
        }
        catch
        {
            typeText = "";
        }

        if (typeText.Length == 0)
        {
            foreach (var child in parameterArray.NamedChildren)
            {
                if (child.Type is "array_type" or "nullable_type" or "generic_name"
                    or "predefined_type" or "identifier" or "qualified_name")
                {
                    typeText = NormalizeTypeText(child.Text ?? "?");
                    break;
                }
            }
        }

        if (typeText.Length == 0)
        {
            // Last resort: strip leading "params" from node text and drop the name token.
            var raw = NormalizeTypeText(parameterArray.Text ?? "");
            if (raw.StartsWith("params ", StringComparison.Ordinal))
                raw = raw["params ".Length..];
            // Drop trailing identifier (parameter name).
            var lastSpace = raw.LastIndexOf(' ');
            typeText = lastSpace > 0 ? raw[..lastSpace] : (raw.Length == 0 ? "?" : raw);
        }

        return "params " + typeText;
    }

    private static string GetParameterDescriptor(Node parameter)
    {
        var modifier = GetParameterModifierPrefix(parameter);
        var typeText = GetParameterTypeText(parameter);
        // If type text already starts with the same modifier words (ref_type folding), don't double.
        // Compare against the full modifier including trailing space so "int" does not match "in "
        // (TrimEnd would make StartsWith("in") true for "int" and drop the in-modifier).
        if (modifier.Length > 0 && typeText.StartsWith(modifier, StringComparison.Ordinal))
            return typeText;
        return modifier.Length == 0 ? typeText : modifier + typeText;
    }

    private static string GetParameterModifierPrefix(Node parameter)
    {
        // Accumulate all relevant modifiers in source order (ref readonly, this, params, …).
        var mods = new List<string>();
        foreach (var child in parameter.NamedChildren)
        {
            if (child.Type == "modifier"
                && child.Text is "ref" or "out" or "in" or "params" or "this" or "readonly")
            {
                mods.Add(child.Text);
            }
        }

        if (mods.Count > 0)
            return string.Join(' ', mods) + " ";

        try
        {
            var typeNode = parameter.GetChildForField("type");
            if (typeNode is not null && typeNode.Type == "ref_type")
            {
                // ref_type text is typically "ref int" / "out string" / "in T" / "ref readonly int".
                // Modifier folded into type text by GetParameterTypeText — avoid double prefix.
                return "";
            }
        }
        catch
        {
            // ignore
        }

        return "";
    }

    private static string GetParameterTypeText(Node parameter)
    {
        try
        {
            var typeNode = parameter.GetChildForField("type");
            if (typeNode is not null && !string.IsNullOrWhiteSpace(typeNode.Text))
                return NormalizeTypeText(typeNode.Text!);
        }
        catch
        {
            // Fall through to child scan.
        }

        // TypeScript: type lives under type_annotation child, not a type field.
        foreach (var child in parameter.NamedChildren)
        {
            if (child.Type == "type_annotation")
            {
                var text = child.Text ?? "";
                if (text.StartsWith(':'))
                    text = text[1..].TrimStart();
                if (!string.IsNullOrWhiteSpace(text))
                    return NormalizeTypeText(text);
            }
        }

        // No type field (unusual) — take the first type-like child, skipping attributes/modifiers.
        foreach (var child in parameter.NamedChildren)
        {
            if (child.Type is "predefined_type" or "generic_name" or "nullable_type"
                or "array_type" or "qualified_name" or "tuple_type" or "pointer_type"
                or "ref_type" or "alias_qualified_name" or "identifier"
                or "type_identifier" or "generic_type")
            {
                return NormalizeTypeText(child.Text ?? "?");
            }
        }

        return "?";
    }

    private static string NormalizeTypeText(string text)
    {
        // Collapse internal whitespace so "string?" / "Dictionary<string, int>" stay stable.
        var chars = text.Trim().ToCharArray();
        var written = 0;
        var prevSpace = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsWhiteSpace(c))
            {
                if (prevSpace)
                    continue;
                // Drop spaces around punctuation commonly found in type names.
                var prev = written > 0 ? chars[written - 1] : '\0';
                var next = i + 1 < chars.Length ? chars[i + 1] : '\0';
                if (prev is '<' or '>' or ',' or '(' or ')' or '[' or ']' or '.' or '?' or '!')
                    continue;
                if (next is '<' or '>' or ',' or '(' or ')' or '[' or ']' or '.' or '?' or '!')
                    continue;
                chars[written++] = ' ';
                prevSpace = true;
            }
            else
            {
                chars[written++] = c;
                prevSpace = false;
            }
        }

        return new string(chars, 0, written);
    }

    private static Node? FindNamedChild(Node parent, string type)
    {
        foreach (var child in parent.NamedChildren)
        {
            if (child.Type == type)
                return child;
        }

        return null;
    }

    private static string? GetNameFieldText(Node node)
    {
        try
        {
            var name = node.GetChildForField("name");
            return name is null ? null : name.Text;
        }
        catch
        {
            return null;
        }
    }

    private static string InferRelationshipEdgeKind(string sourceKind, string targetName, CodeSymbol? resolvedTarget)
    {
        if (sourceKind == "interface")
            return "inherits";

        // Prefer resolved local symbol kind over the I-prefix heuristic.
        if (resolvedTarget is not null)
        {
            if (resolvedTarget.Kind == "interface")
                return "implements";
            return "inherits";
        }

        // External / unresolved: fall back to conventional IFoo naming on the leaf.
        var simple = SimpleTypeName(targetName);
        if (simple.Length >= 2 && simple[0] == 'I' && char.IsUpper(simple[1]))
            return "implements";

        return "inherits";
    }

    /// <summary>Normalize type reference text: strip <c>global::</c> and generic arity/args.</summary>
    private static string NormalizeTypeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];
        var generic = trimmed.IndexOfAny(['<', '`']);
        if (generic >= 0)
            trimmed = trimmed[..generic];
        return trimmed;
    }

    /// <summary>
    /// Edge/reference display text: keep <c>global::</c> (Slice 3 ranking needs it), strip type args only.
    /// </summary>
    private static string PreserveTypeName(string name)
    {
        var trimmed = name.Trim();
        // Collapse whitespace around :: and .
        if (trimmed.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            var buffer = new char[trimmed.Length];
            var n = 0;
            foreach (var c in trimmed)
            {
                if (!char.IsWhiteSpace(c))
                    buffer[n++] = c;
            }

            trimmed = new string(buffer, 0, n);
        }

        var generic = trimmed.IndexOfAny(['<', '`']);
        if (generic >= 0)
            trimmed = trimmed[..generic];
        return trimmed;
    }

    /// <summary>
    /// Prefer <c>alias_qualified_name</c> parent text so <c>global::N.MyList</c> is preserved
    /// when the capture is only the inner <c>qualified_name</c>.
    /// </summary>
    private static string PreserveTypeReferenceText(Node nameNode)
    {
        var node = nameNode;
        if (nameNode.Parent is { Type: "alias_qualified_name" } alias)
            node = alias;
        return PreserveTypeName(node.Text ?? nameNode.Text ?? "");
    }

    /// <summary>
    /// Call target text: keep full member-access source, including a leading
    /// <c>global::</c> when the expression uses <c>alias_qualified_name</c>.
    /// </summary>
    private static string PreserveCallTargetText(Node nameNode)
    {
        var text = nameNode.Text ?? "";
        if (text.Contains("global::", StringComparison.Ordinal))
            return PreserveTypeName(text);

        // Capture may be an inner node; climb to include alias_qualified_name under the
        // outermost member_access (invocation function slot).
        for (var cur = nameNode; cur is not null; cur = cur.Parent)
        {
            if (cur.Type == "alias_qualified_name")
            {
                var top = nameNode;
                while (top.Parent is { Type: "member_access_expression" } parent)
                    top = parent;
                return CallTargetNormalization.StripThisBaseReceiver(PreserveTypeName(top.Text ?? cur.Text ?? text));
            }

            if (cur.Type is "invocation_expression" or "compilation_unit")
                break;
        }

        return CallTargetNormalization.StripThisBaseReceiver(PreserveTypeName(text));
    }

    private static string SimpleTypeName(string name)
    {
        var trimmed = NormalizeTypeName(name);
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }

    private static CodeSymbol? NearestContainingSymbol(IReadOnlyList<CodeSymbol> symbols, int startByte, bool preferCallContainers)
    {
        if (preferCallContainers)
        {
            var container = symbols
                .Where(s => CallContainerKinds.Contains(s.Kind)
                            && s.Span.StartByte <= startByte
                            && s.Span.EndByte >= startByte)
                .OrderByDescending(s => s.Span.StartByte)
                .FirstOrDefault();
            if (container is not null)
                return container;
        }

        return symbols
            .Where(s => s.Span.StartByte <= startByte && s.Span.EndByte >= startByte)
            .OrderByDescending(s => s.Span.StartByte)
            .FirstOrDefault();
    }

    private static (string TargetId, string Status) ResolveTarget(
        string targetName,
        IReadOnlyDictionary<string, CodeSymbol[]> localSymbolsByName,
        bool allowSimpleLeafFallback = true)
    {
        // Prefer exact key (simple names); fall back to simple leaf for FQN bases.
        if (localSymbolsByName.TryGetValue(targetName, out var matches) && matches.Length == 1)
            return (matches[0].Id, "local-symbol");
        if (matches is { Length: > 1 })
            return (targetName, "unresolved");

        if (!allowSimpleLeafFallback)
            return (targetName, "external-name");

        var simple = SimpleTypeName(targetName);
        if (!string.Equals(simple, targetName, StringComparison.Ordinal)
            && localSymbolsByName.TryGetValue(simple, out matches))
        {
            if (matches.Length == 1)
                return (matches[0].Id, "local-symbol");
            if (matches.Length > 1)
                return (targetName, "unresolved");
        }

        return (targetName, "external-name");
    }

    /// <summary>
    /// Resolve an override target, excluding the overriding method itself so a sole
    /// same-named method in the file cannot produce a self-loop edge.
    /// </summary>
    private static (string TargetId, string Status) ResolveOverrideTarget(
        string targetName,
        IReadOnlyDictionary<string, CodeSymbol[]> localSymbolsByName,
        CodeSymbol sourceSymbol)
    {
        if (!localSymbolsByName.TryGetValue(targetName, out var matches))
            return (targetName, "unresolved");

        var others = matches.Where(m => m.Id != sourceSymbol.Id).ToArray();
        if (others.Length == 1)
            return (others[0].Id, "local-symbol");

        // Zero remaining (would have been a self-loop) or ambiguous — leave unresolved.
        return (targetName, "unresolved");
    }

    private static SymbolSurface ExtractSymbolSurface(
        string language,
        PendingSymbol symbol,
        NamedExportTable namedExportBindings)
    {
        if (language.Equals("c-sharp", StringComparison.OrdinalIgnoreCase))
        {
            return new SymbolSurface(
                ExtractCSharpAccessibility(symbol.Kind, symbol.DefNode),
                null,
                ExtractSurfaceModifiers(symbol.DefNode, symbol.NameNode),
                ExtractSourceSignature(symbol.Kind, symbol.DefNode, symbol.NameNode));
        }

        if (language.Equals("typescript", StringComparison.OrdinalIgnoreCase)
            || language.Equals("tsx", StringComparison.OrdinalIgnoreCase))
        {
            return new SymbolSurface(
                ExtractTypescriptAccessibility(symbol.Kind, symbol.DefNode, symbol.NameNode),
                ExtractTypescriptExportStatus(symbol.DefNode, symbol.Name, namedExportBindings),
                ExtractSurfaceModifiers(symbol.DefNode, symbol.NameNode),
                ExtractSourceSignature(symbol.Kind, symbol.DefNode, symbol.NameNode));
        }

        return new SymbolSurface(null, null, null, null);
    }

    /// <summary>
    /// Reconcile surface metadata when a later partial declaration is merged into the
    /// first-seen type symbol (span/id stay with the first declaration).
    /// </summary>
    private static void MergePartialTypeSurface(
        List<SymbolEntry> entries,
        int canonIdx,
        string language,
        PendingSymbol additional,
        NamedExportTable namedExportBindings)
    {
        var surface = ExtractSymbolSurface(language, additional, namedExportBindings);
        var existing = entries[canonIdx].Symbol;

        var accessibility = existing.Accessibility;
        if (language.Equals("c-sharp", StringComparison.OrdinalIgnoreCase)
            && HasExplicitCSharpAccessibility(additional.DefNode)
            && surface.Accessibility is not null)
        {
            accessibility = surface.Accessibility;
        }
        else if (accessibility is null && surface.Accessibility is not null)
        {
            accessibility = surface.Accessibility;
        }

        // Prefer default-exported over exported when either declaration is exported.
        var exportStatus = PreferExportStatus(existing.ExportStatus, surface.ExportStatus);
        var modifiers = MergeModifierLists(existing.Modifiers, surface.Modifiers);
        // C# partial types may omit the keyword on some parts; still surface as partial.
        // TypeScript declaration merging (interfaces, etc.) is not the C# partial keyword.
        if (language.Equals("c-sharp", StringComparison.OrdinalIgnoreCase))
            modifiers = EnsureModifier(modifiers, "partial");

        entries[canonIdx] = entries[canonIdx] with
        {
            Symbol = existing with
            {
                Accessibility = accessibility,
                ExportStatus = exportStatus,
                Modifiers = modifiers,
            },
        };
    }

    private static string? PreferExportStatus(string? current, string? incoming)
    {
        if (incoming is null)
            return current;
        if (current is null)
            return incoming;
        if (current == "default-exported" || incoming == "default-exported")
            return "default-exported";
        if (current == "exported" || incoming == "exported")
            return "exported";
        return current;
    }

    private static IReadOnlyList<string>? MergeModifierLists(
        IReadOnlyList<string>? first,
        IReadOnlyList<string>? second)
    {
        if (first is null || first.Count == 0)
            return second is null || second.Count == 0 ? null : second.ToArray();
        if (second is null || second.Count == 0)
            return first.ToArray();

        var merged = new List<string>(first);
        foreach (var modifier in second)
        {
            if (!merged.Contains(modifier, StringComparer.Ordinal))
                merged.Add(modifier);
        }

        return merged.ToArray();
    }

    private static IReadOnlyList<string>? EnsureModifier(IReadOnlyList<string>? modifiers, string modifier)
    {
        if (modifiers is null || modifiers.Count == 0)
            return new[] { modifier };
        if (modifiers.Contains(modifier, StringComparer.Ordinal))
            return modifiers is string[] arr ? arr : modifiers.ToArray();
        var list = modifiers.ToList();
        list.Add(modifier);
        return list.ToArray();
    }

    private static string? ExtractCSharpAccessibility(string kind, Node declaration)
    {
        if (kind is "namespace" or "type-parameter")
            return null;

        // Static constructors have no accessibility concept in C#.
        if (kind == "constructor"
            && (HasModifier(declaration, "static") || HasAnonymousChildKeyword(declaration, "static")))
        {
            return null;
        }

        // Explicit interface implementations are not private class members; omit accessibility.
        if ((kind is "method" or "property" or "event" or "indexer")
            && FindNamedChild(declaration, "explicit_interface_specifier") is not null)
        {
            return null;
        }

        var explicitModifiers = DirectChildTexts(declaration, "modifier").ToHashSet(StringComparer.Ordinal);
        if (explicitModifiers.Contains("protected") && explicitModifiers.Contains("internal"))
            return "protected internal";
        if (explicitModifiers.Contains("private") && explicitModifiers.Contains("protected"))
            return "private protected";
        foreach (var accessibility in new[] { "public", "internal", "protected", "private", "file" })
        {
            if (explicitModifiers.Contains(accessibility))
                return accessibility;
        }

        if (kind == "enum-member")
            return "public";

        var containingType = FindContainingType(declaration);
        if (TypeKinds.Contains(kind))
            return containingType is null ? "internal" : containingType.Type == "interface_declaration" ? "public" : "private";

        if (containingType?.Type == "interface_declaration")
            return "public";

        // C# members and local functions have private effective accessibility when omitted.
        return "private";
    }

    private static bool HasExplicitCSharpAccessibility(Node declaration)
    {
        foreach (var text in DirectChildTexts(declaration, "modifier"))
        {
            if (text is "public" or "internal" or "protected" or "private" or "file")
                return true;
        }

        return false;
    }

    private static string ExtractRecordPositionalAccessibility(Node paramNode)
    {
        var explicitModifiers = DirectChildTexts(paramNode, "modifier").ToHashSet(StringComparer.Ordinal);
        if (explicitModifiers.Contains("protected") && explicitModifiers.Contains("internal"))
            return "protected internal";
        if (explicitModifiers.Contains("private") && explicitModifiers.Contains("protected"))
            return "private protected";
        foreach (var accessibility in new[] { "public", "internal", "protected", "private" })
        {
            if (explicitModifiers.Contains(accessibility))
                return accessibility;
        }

        // Record positional parameters are public properties by default.
        return "public";
    }

    private static string? ExtractTypescriptAccessibility(string kind, Node declaration, Node nameNode)
    {
        if (nameNode.Type == "private_property_identifier"
            || (nameNode.Text?.StartsWith('#') ?? false))
        {
            return "private";
        }

        var containingType = FindContainingType(declaration);
        if (containingType is null
            || !(containingType.Type is "class_declaration" or "abstract_class_declaration" or "interface_declaration"))
        {
            return null;
        }

        foreach (var child in declaration.NamedChildren)
        {
            if (child.Type == "accessibility_modifier"
                && child.Text is "public" or "protected" or "private")
            {
                return child.Text;
            }
        }

        foreach (var child in declaration.Children)
        {
            if (child.Text is "public" or "protected" or "private")
                return child.Text;
        }

        return kind is "method" or "constructor" or "property" or "field" ? "public" : null;
    }

    private static string ExtractTypescriptExportStatus(
        Node declaration,
        string name,
        NamedExportTable namedExportBindings)
    {
        string? directStatus = null;
        if (declaration.Parent is { Type: "export_statement" } exportStatement)
        {
            // Direct `export default class Foo` / `export class Foo`.
            directStatus = HasAnonymousChildKeyword(exportStatement, "default")
                ? "default-exported"
                : "exported";
        }

        var scope = GetTypescriptExportScope(declaration);
        string? tableStatus = null;
        if (IsBindingDirectlyInExportScope(declaration, scope))
            tableStatus = namedExportBindings.TryGetStatus(NodeKey.From(scope), name);

        // Prefer default-exported when a direct named export is also defaulted later
        // (`export class X {}; export default X;` or `export { X as default }`).
        var preferred = PreferExportStatus(directStatus, tableStatus);
        return preferred ?? "not-exported";
    }

    private static NamedExportTable BuildNamedExportBindings(IEnumerable<ExportNameSpan> exports)
    {
        var result = new NamedExportTable();
        foreach (var export in exports)
        {
            var scope = GetTypescriptExportScope(export.Node);
            result.Add(NodeKey.From(scope), export.Name, export.IsDefault);
        }

        return result;
    }

    private static bool IsExportSpecifierDefaultAlias(Node exportNameNode)
    {
        var specifier = FindAncestorOfType(exportNameNode, "export_specifier");
        if (specifier is null)
            return false;

        var alias = TryGetChildForField(specifier, "alias");
        if (alias is null)
            return false;

        if (string.Equals(alias.Text, "default", StringComparison.Ordinal))
            return true;

        // Grammar may emit an anonymous `default` token as the alias.
        return !alias.IsNamed && alias.Type == "default";
    }

    private static Node GetTypescriptExportScope(Node node)
    {
        var current = node.Parent;
        Node outermost = node;
        while (current is not null)
        {
            outermost = current;
            if (current.Type is "internal_module" or "module")
                return current;
            current = current.Parent;
        }

        return outermost;
    }

    private static bool IsBindingDirectlyInExportScope(Node declaration, Node scope)
    {
        var current = declaration.Parent;
        while (current is not null
               && !(current.StartIndex == scope.StartIndex && current.EndIndex == scope.EndIndex))
        {
            if (IsNamedScopeContainer(current))
                return false;
            // Bare blocks and similar lexical boundaries hide bindings from module export { name }.
            // The namespace/module body block is not a barrier (it is the scope body itself).
            if (IsLexicalExportBarrier(current) && !IsScopeBody(current, scope))
                return false;
            current = current.Parent;
        }

        return current is not null;
    }

    private static bool IsLexicalExportBarrier(Node node) =>
        node.Type is "statement_block" or "class_body" or "enum_body"
            or "for_statement" or "for_in_statement" or "for_of_statement"
            or "catch_clause" or "switch_body" or "switch_case" or "switch_default";

    private static bool IsScopeBody(Node block, Node scope)
    {
        var body = TryGetChildForField(scope, "body");
        return body is not null
            && body.StartIndex == block.StartIndex
            && body.EndIndex == block.EndIndex;
    }

    private static string[]? ExtractSurfaceModifiers(Node declaration, Node? nameNode = null)
    {
        var candidates = new List<Node> { declaration };
        var callable = FindCallableNode(declaration, nameNode);
        if (callable is not null
            && (callable.StartIndex != declaration.StartIndex || callable.EndIndex != declaration.EndIndex))
        {
            candidates.Add(callable);
        }

        // Single left-to-right scan so modifiers keep source order (no prepending abstract).
        var modifiers = new List<string>();
        foreach (var candidate in candidates)
        {
            foreach (var child in candidate.Children)
            {
                var text = child.Text;
                if (text is "static" or "abstract" or "sealed" or "partial" or "async"
                    or "override" or "virtual" or "readonly")
                {
                    if (!modifiers.Contains(text, StringComparer.Ordinal))
                        modifiers.Add(text);
                }
            }
        }

        // abstract_class_declaration / abstract_method_signature encode abstract in the node
        // type; add only when the keyword was not already present as a child.
        if (declaration.Type is "abstract_class_declaration" or "abstract_method_signature"
            && !modifiers.Contains("abstract", StringComparer.Ordinal))
        {
            modifiers.Add("abstract");
        }

        return modifiers.Count == 0 ? null : modifiers.ToArray();
    }

    private static string? ExtractSourceSignature(string kind, Node declaration, Node nameNode)
    {
        if (kind is "method" or "function" or "constructor")
        {
            var callable = FindCallableNode(declaration, nameNode) ?? declaration;
            var parameters = FindParameterNode(callable);
            var parameterText = parameters?.Text;
            if (string.IsNullOrWhiteSpace(parameterText))
                parameterText = "()";
            else if (!(parameters!.Type is "parameter_list" or "formal_parameters"))
                parameterText = $"({parameterText})";

            if (kind == "constructor")
                return parameterText;

            var returnType = FindReturnTypeNode(callable, parameters);
            return returnType is null || string.IsNullOrWhiteSpace(returnType.Text)
                ? parameterText
                : $"{parameterText} -> {TrimTypeAnnotation(returnType.Text!)}";
        }

        return kind is "property" or "field" or "event"
            ? ExtractTypedSignature(declaration)
            : null;
    }

    private static string? ExtractTypedSignature(Node declaration)
    {
        var type = TryGetChildForField(declaration, "type")
                   ?? TryGetChildForField(declaration, "returns")
                   ?? FindNamedChild(declaration, "type_annotation");

        if (type is null)
        {
            var variableDeclaration = FindFirstDescendant(declaration, "variable_declaration");
            type = variableDeclaration is null ? null : TryGetChildForField(variableDeclaration, "type");
        }

        if (type is null && declaration.Type == "parameter")
            type = TryGetChildForField(declaration, "type");

        return type is null || string.IsNullOrWhiteSpace(type.Text)
            ? null
            : TrimTypeAnnotation(type.Text!);
    }

    private static Node? FindCallableNode(Node declaration, Node? nameNode)
    {
        if (declaration.Type is "method_declaration" or "local_function_statement"
            or "constructor_declaration" or "method_definition" or "method_signature"
            or "abstract_method_signature" or "function_declaration" or "function_signature"
            or "generator_function_declaration" or "function_expression" or "generator_function"
            or "arrow_function")
        {
            return declaration;
        }

        if (!(declaration.Type is "lexical_declaration" or "variable_declaration"))
            return null;

        var declarator = nameNode;
        while (declarator?.Parent is not null && declarator.Type != "variable_declarator")
            declarator = declarator.Parent;
        if (declarator?.Type != "variable_declarator")
            return null;

        return TryGetChildForField(declarator, "value");
    }

    private static Node? FindParameterNode(Node callable)
    {
        if (callable.Type is "parameter_list" or "formal_parameters")
            return callable;

        return TryGetChildForField(callable, "parameters")
               ?? FindNamedChild(callable, "parameter_list")
               ?? FindNamedChild(callable, "formal_parameters")
               ?? TryGetChildForField(callable, "parameter");
    }

    private static Node? FindReturnTypeNode(Node callable, Node? parameters)
    {
        var field = TryGetChildForField(callable, "returns")
                    ?? TryGetChildForField(callable, "return_type");
        if (field is not null)
            return field;

        foreach (var child in callable.NamedChildren)
        {
            if (child.Type == "type_annotation"
                && (parameters is null || child.StartIndex >= parameters.EndIndex))
            {
                return child;
            }
        }

        return null;
    }

    private static Node? TryGetChildForField(Node node, string field)
    {
        try { return node.GetChildForField(field); }
        catch { return null; }
    }

    private static Node? FindFirstDescendant(Node node, string type)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == type)
                return child;
            var nested = FindFirstDescendant(child, type);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static Node? FindContainingType(Node declaration)
    {
        var current = declaration.Parent;
        while (current is not null)
        {
            if (current.Type is "class_declaration" or "abstract_class_declaration"
                or "interface_declaration" or "struct_declaration" or "record_declaration"
                or "enum_declaration")
            {
                return current;
            }
            current = current.Parent;
        }

        return null;
    }

    private static IEnumerable<string> DirectChildTexts(Node node, string childType)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == childType && !string.IsNullOrWhiteSpace(child.Text))
                yield return child.Text!;
        }
    }

    private static string TrimTypeAnnotation(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith(':') ? trimmed[1..].TrimStart() : trimmed;
    }

    /// <summary>
    /// TreeSitter.DotNet's <c>Parser.Parse(string)</c> returns node StartIndex/EndIndex as
    /// .NET string (UTF-16 code unit) offsets, not UTF-8 byte offsets. Convert via SourceText
    /// so artifact StartByte/EndByte stay UTF-8 correct for non-ASCII source.
    /// </summary>
    private static SourceSpan SpanFromNode(SourceText source, Node node)
    {
        var start = Math.Clamp(node.StartIndex, 0, source.Text.Length);
        var end = Math.Clamp(node.EndIndex, start, source.Text.Length);
        return source.SpanFor(start, end - start);
    }

    private static int CharIndexToByte(SourceText source, int charIndex) =>
        source.ByteOffsetForChar(Math.Clamp(charIndex, 0, source.Text.Length));

    private static ProviderProvenance ForFact(ProviderProvenance provenance, string factKind, double? confidence = null)
    {
        var requested = confidence ?? provenance.Confidence;
        var clamped = provenance.ProviderId switch
        {
            TreeSitterQueryRegistry.TreeSitterQueryProviderId => Math.Min(requested, 0.95),
            "hypa-pattern" => Math.Min(requested, 0.79),
            "regex-fallback" => Math.Min(requested, 0.49),
            _ => requested,
        };
        return provenance with { FactKind = factKind, Confidence = clamped };
    }

    private static Node? FindAncestorOfType(Node node, string type)
    {
        var n = node;
        while (n is not null)
        {
            if (n.Type == type)
                return n;
            n = n.Parent;
        }

        return null;
    }

    private static bool HasModifier(Node? declaration, string modifier)
    {
        if (declaration is null)
            return false;

        foreach (var child in declaration.NamedChildren)
        {
            if (child.Type == "modifier" && child.Text == modifier)
                return true;
        }

        return false;
    }

    private static bool HasOverrideModifierNode(Node declaration)
    {
        foreach (var child in declaration.NamedChildren)
        {
            if (child.Type == "override_modifier")
                return true;
        }

        return false;
    }

    private static bool HasAnonymousChildKeyword(Node declaration, string keyword)
    {
        foreach (var child in declaration.Children)
        {
            if (!child.IsNamed && child.Type == keyword)
                return true;
        }

        return false;
    }

    /// <summary>Strip quotes from TS import module specifier strings.</summary>
    private static string NormalizeImportName(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2)
        {
            var open = t[0];
            var close = t[^1];
            if ((open == '"' && close == '"')
                || (open == (char)39 && close == (char)39)
                || (open == '`' && close == '`'))
            {
                return t[1..^1];
            }
        }

        return t;
    }

    private static bool IsKeyword(string name) => name is
        "if" or "for" or "foreach" or "while" or "switch" or "return" or "using" or "new"
        or "typeof" or "sizeof" or "nameof" or "checked" or "unchecked" or "default"
        or "base" or "this" or "await" or "throw" or "lock" or "fixed";

    private readonly record struct NodeKey(int Start, int End, string Type)
    {
        public static NodeKey From(Node node) => new(node.StartIndex, node.EndIndex, node.Type);
    }

    private sealed record PendingSymbol(string Kind, string Name, Node SpanNode, Node NameNode, Node DefNode);
    private sealed record SymbolEntry(CodeSymbol Symbol, Node DefNode, Node NameNode);
    private sealed record NameSpan(string Name, Node Node);
    private sealed record ExportNameSpan(string Name, Node Node, bool IsDefault);
    private sealed record SymbolSurface(
        string? Accessibility,
        string? ExportStatus,
        IReadOnlyList<string>? Modifiers,
        string? Signature);

    /// <summary>
    /// Module/namespace-scope named export bindings from <c>export { name }</c> and
    /// <c>export default Identifier</c>. Prefer default when both forms appear.
    /// </summary>
    private sealed class NamedExportTable
    {
        private readonly Dictionary<(NodeKey Scope, string Name), bool> _isDefault = new();

        public void Add(NodeKey scope, string name, bool isDefault)
        {
            var key = (scope, name);
            if (_isDefault.TryGetValue(key, out var existing))
                _isDefault[key] = existing || isDefault;
            else
                _isDefault[key] = isDefault;
        }

        public string? TryGetStatus(NodeKey scope, string name) =>
            _isDefault.TryGetValue((scope, name), out var isDefault)
                ? isDefault ? "default-exported" : "exported"
                : null;
    }
    private sealed record BaseCapture(
        string SourceName,
        string TargetName,
        Node TargetNode,
        Node? SourceDeclNode,
        bool PreferImplements = false);
    private sealed record PrimaryCtorCapture(string TypeName, Node ParameterList, Node TypeNode);
    private sealed record RecordPositionalCapture(string OwnerName, string Name, Node ParamNode, Node NameNode, Node OwnerNode);
}
