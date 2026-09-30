using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Services;

/// <summary>
/// Deep Code Graph Slice 3 — second-pass, project-wide edge resolution.
/// After per-file extraction, builds a scope/import-aware symbol table and upgrades
/// unresolved <c>calls</c>/<c>inherits</c>/<c>implements</c>/<c>imports</c>/<c>overrides</c>
/// edges to internal symbol moniker IDs when the target is unambiguous.
/// </summary>
/// <remarks>
/// <para>
/// Framework/BCL names with no project match remain <c>external-name</c>. Project hits that
/// are out of scope or ambiguous remain <c>unresolved</c>. Does not chase zero dangling edges.
/// </para>
/// <para>
/// <b>Call resolution policy (Phase 3):</b> unqualified calls bind by simple name + C# file
/// scope (same/enclosing namespace or exact <c>using</c>) when unique. There is no receiver
/// type-flow analysis — a unique same-namespace <c>Helper()</c> may bind across types. Member
/// access / dotted call targets (e.g. <c>Console.WriteLine</c>) bind only when the full
/// scope path matches a project callable (namespace-relative FQN); otherwise they stay
/// <c>external-name</c> so BCL/framework calls never attach to unrelated project methods.
/// </para>
/// <para>
/// <b>Incremental indexing:</b> only full-project passes (export and <c>IndexFullAsync</c>)
/// run this resolver. Incremental CLI index without <c>--full</c> is deferred to Phase 5.
/// </para>
/// </remarks>
public sealed class CrossFileDependencyResolver
{
    private static readonly HashSet<string> ResolvableEdgeKinds = new(StringComparer.Ordinal)
    {
        "calls", "inherits", "implements", "imports", "overrides",
        // KG-H2: markdown section → symbol/file references (doc-side span, project-wide bind).
        "documents",
    };

    /// <summary>
    /// Symbol kinds eligible as doc-section reference targets. Excludes headings/code-blocks
    /// and structural noise so prose `` `Worker` `` cannot bind to a markdown heading.
    /// </summary>
    private static readonly HashSet<string> DocReferenceTargetKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "struct", "enum", "record", "type", "type-alias",
        "method", "function", "constructor", "property", "field", "event", "namespace",
    };

    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "struct", "enum", "record", "type",
    };

    private static readonly HashSet<string> CallableKinds = new(StringComparer.Ordinal)
    {
        "method", "function", "constructor",
    };

    private static readonly HashSet<string> RelationshipEdgeKinds = new(StringComparer.Ordinal)
    {
        "inherits", "implements",
    };

    /// <summary>
    /// Resolve cross-file dependency targets across all parsed documents.
    /// Preserves already-resolved <c>local-symbol</c> edges and stable symbol IDs.
    /// </summary>
    public IReadOnlyList<CodeStructureDocument> Resolve(IReadOnlyList<CodeStructureDocument> documents)
    {
        if (documents.Count == 0)
            return documents;

        var table = ProjectSymbolTable.Build(documents);
        var resolved = new CodeStructureDocument[documents.Count];
        for (var i = 0; i < documents.Count; i++)
            resolved[i] = ResolveDocument(documents[i], table);

        return resolved;
    }

    private static CodeStructureDocument ResolveDocument(CodeStructureDocument document, ProjectSymbolTable table)
    {
        if (document.DependencyEdges.Count == 0)
            return document;

        var fileCtx = table.ContextFor(document.File.RelativePath);
        var changed = false;
        var edges = new CodeDependencyEdge[document.DependencyEdges.Count];

        for (var i = 0; i < document.DependencyEdges.Count; i++)
        {
            var edge = document.DependencyEdges[i];
            var next = TryResolveEdge(edge, fileCtx, table);
            edges[i] = next;
            if (!ReferenceEquals(next, edge))
                changed = true;
        }

        if (!changed)
            return document;

        // Stable order only — do not silently drop edges on Id collision.
        return document with
        {
            DependencyEdges = edges
                .OrderBy(e => e.SourceSpan?.StartByte ?? int.MaxValue)
                .ThenBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.TargetName ?? e.TargetId, StringComparer.Ordinal)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static CodeDependencyEdge TryResolveEdge(
        CodeDependencyEdge edge,
        FileResolutionContext fileCtx,
        ProjectSymbolTable table)
    {
        if (!ResolvableEdgeKinds.Contains(edge.Kind))
            return edge;

        // Already bound to a symbol moniker — leave intact when the target still exists
        // in the project table (same-file pass and prior full resolution own these).
        // If the moniker is gone (delete/rename under incremental re-resolution), reset
        // to an unbound name-based form and re-resolve so dependent files flip status
        // and confidence matches a fresh extract+resolve pass (unbound type edges are 0.8).
        //
        // Documents edges are exempt: always re-resolve from TargetName so unique→duplicate
        // project growth (and type-vs-constructor / overload policy) converges with a full
        // re-export of the end state (D5). A still-present moniker is not sufficient.
        if (edge.TargetResolutionStatus == "local-symbol"
            && edge.TargetId.StartsWith("sym_", StringComparison.Ordinal))
        {
            if (edge.Kind != "documents" && table.TryGet(edge.TargetId, out _))
                return edge;

            var staleName = edge.TargetName;
            if (string.IsNullOrWhiteSpace(staleName))
                staleName = edge.TargetId;
            edge = edge with
            {
                TargetId = staleName,
                TargetResolutionStatus = "unresolved",
                // Reset to the extractor's unbound confidence for this provider×kind so a
                // subsequent StatusEdge/Bind converges with a fresh extract+resolve pass.
                Provenance = edge.Provenance with
                {
                    Confidence = UnboundEdgeConfidence(
                        edge.Provenance.ProviderId, edge.Kind, edge.Provenance.Confidence),
                },
                Id = ReId(edge, staleName),
            };
        }

        // Previously bound file path — keep when still indexed (non-documents kinds);
        // documents edges always re-resolve from TargetName for D5 convergence.
        if (edge.TargetResolutionStatus == "local-file")
        {
            var boundPath = edge.TargetId;
            if (edge.Kind != "documents" && table.HasIndexedFile(boundPath))
                return edge;

            var stalePath = edge.TargetName;
            if (string.IsNullOrWhiteSpace(stalePath))
                stalePath = boundPath;
            edge = edge with
            {
                TargetId = stalePath,
                TargetResolutionStatus = "unresolved",
                Provenance = edge.Provenance with
                {
                    Confidence = UnboundEdgeConfidence(
                        edge.Provenance.ProviderId, edge.Kind, edge.Provenance.Confidence),
                },
                Id = ReId(edge, stalePath),
            };
        }

        var targetName = edge.TargetName;
        if (string.IsNullOrWhiteSpace(targetName))
            targetName = edge.TargetId;
        if (string.IsNullOrWhiteSpace(targetName))
            return edge;

        // global:: forces global-qualified lookup — never apply namespace-relative prefix ranking
        // (NormalizeTypeName strips the alias, so detect it on the raw target first).
        var isGlobalQualified = targetName.TrimStart().StartsWith("global::", StringComparison.Ordinal);
        var normalized = NormalizeTypeName(targetName);
        var simpleName = SimpleName(normalized);
        var isQualified = normalized.Contains('.', StringComparison.Ordinal);

        // Scope from the edge source's containing namespace when available (type/method),
        // so multi-namespace files do not treat sibling namespaces as mutually visible.
        // Computed early: namespace-relative qualified matching needs it.
        var sourceNs = table.NamespaceOfSymbol(edge.SourceId);
        // global:: targets match exact FQN only (null source ns disables relative prefixes).
        var qualifiedMatchNs = isGlobalQualified ? null : sourceNs;

        // TypeScript/TSX relative module imports (./foo, ../bar) are not C# namespaces.
        // Try file-path moniker join before the generic namespace lookup path.
        if (edge.Kind == "imports"
            && IsTypescriptFamily(fileCtx.Language)
            && IsRelativeModuleSpecifier(normalized))
        {
            var moduleHit = table.LookupRelativeModule(fileCtx.RelativePath, normalized);
            if (moduleHit is not null)
                return Bind(edge, moduleHit);
            return StatusEdge(edge, targetName, "external-name");
        }

        // KG-H2: section→symbol/file. Deterministic, no loose same-name fallback across
        // ambiguous candidates. File-path targets use local-file; symbol targets require
        // unique project match (qualified = scope-path identity, simple = unique leaf).
        // Pass raw targetName — signature split must run before NormalizeTypeName so
        // generic parameter types (Dictionary<string,int>) are not truncated at '<'.
        if (edge.Kind == "documents")
            return ResolveDocumentsEdge(edge, targetName, table);

        IReadOnlyList<IndexedSymbol> candidates = edge.Kind switch
        {
            "imports" => table.LookupNamespaces(normalized, simpleName),
            "inherits" or "implements" => table.LookupTypes(simpleName),
            "calls" or "overrides" => table.LookupCallables(simpleName),
            _ => [],
        };

        if (candidates.Count == 0)
            return StatusEdge(edge, targetName, EmptyStatus(edge.Kind, hadProjectCandidates: false));

        // Member-access / dotted call targets: only strong scope-path identity may bind.
        // Never fall through to simple-name IsInScope (BCL Console.WriteLine vs project WriteLine).
        if (edge.Kind == "calls" && isQualified)
        {
            candidates = PreferInnermostQualifiedMatches(candidates, normalized, qualifiedMatchNs);
            if (candidates.Count == 1)
                return Bind(edge, candidates[0]);
            if (candidates.Count == 0)
                return StatusEdge(edge, targetName, "external-name");
            return StatusEdge(edge, targetName, "unresolved");
        }

        // Qualified name filter (e.g. Corpus.Graph.WorkerContract, incl. namespace-relative).
        // When multiple prefixes match, keep only the innermost (C# closest-enclosing) rank.
        // global:: disables relative ranking so N.MyList is not outranked by N.N.MyList.
        if (isQualified)
        {
            candidates = PreferInnermostQualifiedMatches(candidates, normalized, qualifiedMatchNs);
            if (candidates.Count == 0)
                return StatusEdge(edge, targetName, EmptyStatus(edge.Kind, hadProjectCandidates: true));
        }

        // Collapse multi-file partials / multi-file namespace decls for type/import targets only.
        // Callables must never collapse — overloads and partial methods keep distinct monikers.
        if (edge.Kind is "inherits" or "implements" or "imports")
            candidates = CollapseLogicalDuplicates(candidates);

        // Override: bind only via declared base chain (inherits/implements of the source type).
        // Never fall through to arbitrary same-namespace callables. Do not collapse overloads.
        if (edge.Kind == "overrides")
            return ResolveOverrideEdge(edge, targetName, candidates, table, fileCtx);

        // FQN is self-scoping in C# — once unique after qualified match, bind without using.
        if (isQualified && candidates.Count == 1 && edge.Kind is not "imports")
            return Bind(edge, candidates[0]);

        // Import edges target namespaces themselves — no circular import gate.
        if (edge.Kind == "imports")
        {
            if (candidates.Count == 1)
                return Bind(edge, candidates[0]);
            return StatusEdge(edge, targetName, "unresolved");
        }

        var inScope = candidates.Where(c => IsInScope(c, fileCtx, sourceNs)).ToArray();
        if (inScope.Length == 0)
        {
            // Project hit(s) exist but none are visible from this file → unresolved (not BCL).
            return StatusEdge(edge, targetName, EmptyStatus(edge.Kind, hadProjectCandidates: true));
        }

        IReadOnlyList<IndexedSymbol> pool = inScope;

        // Prefer same-file when still ambiguous.
        var sameFile = pool.Where(c => c.FilePath == fileCtx.RelativePath).ToArray();
        if (sameFile.Length == 1)
            return Bind(edge, sameFile[0]);
        if (sameFile.Length > 1)
            pool = sameFile;

        // Prefer same containing namespace as the source symbol when available.
        if (sourceNs is not null && pool.Count > 1)
        {
            var sameNs = pool.Where(c => c.ContainingNamespace == sourceNs).ToArray();
            if (sameNs.Length == 1)
                return Bind(edge, sameNs[0]);
            if (sameNs.Length > 1)
                pool = sameNs;
        }

        // Prefer same containing type for callables (reduces cross-type false binds a bit).
        if (edge.Kind == "calls" && pool.Count > 1)
        {
            var sourceParent = table.ParentIdOf(edge.SourceId);
            if (sourceParent is not null)
            {
                var sameType = pool.Where(c => c.Symbol.ParentId == sourceParent).ToArray();
                if (sameType.Length == 1)
                    return Bind(edge, sameType[0]);
                if (sameType.Length > 1)
                    pool = sameType;
            }
        }

        if (pool.Count == 1)
            return Bind(edge, pool[0]);

        return StatusEdge(edge, targetName, "unresolved");
    }

    /// <summary>
    /// Override resolution requires a strong declared-base match. No IsInScope fallthrough
    /// to unrelated same-namespace methods. Unqualified bases must resolve to a unique
    /// in-scope project type first — bare name alone never binds out-of-scope types.
    /// </summary>
    private static CodeDependencyEdge ResolveOverrideEdge(
        CodeDependencyEdge edge,
        string targetName,
        IReadOnlyList<IndexedSymbol> candidates,
        ProjectSymbolTable table,
        FileResolutionContext fileCtx)
    {
        candidates = candidates
            .Where(c => c.Symbol.Id != edge.SourceId)
            .ToArray();

        if (candidates.Count == 0)
            return StatusEdge(edge, targetName, "unresolved");

        var sourceParentId = table.ParentIdOf(edge.SourceId);
        if (sourceParentId is null)
            return StatusEdge(edge, targetName, "unresolved");

        var baseHints = table.BaseTypeHints(sourceParentId);
        // No declared inherits/implements on the source type → leave unresolved
        // (same-file extractor may already have set local-symbol; that path returns earlier).
        if (baseHints.Count == 0)
            return StatusEdge(edge, targetName, "unresolved");

        // Strong: moniker Id or FQN parent scope. Unqualified: only via unique in-scope type.
        // Expand partial-type groups so a method declared on a non-canonical partial file still matches.
        var allowedParentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hint in baseHints)
        {
            if (hint.Kind == BaseHintKind.SymbolId)
            {
                foreach (var id in table.PartialGroupIds(hint.Value))
                    allowedParentIds.Add(id);
            }
        }

        // Scope against the source *type's* containing namespace + file usings — not every
        // namespace declared in the document (multi-namespace same-file files).
        var sourceTypeNs = table.NamespaceOfSymbol(sourceParentId);
        foreach (var typeId in ResolveInScopeUnqualifiedBaseTypeIds(baseHints, fileCtx, table, sourceTypeNs))
        {
            foreach (var id in table.PartialGroupIds(typeId))
                allowedParentIds.Add(id);
        }

        var strong = candidates
            .Where(c => MatchesDeclaredBaseStrong(c, baseHints, allowedParentIds, table, sourceTypeNs))
            .ToArray();

        // Hints exist but no project base member matches → unresolved (not arbitrary Run).
        if (strong.Length == 0)
            return StatusEdge(edge, targetName, "unresolved");

        if (strong.Length > 1)
        {
            var classBases = strong
                .Where(c => !IsInterfaceContainer(c, table))
                .ToArray();
            if (classBases.Length > 0)
                strong = classBases;
        }

        if (strong.Length == 1)
            return Bind(edge, strong[0]);

        // Overloads on the declared base — leave unresolved.
        return StatusEdge(edge, targetName, "unresolved");
    }

    /// <summary>
    /// Resolve unqualified base names (e.g. <c>: Base</c>) to unique project types visible
    /// from the source type's containing namespace + file usings. Sibling namespaces in the
    /// same file (without a using) do not count as in-scope.
    /// </summary>
    private static HashSet<string> ResolveInScopeUnqualifiedBaseTypeIds(
        IReadOnlyList<BaseTypeHint> hints,
        FileResolutionContext fileCtx,
        ProjectSymbolTable table,
        string? sourceContainingNamespace)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hint in hints)
        {
            if (hint.Kind != BaseHintKind.UnqualifiedName)
                continue;

            var types = table.LookupTypes(hint.Value);
            var inScope = types
                .Where(t => IsInScope(t, fileCtx, sourceContainingNamespace))
                .ToArray();
            if (inScope.Length == 1)
                ids.Add(inScope[0].Symbol.Id);
        }

        return ids;
    }

    /// <summary>
    /// Strong declared-base identity for override bypass:
    /// (1) parent type moniker Id ∈ allowed set (from resolved inherits or in-scope unqualified),
    /// (2) parent full scope path equals a <b>qualified</b> hint (segment-aware).
    /// Bare UnqualifiedName is never enough by itself — see
    /// <see cref="ResolveInScopeUnqualifiedBaseTypeIds"/>.
    /// </summary>
    private static bool MatchesDeclaredBaseStrong(
        IndexedSymbol method,
        IReadOnlyList<BaseTypeHint> hints,
        HashSet<string> allowedParentIds,
        ProjectSymbolTable table,
        string? sourceContainingNamespace)
    {
        if (method.Symbol.ParentId is null || !table.TryGet(method.Symbol.ParentId, out var parent))
            return false;

        if (allowedParentIds.Contains(method.Symbol.ParentId)
            || allowedParentIds.Contains(parent.Symbol.Id))
            return true;

        var parentScope = parent.ScopePath;
        foreach (var hint in hints)
        {
            if (hint.Kind == BaseHintKind.GlobalQualifiedName)
            {
                // global:: base — exact FQN only, no namespace-relative prefix ranking.
                if (SegmentsEqual(parentScope, hint.Value))
                    return true;
                continue;
            }

            if (hint.Kind != BaseHintKind.QualifiedName)
                continue;
            // Full parent scope must match the FQN (exact or namespace-relative segments); not bare leaf.
            if (ScopeMatchesQualified(parentScope, hint.Value, sourceContainingNamespace))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Collapse multi-file partial types / multi-file namespace decls that share
    /// (Kind, ScopePath) — ScopePath includes generic arity (<c>Box`1</c>) so distinct
    /// arities never merge. Callables must not use this.
    /// </summary>
    private static IReadOnlyList<IndexedSymbol> CollapseLogicalDuplicates(IReadOnlyList<IndexedSymbol> candidates)
    {
        if (candidates.Count <= 1)
            return candidates;

        return candidates
            .GroupBy(c => (c.Symbol.Kind, c.ScopePath), NamedScopeComparer.Instance)
            .Select(g => g.OrderBy(x => x.Symbol.Id, StringComparer.Ordinal).First())
            .ToArray();
    }

    private static CodeDependencyEdge Bind(CodeDependencyEdge edge, IndexedSymbol target)
    {
        var kind = edge.Kind;
        // Correct inherits/implements once we know the target kind.
        if (kind is "inherits" or "implements")
        {
            kind = target.Symbol.Kind == "interface" ? "implements" : "inherits";
        }

        var targetId = target.Symbol.Id;
        var id = edge.SourceSpan is { } span
            ? CodeStableId.ForEdge(edge.SourceId, targetId, kind, span.StartByte)
            : CodeStableId.ForEdge(edge.SourceId, targetId, kind);

        // Raise confidence for a successful bind, but never break provenance band caps:
        // hypa-pattern ≤ 0.79, regex-fallback < 0.50, AST-grounded may reach ≥ 0.88.
        var confidence = CapBindConfidence(edge.Provenance.ProviderId, edge.Provenance.Confidence);
        return edge with
        {
            Id = id,
            Kind = kind,
            TargetId = targetId,
            TargetResolutionStatus = "local-symbol",
            Provenance = edge.Provenance with
            {
                Confidence = confidence,
                FactKind = edge.Provenance.FactKind,
            },
        };
    }

    /// <summary>
    /// Bind a doc-section edge to an indexed file path (not a symbol moniker).
    /// Status vocabulary is additive: <c>local-file</c> is only used for documents edges.
    /// </summary>
    private static CodeDependencyEdge BindFile(CodeDependencyEdge edge, string relativePath)
    {
        var id = edge.SourceSpan is { } span
            ? CodeStableId.ForEdge(edge.SourceId, relativePath, edge.Kind, span.StartByte)
            : CodeStableId.ForEdge(edge.SourceId, relativePath, edge.Kind);
        var confidence = CapBindConfidence(edge.Provenance.ProviderId, edge.Provenance.Confidence);
        return edge with
        {
            Id = id,
            TargetId = relativePath,
            TargetName = edge.TargetName ?? relativePath,
            TargetResolutionStatus = "local-file",
            Provenance = edge.Provenance with
            {
                Confidence = confidence,
                FactKind = edge.Provenance.FactKind,
            },
        };
    }

    /// <summary>
    /// Resolve a markdown <c>documents</c> edge. Policy is deliberately strict to avoid
    /// the same-name false-bind class of bugs: simple names bind only when unique among
    /// eligible kinds project-wide; qualified names require scope-path identity.
    /// Callable overloads stay ambiguous unless a signature-bearing form pins one.
    /// Simple names never bind constructors (type-like symbols win; ctors need qualified
    /// and/or signature form).
    /// </summary>
    private static CodeDependencyEdge ResolveDocumentsEdge(
        CodeDependencyEdge edge,
        string targetName,
        ProjectSymbolTable table)
    {
        // Signature split on the raw target first — NormalizeTypeName truncates at '<' and
        // would destroy generic parameter types (Dictionary<string,int>). Status TargetId
        // keeps the original targetName for display/unresolved identity.
        var raw = targetName.Trim();
        if (raw.StartsWith("global::", StringComparison.Ordinal))
            raw = raw["global::".Length..];

        var (nameWithoutSig, parameterTypes) = SplitCallableSignatureForm(raw);
        // Normalize only the name portion (strip type-arg lists on type names / global::).
        var normalizedName = NormalizeTypeName(nameWithoutSig);
        // Collapse param whitespace so prose `Run(Dictionary<string, int>)` matches monikers.
        if (parameterTypes is not null)
            parameterTypes = CollapseParameterTypeWhitespace(parameterTypes);

        var pathLookup = normalizedName;
        var isQualifiedName = normalizedName.Contains('.', StringComparison.Ordinal);
        var simple = SimpleName(normalizedName);

        // File targets: path-separator shapes (from markdown links) always go through the
        // indexed-file set. Bare extension names like `Worker.cs` only bind as files when that
        // exact relative path is indexed — otherwise they fall through to symbol resolution so
        // inline-code filename-shaped tokens never steal a unique symbol of a different name.
        // Signature forms are never path-shaped.
        if (parameterTypes is null)
        {
            var path = pathLookup.Replace('\\', '/');
            var pathShaped = LooksLikeFilePathTarget(pathLookup);
            if (pathShaped || table.HasIndexedFile(path))
            {
                if (table.HasIndexedFile(path))
                    return BindFile(edge, path);
                if (pathShaped)
                    return StatusEdge(edge, path, "unresolved");
                // Bare name not indexed as a file — continue to symbol resolution.
            }
        }

        IReadOnlyList<IndexedSymbol> candidates;
        if (isQualifiedName)
        {
            // Exact or namespace-relative scope-path identity only — never leaf-only fallback.
            candidates = table.LookupDocTargetsByQualifiedName(normalizedName);
            if (parameterTypes is not null)
                candidates = FilterByCallableSignature(candidates, parameterTypes);
            if (candidates.Count == 1)
                return Bind(edge, candidates[0]);
            if (candidates.Count == 0)
                return StatusEdge(edge, targetName, "external-name");
            return StatusEdge(edge, targetName, "unresolved");
        }

        // Simple name: unique eligible symbol project-wide, or stay unresolved.
        // No import/namespace scope — docs have no language imports to honor.
        // Constructors are excluded from the simple-name pool (type wins over same-name ctor).
        // Signature form re-includes constructors so `Foo(int)` can pin a unique ctor.
        candidates = table.LookupDocTargetsBySimpleName(simple, includeConstructors: parameterTypes is not null);
        if (parameterTypes is not null)
            candidates = FilterByCallableSignature(candidates, parameterTypes);
        if (candidates.Count == 1)
            return Bind(edge, candidates[0]);
        if (candidates.Count == 0)
            return StatusEdge(edge, targetName, "external-name");
        return StatusEdge(edge, targetName, "unresolved");
    }

    /// <summary>
    /// Split a callable signature form <c>Name(paramTypes)</c> into name + moniker parameter
    /// types. Returns <c>parameterTypes: null</c> when no trailing <c>(...)</c> is present.
    /// Empty parens yield <c>""</c> (matches parameterless monikers).
    /// Must run on the raw target before <see cref="NormalizeTypeName"/> so nested
    /// <c>&lt;…&gt;</c> inside the parameter list survive.
    /// </summary>
    private static (string Name, string? ParameterTypes) SplitCallableSignatureForm(string name)
    {
        var open = name.IndexOf('(');
        if (open <= 0 || !name.EndsWith(")", StringComparison.Ordinal))
            return (name, null);
        return (name[..open], name[(open + 1)..^1]);
    }

    /// <summary>
    /// Collapse whitespace inside moniker-style parameter-type lists so doc prose with
    /// spaces after commas matches extractor monikers (<c>Dictionary&lt;string,int&gt;</c>).
    /// </summary>
    private static string CollapseParameterTypeWhitespace(string parameterTypes)
    {
        if (parameterTypes.Length == 0)
            return parameterTypes;
        return string.Concat(parameterTypes.Where(static c => !char.IsWhiteSpace(c)));
    }

    /// <summary>
    /// Keep only callables whose moniker parameter-types suffix matches
    /// <paramref name="parameterTypes"/> (the comma-separated form used by
    /// <see cref="CodeSymbolMoniker"/>). Non-callables are dropped when a signature form is
    /// present — a type cannot satisfy <c>Foo(int)</c>.
    /// </summary>
    private static IReadOnlyList<IndexedSymbol> FilterByCallableSignature(
        IReadOnlyList<IndexedSymbol> candidates,
        string parameterTypes)
    {
        if (candidates.Count == 0)
            return candidates;

        var hits = new List<IndexedSymbol>();
        foreach (var c in candidates)
        {
            if (!CallableKinds.Contains(c.Symbol.Kind))
                continue;
            var expectedId = CodeStableId.ForSymbol(
                c.FilePath, c.Symbol.Kind, c.ScopePath, parameterTypes);
            if (string.Equals(c.Symbol.Id, expectedId, StringComparison.Ordinal))
                hits.Add(c);
        }

        return hits;
    }

    /// <summary>
    /// True only for workspace-relative path shapes (contain <c>/</c> or <c>\</c>).
    /// Extension-only names like <c>Worker.cs</c> are <b>not</b> path-shaped — they may be
    /// symbols or same-dir file links resolved via <see cref="ProjectSymbolTable.HasIndexedFile"/>.
    /// </summary>
    private static bool LooksLikeFilePathTarget(string name) =>
        name.Contains('/', StringComparison.Ordinal)
        || name.Contains('\\', StringComparison.Ordinal);

    /// <summary>
    /// Resolution may raise confidence for AST-grounded providers (≥ 0.88), but must
    /// <b>clamp not rescale</b> capped bands: <c>hypa-pattern</c> ≤ 0.79 and
    /// <c>regex-fallback</c> &lt; 0.50 keep their in-band value (no elevation to the ceiling).
    /// Markdown structure facts share the pattern ceiling and are not raised into the AST band.
    /// </summary>
    private static double CapBindConfidence(string providerId, double current)
    {
        return providerId switch
        {
            // Clamp-only: preserve within-band ranking (0.62 vs 0.68 stay distinct).
            "hypa-pattern" => Math.Min(current, 0.79),
            "regex-fallback" => Math.Min(current, 0.49),
            // Markdown is not AST-grounded; never invent ≥0.80 from a successful bind.
            "markdown" => Math.Min(current, 0.79),
            _ => Math.Max(current, 0.88),
        };
    }

    private static CodeDependencyEdge StatusEdge(CodeDependencyEdge edge, string targetName, string status)
    {
        if (edge.TargetResolutionStatus == status && edge.TargetId == targetName)
            return edge;
        return edge with
        {
            TargetId = targetName,
            TargetResolutionStatus = status,
            Id = ReId(edge, targetName),
        };
    }

    /// <summary>
    /// Extractor unbound confidence for resolvable edge kinds, keyed by provenance
    /// provider. Must stay in sync with <c>TreeSitterQueryExtractor</c> /
    /// <c>CodePatternExtractor</c> so unbind→re-resolve converges with a full re-export
    /// of the same end state (D5 / §2).
    /// </summary>
    private static double UnboundEdgeConfidence(string providerId, string kind, double current) =>
        providerId switch
        {
            // TreeSitterQueryExtractor: document base 0.92; explicit unbound bands below.
            "tree-sitter-query" => kind switch
            {
                "inherits" or "implements" => 0.8,
                "overrides" or "calls" => 0.75,
                // imports use ForFact(provenance, "import") → document confidence 0.92.
                "imports" => 0.92,
                _ => current,
            },
            // CodePatternExtractor on a parse-validated file (hypa-pattern).
            "hypa-pattern" => kind switch
            {
                "inherits" or "implements" => 0.68,
                "calls" => 0.62,
                "overrides" => 0.64,
                // imports use ForFact(provenance, "import") → base 0.79, clamped to 0.79.
                "imports" => 0.79,
                _ => current,
            },
            // CodePatternExtractor under regex-fallback: ForFact clamps requested bands to ≤0.49.
            "regex-fallback" => kind switch
            {
                "inherits" or "implements" or "calls" or "overrides" => 0.49,
                "imports" => 0.45,
                _ => current,
            },
            // MarkdownStructureProvider / CodePatternExtractor.ExtractMarkdown doc-section edges.
            // Preserve the extract band (0.7 symbol / 0.72 file-link) so unbind→re-resolve
            // converges with a fresh extract of the same occurrence.
            "markdown" => kind switch
            {
                "documents" => current,
                _ => current,
            },
            // Unknown provider: do not invent a band — leave current (still re-resolves by name).
            _ => current,
        };

    /// <summary>
    /// Empty-pool status: overrides always unresolved; zero project hits → external-name
    /// (BCL/framework); project hits filtered out → unresolved.
    /// </summary>
    private static string EmptyStatus(string edgeKind, bool hadProjectCandidates)
    {
        if (edgeKind == "overrides")
            return "unresolved";
        return hadProjectCandidates ? "unresolved" : "external-name";
    }

    private static string ReId(CodeDependencyEdge edge, string targetId) =>
        edge.SourceSpan is { } span
            ? CodeStableId.ForEdge(edge.SourceId, targetId, edge.Kind, span.StartByte)
            : CodeStableId.ForEdge(edge.SourceId, targetId, edge.Kind);

    private static bool IsInterfaceContainer(IndexedSymbol candidate, ProjectSymbolTable table)
    {
        if (candidate.Symbol.ParentId is null)
            return false;
        return table.TryGet(candidate.Symbol.ParentId, out var parent)
               && parent.Symbol.Kind == "interface";
    }

    /// <summary>
    /// C# simple-name visibility from a source location.
    /// Prefer <paramref name="sourceContainingNamespace"/> (the declaring type/method's ns)
    /// so multi-namespace files do not treat sibling namespaces as mutually visible.
    /// Falls back to file-level namespace list only when source ns is unknown.
    /// Same-file alone is <b>not</b> sufficient — sibling ns in the same file need a using.
    /// </summary>
    private static bool IsInScope(
        IndexedSymbol candidate,
        FileResolutionContext ctx,
        string? sourceContainingNamespace = null)
    {
        var ns = candidate.ContainingNamespace;
        if (string.IsNullOrEmpty(ns))
            return true;

        if (!string.IsNullOrEmpty(sourceContainingNamespace))
        {
            // Same namespace as the source type/method.
            if (string.Equals(sourceContainingNamespace, ns, StringComparison.Ordinal))
                return true;
            // Source is nested under candidate ns (source A.B sees types in enclosing A).
            if (sourceContainingNamespace.StartsWith(ns + ".", StringComparison.Ordinal))
                return true;
        }
        else
        {
            // No source ns (e.g. non-symbol source): fall back to file namespace decls.
            foreach (var fileNs in ctx.Namespaces)
            {
                if (string.Equals(fileNs, ns, StringComparison.Ordinal))
                    return true;
                if (fileNs.StartsWith(ns + ".", StringComparison.Ordinal))
                    return true;
            }
        }

        foreach (var import in ctx.Imports)
        {
            if (!ImportAppliesToSource(import, sourceContainingNamespace))
                continue;

            // using Namespace; — exact match only (does not import nested namespaces).
            if (string.Equals(import.Target, ns, StringComparison.Ordinal))
                return true;
            // using static / type import style — exact type scope.
            if (string.Equals(import.Target, candidate.ScopePath, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// File-level usings apply everywhere. Namespace-scoped usings apply only when the
    /// source is that namespace or nested under it (not sibling namespaces in the same file).
    /// </summary>
    private static bool ImportAppliesToSource(ScopedImport import, string? sourceContainingNamespace)
    {
        // Compilation-unit / file-scoped usings are visible to all namespaces in the file.
        if (import.ContainingNamespace is null)
            return true;

        // Without a source ns, only file-level usings apply (avoid document-wide leak).
        if (sourceContainingNamespace is null)
            return false;

        if (string.Equals(sourceContainingNamespace, import.ContainingNamespace, StringComparison.Ordinal))
            return true;

        // Nested source under the using's namespace body.
        return sourceContainingNamespace.StartsWith(import.ContainingNamespace + ".", StringComparison.Ordinal);
    }

    /// <summary>
    /// Segment-aware qualified match. Exact segment equality (arity markers optional on either
    /// side), plus C# namespace-relative names: inside <c>namespace Corpus</c>,
    /// <c>Graph.WorkerContract</c> matches scope <c>Corpus.Graph.WorkerContract</c> by
    /// prefixing each enclosing namespace of the source. Deliberately rejects longer-prefix
    /// suffixes from unrelated namespaces (<c>MyCompany.Corpus.Graph.T</c> ≰ <c>Corpus.Graph.T</c>
    /// when the source is not under <c>MyCompany</c>) and near-miss segments
    /// (<c>XFooBar.Service</c> ≰ <c>FooBar.Service</c>).
    /// </summary>
    /// <remarks>
    /// When multiple candidates match different enclosing prefixes, callers must use
    /// <see cref="PreferInnermostQualifiedMatches"/> so C# closest-enclosing-namespace
    /// preference is preserved (e.g. <c>A.B.Graph.T</c> wins over <c>A.Graph.T</c>).
    /// </remarks>
    private static bool ScopeMatchesQualified(
        string scopePath,
        string qualifiedName,
        string? sourceContainingNamespace = null) =>
        QualifiedMatchRank(scopePath, qualifiedName, sourceContainingNamespace) >= 0;

    /// <summary>
    /// Keep only candidates that match <paramref name="qualifiedName"/>, preferring the
    /// innermost enclosing-namespace rank when several prefixes would match.
    /// </summary>
    private static IReadOnlyList<IndexedSymbol> PreferInnermostQualifiedMatches(
        IReadOnlyList<IndexedSymbol> candidates,
        string qualifiedName,
        string? sourceContainingNamespace)
    {
        if (candidates.Count == 0)
            return candidates;

        var ranked = new List<(IndexedSymbol Symbol, int Rank)>(candidates.Count);
        var bestRank = -1;
        foreach (var c in candidates)
        {
            var rank = QualifiedMatchRank(c.ScopePath, qualifiedName, sourceContainingNamespace);
            if (rank < 0)
                continue;
            ranked.Add((c, rank));
            if (rank > bestRank)
                bestRank = rank;
        }

        if (ranked.Count == 0)
            return [];

        // Higher rank = more enclosing segments used (innermost). Exact/global match ranks 0.
        return ranked.Where(x => x.Rank == bestRank).Select(x => x.Symbol).ToArray();
    }

    /// <summary>
    /// Rank of a scope-path match against a qualified target under C# namespace-relative rules.
    /// Returns -1 if no match; otherwise the number of source-namespace segments used as a
    /// prefix (0 = global/exact <paramref name="qualifiedName"/>, higher = closer/inner).
    /// </summary>
    private static int QualifiedMatchRank(
        string scopePath,
        string qualifiedName,
        string? sourceContainingNamespace)
    {
        // No source ns: only exact/global qualified form.
        if (string.IsNullOrEmpty(sourceContainingNamespace))
            return SegmentsEqual(scopePath, qualifiedName) ? 0 : -1;

        var segs = SplitSegments(sourceContainingNamespace);
        // Try innermost prefix first for documentation clarity; rank encodes specificity.
        for (var len = segs.Length; len >= 0; len--)
        {
            var candidate = len == 0
                ? qualifiedName
                : string.Join(".", segs, 0, len) + "." + qualifiedName;
            if (SegmentsEqual(scopePath, candidate))
                return len;
        }

        return -1;
    }

    private static bool SegmentsEqual(string scopePath, string qualifiedName)
    {
        if (string.Equals(scopePath, qualifiedName, StringComparison.Ordinal))
            return true;

        var scopeSegs = SplitSegments(scopePath);
        var qualSegs = SplitSegments(qualifiedName);
        if (scopeSegs.Length == 0 || scopeSegs.Length != qualSegs.Length)
            return false;

        for (var i = 0; i < scopeSegs.Length; i++)
        {
            if (string.Equals(scopeSegs[i], qualSegs[i], StringComparison.Ordinal))
                continue;
            // Allow Box`1 ↔ Box when one side dropped arity via type-arg normalization.
            if (string.Equals(StripArity(scopeSegs[i]), StripArity(qualSegs[i]), StringComparison.Ordinal))
                continue;
            return false;
        }

        return true;
    }

    private static string[] SplitSegments(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Normalize a type reference for lookup: strip <c>global::</c> and type-arg lists
    /// (<c>Foo&lt;T&gt;</c>), but preserve metadata arity markers when present as a separate
    /// concern via <see cref="StripArity"/> for hint matching. Lookup simple names still
    /// drop both <c>&lt;…&gt;</c> and trailing <c>`N</c> so <c>Box&lt;T&gt;</c> finds <c>Box`1</c>.
    /// </summary>
    private static string NormalizeTypeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];
        // Strip C# type-arg lists only (Foo<T>); keep `N for moniker-style inputs.
        var lt = trimmed.IndexOf('<');
        if (lt >= 0)
            trimmed = trimmed[..lt];
        return trimmed;
    }

    /// <summary>Leaf name without namespace dots; also strips trailing <c>`N</c> arity.</summary>
    private static string SimpleName(string name)
    {
        var lastDot = name.LastIndexOf('.');
        var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
        return StripArity(leaf);
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    private static bool IsTypescriptFamily(string? language) =>
        language is not null
        && (language.Equals("typescript", StringComparison.OrdinalIgnoreCase)
            || language.Equals("tsx", StringComparison.OrdinalIgnoreCase));

    /// <summary>Relative ESM/TS module specifier (<c>./x</c>, <c>../y</c>).</summary>
    private static bool IsRelativeModuleSpecifier(string specifier) =>
        specifier.StartsWith("./", StringComparison.Ordinal)
        || specifier.StartsWith("../", StringComparison.Ordinal);

    private sealed record IndexedSymbol(
        CodeSymbol Symbol,
        string FilePath,
        string ScopePath,
        string ContainingNamespace)
    {
        /// <summary>Simple name of the containing type (parent), if any.</summary>
        public string ContainingTypeName
        {
            get
            {
                // ScopePath is Namespace.Type.Member or Namespace.Type — for methods last
                // segment is the method; parent type is second-to-last when nested under type.
                var parts = ScopePath.Split('.');
                if (parts.Length < 2)
                    return string.Empty;
                // method/property scope ends with member name; type name is previous segment.
                if (CallableKinds.Contains(Symbol.Kind) || Symbol.Kind is "property" or "field" or "event")
                    return parts[^2];
                return parts[^1];
            }
        }
    }

    /// <summary>
    /// A using/import target, optionally scoped to the innermost enclosing namespace
    /// (null = file-level / compilation-unit using, visible everywhere in the file).
    /// </summary>
    private sealed record ScopedImport(string Target, string? ContainingNamespace);

    private sealed record FileResolutionContext(
        string RelativePath,
        string Language,
        IReadOnlyList<ScopedImport> Imports,
        IReadOnlyList<string> Namespaces);

    private sealed class NamedScopeComparer : IEqualityComparer<(string Kind, string ScopePath)>
    {
        public static readonly NamedScopeComparer Instance = new();
        public bool Equals((string Kind, string ScopePath) x, (string Kind, string ScopePath) y) =>
            string.Equals(x.Kind, y.Kind, StringComparison.Ordinal)
            && string.Equals(x.ScopePath, y.ScopePath, StringComparison.Ordinal);
        public int GetHashCode((string Kind, string ScopePath) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Kind), StringComparer.Ordinal.GetHashCode(obj.ScopePath));
    }

    private enum BaseHintKind
    {
        SymbolId,
        QualifiedName,
        /// <summary>Qualified name that was written with <c>global::</c> — exact FQN only.</summary>
        GlobalQualifiedName,
        UnqualifiedName,
    }

    private readonly record struct BaseTypeHint(BaseHintKind Kind, string Value);

    private sealed class ProjectSymbolTable
    {
        private readonly Dictionary<string, List<IndexedSymbol>> _bySimpleName;
        private readonly Dictionary<string, IndexedSymbol> _byId;
        private readonly Dictionary<string, FileResolutionContext> _fileContexts;
        private readonly Dictionary<string, List<BaseTypeHint>> _baseTypeHints;
        private readonly Dictionary<string, string[]> _partialGroupIds;
        /// <summary>Normalized relative file path keys → synthetic TS file-module symbol.</summary>
        private readonly Dictionary<string, IndexedSymbol> _moduleByPath;
        /// <summary>Workspace-relative paths present in this resolve pass (for local-file binds).</summary>
        private readonly HashSet<string> _indexedFiles;

        private ProjectSymbolTable(
            Dictionary<string, List<IndexedSymbol>> bySimpleName,
            Dictionary<string, IndexedSymbol> byId,
            Dictionary<string, FileResolutionContext> fileContexts,
            Dictionary<string, List<BaseTypeHint>> baseTypeHints,
            Dictionary<string, string[]> partialGroupIds,
            Dictionary<string, IndexedSymbol> moduleByPath,
            HashSet<string> indexedFiles)
        {
            _bySimpleName = bySimpleName;
            _byId = byId;
            _fileContexts = fileContexts;
            _baseTypeHints = baseTypeHints;
            _partialGroupIds = partialGroupIds;
            _moduleByPath = moduleByPath;
            _indexedFiles = indexedFiles;
        }

        public static ProjectSymbolTable Build(IReadOnlyList<CodeStructureDocument> documents)
        {
            var bySimpleName = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
            var byId = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
            var fileContexts = new Dictionary<string, FileResolutionContext>(StringComparer.Ordinal);
            var baseTypeHints = new Dictionary<string, List<BaseTypeHint>>(StringComparer.Ordinal);
            var moduleByPath = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
            var indexedFiles = new HashSet<string>(StringComparer.Ordinal);

            // Pre-count type-parameter children per parent so scope paths can encode arity.
            var typeParamArity = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var doc in documents)
            {
                foreach (var symbol in doc.Symbols)
                {
                    if (symbol.Kind != "type-parameter" || symbol.ParentId is null)
                        continue;
                    typeParamArity[symbol.ParentId] = typeParamArity.GetValueOrDefault(symbol.ParentId) + 1;
                }
            }

            foreach (var doc in documents)
            {
                indexedFiles.Add(doc.File.RelativePath.Replace('\\', '/'));
                var byIdLocal = doc.Symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);

                foreach (var symbol in doc.Symbols)
                {
                    var scopePath = BuildScopePath(symbol, byIdLocal, typeParamArity);
                    var containingNs = ContainingNamespace(symbol, byIdLocal, typeParamArity);
                    var entry = new IndexedSymbol(symbol, doc.File.RelativePath, scopePath, containingNs);
                    byId[symbol.Id] = entry;

                    // Index by bare name and arity-stripped leaf so Box`1 is found as "Box".
                    IndexByName(bySimpleName, StripArity(symbol.Name), entry);
                    if (!string.Equals(symbol.Name, StripArity(symbol.Name), StringComparison.Ordinal))
                        IndexByName(bySimpleName, symbol.Name, entry);

                    // Namespace: index full scope path + leaf so block Nested under Outer
                    // is found as "Outer.Nested" and leaf "Nested".
                    if (symbol.Kind == "namespace")
                    {
                        IndexByName(bySimpleName, scopePath, entry);
                        var leaf = SimpleName(scopePath);
                        if (!string.Equals(leaf, scopePath, StringComparison.Ordinal))
                            IndexByName(bySimpleName, leaf, entry);
                    }
                }

                // Record inherits/implements targets for each source type (override base chain).
                // Qualified names stay FQN-only; simple leaf is recorded only for unqualified bases.
                foreach (var edge in doc.DependencyEdges)
                {
                    if (!RelationshipEdgeKinds.Contains(edge.Kind))
                        continue;
                    if (!baseTypeHints.TryGetValue(edge.SourceId, out var hints))
                    {
                        hints = [];
                        baseTypeHints[edge.SourceId] = hints;
                    }

                    if (edge.TargetId.StartsWith("sym_", StringComparison.Ordinal))
                        AddHint(hints, BaseHintKind.SymbolId, edge.TargetId);

                    if (!string.IsNullOrWhiteSpace(edge.TargetName))
                    {
                        var raw = edge.TargetName.Trim();
                        var n = NormalizeTypeName(raw);
                        if (n.Contains('.', StringComparison.Ordinal))
                        {
                            // Preserve global:: so override base matching does not namespace-relativize.
                            var kind = raw.StartsWith("global::", StringComparison.Ordinal)
                                ? BaseHintKind.GlobalQualifiedName
                                : BaseHintKind.QualifiedName;
                            AddHint(hints, kind, n);
                        }
                        else
                            AddHint(hints, BaseHintKind.UnqualifiedName, StripArity(SimpleName(n)));
                    }
                }

                // Namespace spans for mapping using startByte → innermost enclosing ns.
                var nsSpans = doc.Symbols
                    .Where(s => s.Kind == "namespace")
                    .Select(s => (
                        Scope: BuildScopePath(s, byIdLocal, typeParamArity),
                        Span: s.Span))
                    .ToArray();

                var imports = new List<ScopedImport>();
                foreach (var r in doc.References.Where(r => r.Kind == "import"))
                {
                    var target = NormalizeTypeName(r.Target);
                    if (target.Length == 0)
                        continue;
                    var enclosing = FindInnermostNamespaceScope(r.Span.StartByte, nsSpans);
                    AddScopedImport(imports, target, enclosing);
                }

                foreach (var e in doc.DependencyEdges.Where(e => e.Kind == "imports"))
                {
                    var target = NormalizeTypeName(e.TargetName ?? e.TargetId);
                    if (target.Length == 0)
                        continue;
                    var start = e.SourceSpan?.StartByte ?? 0;
                    var enclosing = FindInnermostNamespaceScope(start, nsSpans);
                    AddScopedImport(imports, target, enclosing);
                }

                // Full nested scope paths (Outer.Nested), never raw inner Name alone.
                var namespaces = nsSpans
                    .Select(n => n.Scope)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                fileContexts[doc.File.RelativePath] = new FileResolutionContext(
                    doc.File.RelativePath,
                    doc.File.Language ?? string.Empty,
                    imports,
                    namespaces);

                // Minimal TS/TSX file-module moniker so relative imports can join to a local-symbol.
                if (IsTypescriptFamily(doc.File.Language))
                    RegisterFileModule(doc, byId, moduleByPath);
            }

            // Map partial-type / multi-file type monikers that share (Kind, ScopePath) so override
            // parent matching can see methods declared on non-canonical partial files.
            var partialGroupIds = BuildPartialGroupIds(byId);

            return new ProjectSymbolTable(
                bySimpleName, byId, fileContexts, baseTypeHints, partialGroupIds, moduleByPath, indexedFiles);
        }

        public bool HasIndexedFile(string relativePath) =>
            _indexedFiles.Contains(relativePath.Replace('\\', '/'));

        /// <summary>
        /// Doc simple-name lookup: eligible kinds only. Non-callables collapse logical
        /// duplicates (partials); callables keep distinct monikers so overloads stay
        /// ambiguous. Constructors are excluded unless
        /// <paramref name="includeConstructors"/> is set (signature-bearing simple forms).
        /// </summary>
        public IReadOnlyList<IndexedSymbol> LookupDocTargetsBySimpleName(
            string simpleName,
            bool includeConstructors = false)
        {
            if (!_bySimpleName.TryGetValue(simpleName, out var list))
                return [];
            var eligible = list
                .Where(s => DocReferenceTargetKinds.Contains(s.Symbol.Kind)
                            && (includeConstructors || s.Symbol.Kind != "constructor"))
                .ToArray();
            return CollapseDocLogicalDuplicates(eligible);
        }

        /// <summary>
        /// Doc qualified-name lookup: scope-path identity only (exact or ends-with segment
        /// match when unique). Never falls back to leaf-name alone. Callables are not
        /// collapsed — overloads remain multi-hit until a signature form pins one.
        /// </summary>
        /// <remarks>
        /// Prefilters through the leaf/simple-name index so cost is O(symbols with that
        /// leaf) rather than O(total symbols) per documents edge. Exact/suffix matching,
        /// overload ambiguity, and partial-type collapse are unchanged.
        /// </remarks>
        public IReadOnlyList<IndexedSymbol> LookupDocTargetsByQualifiedName(string qualifiedName)
        {
            // Leaf of the qualified form (arity-stripped) — same key used when indexing.
            var leaf = SimpleName(qualifiedName);
            if (!_bySimpleName.TryGetValue(leaf, out var leafList) || leafList.Count == 0)
                return [];

            var results = new List<IndexedSymbol>();
            foreach (var entry in leafList)
            {
                if (!DocReferenceTargetKinds.Contains(entry.Symbol.Kind))
                    continue;
                if (SegmentsEqual(entry.ScopePath, qualifiedName)
                    || string.Equals(entry.ScopePath, qualifiedName, StringComparison.Ordinal))
                {
                    results.Add(entry);
                }
            }

            if (results.Count > 0)
                return CollapseDocLogicalDuplicates(results);

            // Namespace-relative style: unique innermost suffix match (A.B.C matches scope X.A.B.C).
            var suffixHits = new List<IndexedSymbol>();
            var suffix = "." + qualifiedName;
            foreach (var entry in leafList)
            {
                if (!DocReferenceTargetKinds.Contains(entry.Symbol.Kind))
                    continue;
                if (entry.ScopePath.EndsWith(suffix, StringComparison.Ordinal)
                    || string.Equals(entry.ScopePath, qualifiedName, StringComparison.Ordinal))
                {
                    suffixHits.Add(entry);
                }
            }

            return CollapseDocLogicalDuplicates(suffixHits);
        }

        /// <summary>
        /// Collapse multi-file partials / multi-file namespace decls for non-callables only.
        /// Callables (method/function/constructor) keep every moniker so overloads stay
        /// ambiguous for documents resolution.
        /// </summary>
        private static IReadOnlyList<IndexedSymbol> CollapseDocLogicalDuplicates(
            IReadOnlyList<IndexedSymbol> candidates)
        {
            if (candidates.Count <= 1)
                return candidates;

            var nonCallables = candidates.Where(c => !CallableKinds.Contains(c.Symbol.Kind)).ToArray();
            var callables = candidates.Where(c => CallableKinds.Contains(c.Symbol.Kind)).ToArray();
            if (nonCallables.Length == 0)
                return callables;
            if (callables.Length == 0)
                return CollapseLogicalDuplicates(nonCallables);

            var collapsed = CollapseLogicalDuplicates(nonCallables);
            var merged = new IndexedSymbol[collapsed.Count + callables.Length];
            for (var i = 0; i < collapsed.Count; i++)
                merged[i] = collapsed[i];
            for (var i = 0; i < callables.Length; i++)
                merged[collapsed.Count + i] = callables[i];
            return merged;
        }

        /// <summary>
        /// Innermost namespace whose span contains <paramref name="bytePos"/>; null if none
        /// (file-level / compilation-unit using).
        /// </summary>
        private static string? FindInnermostNamespaceScope(
            int bytePos,
            IReadOnlyList<(string Scope, SourceSpan Span)> nsSpans)
        {
            string? best = null;
            var bestWidth = int.MaxValue;
            var bestStart = -1;
            foreach (var (scope, span) in nsSpans)
            {
                if (bytePos < span.StartByte || bytePos >= span.EndByte)
                    continue;
                var width = span.EndByte - span.StartByte;
                if (width < bestWidth || (width == bestWidth && span.StartByte > bestStart))
                {
                    best = scope;
                    bestWidth = width;
                    bestStart = span.StartByte;
                }
            }

            return best;
        }

        private static void AddScopedImport(List<ScopedImport> imports, string target, string? enclosingNs)
        {
            if (imports.Any(i =>
                    string.Equals(i.Target, target, StringComparison.Ordinal)
                    && string.Equals(i.ContainingNamespace, enclosingNs, StringComparison.Ordinal)))
            {
                return;
            }

            imports.Add(new ScopedImport(target, enclosingNs));
        }

        private static void IndexByName(
            Dictionary<string, List<IndexedSymbol>> bySimpleName,
            string key,
            IndexedSymbol entry)
        {
            if (key.Length == 0)
                return;
            if (!bySimpleName.TryGetValue(key, out var list))
            {
                list = [];
                bySimpleName[key] = list;
            }

            if (list.All(e => e.Symbol.Id != entry.Symbol.Id))
                list.Add(entry);
        }

        public FileResolutionContext ContextFor(string relativePath) =>
            _fileContexts.TryGetValue(relativePath, out var ctx)
                ? ctx
                : new FileResolutionContext(relativePath, string.Empty, Array.Empty<ScopedImport>(), []);

        public string? NamespaceOfSymbol(string symbolId) =>
            _byId.TryGetValue(symbolId, out var entry) ? entry.ContainingNamespace : null;

        public string? ParentIdOf(string symbolId) =>
            _byId.TryGetValue(symbolId, out var entry) ? entry.Symbol.ParentId : null;

        public IReadOnlyList<BaseTypeHint> BaseTypeHints(string typeSymbolId) =>
            _baseTypeHints.TryGetValue(typeSymbolId, out var hints) ? hints : [];

        public bool TryGet(string symbolId, out IndexedSymbol entry) =>
            _byId.TryGetValue(symbolId, out entry!);

        /// <summary>
        /// All moniker Ids that share (Kind, ScopePath) with <paramref name="symbolId"/>
        /// (multi-file partial types). Single-element list when the type is not partial.
        /// </summary>
        public IReadOnlyList<string> PartialGroupIds(string symbolId) =>
            _partialGroupIds.TryGetValue(symbolId, out var group) ? group : [symbolId];

        private static Dictionary<string, string[]> BuildPartialGroupIds(
            Dictionary<string, IndexedSymbol> byId)
        {
            var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var group in byId.Values
                         .Where(e => TypeKinds.Contains(e.Symbol.Kind))
                         .GroupBy(e => (e.Symbol.Kind, e.ScopePath), NamedScopeComparer.Instance))
            {
                var ids = group.Select(e => e.Symbol.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                if (ids.Length <= 1)
                    continue;
                foreach (var id in ids)
                    map[id] = ids;
            }

            return map;
        }

        private static void AddHint(List<BaseTypeHint> hints, BaseHintKind kind, string value)
        {
            if (value.Length == 0)
                return;
            if (hints.Any(h => h.Kind == kind && string.Equals(h.Value, value, StringComparison.Ordinal)))
                return;
            hints.Add(new BaseTypeHint(kind, value));
        }

        /// <summary>
        /// Resolve a relative TS module specifier against the importer path and return the
        /// synthetic file-module symbol when the target file is in the project index.
        /// </summary>
        public IndexedSymbol? LookupRelativeModule(string fromRelativePath, string moduleSpecifier)
        {
            var resolved = ResolveRelativeModulePath(fromRelativePath, moduleSpecifier);
            if (resolved is null)
                return null;

            foreach (var key in ModulePathLookupKeys(resolved))
            {
                if (_moduleByPath.TryGetValue(key, out var entry))
                    return entry;
            }

            return null;
        }

        private static void RegisterFileModule(
            CodeStructureDocument doc,
            Dictionary<string, IndexedSymbol> byId,
            Dictionary<string, IndexedSymbol> moduleByPath)
        {
            var relative = NormalizePathSeparators(doc.File.RelativePath);
            if (relative.Length == 0)
                return;

            var moduleName = Path.GetFileNameWithoutExtension(relative);
            if (string.IsNullOrEmpty(moduleName))
                moduleName = relative;

            var moniker = CodeSymbolMoniker.Build(relative, "module", moduleName);
            var symbol = new CodeSymbol
            {
                Id = CodeStableId.ForSymbol(moniker),
                FilePath = relative,
                Language = doc.File.Language,
                Name = moduleName,
                Kind = "module",
                Span = new SourceSpan { StartByte = 0, EndByte = 0 },
                Provenance = doc.Provenance with { FactKind = "module-file", Confidence = 0.9 },
            };
            var entry = new IndexedSymbol(symbol, relative, moduleName, string.Empty);
            byId[symbol.Id] = entry;

            foreach (var key in ModulePathLookupKeys(relative))
            {
                if (!moduleByPath.TryGetValue(key, out var existing))
                {
                    moduleByPath[key] = entry;
                    continue;
                }

                // Deterministic collision rule for shared keys such as `src/foo` claimed by both
                // `src/foo.ts` and `src/foo/index.ts`: prefer the exact file over directory-index.
                // Same rank keeps first registration (stable, order-independent only across ranks).
                if (ModuleKeyRank(relative, key) < ModuleKeyRank(existing.FilePath, key))
                    moduleByPath[key] = entry;
            }
        }

        /// <summary>
        /// Keys used for module path join: full path, extensionless path, and directory index forms.
        /// When both <c>foo.ts</c> and <c>foo/index.ts</c> register the shared key <c>foo</c>,
        /// registration prefers the exact file (see <see cref="ModuleKeyRank"/>).
        /// </summary>
        private static IEnumerable<string> ModulePathLookupKeys(string relativePath)
        {
            var path = NormalizePathSeparators(relativePath);
            if (path.Length == 0)
                yield break;

            yield return path;

            var noExt = StripKnownScriptExtension(path);
            if (!string.Equals(noExt, path, StringComparison.Ordinal))
                yield return noExt;

            // Directory index: src/foo/index.ts also answers specifier ./foo
            if (noExt.EndsWith("/index", StringComparison.Ordinal))
            {
                var dir = noExt[..^"/index".Length];
                if (dir.Length > 0)
                    yield return dir;
            }
        }

        /// <summary>
        /// Priority for a file claiming a module lookup key. Lower wins.
        /// 0 = exact file (<c>src/foo.ts</c> → key <c>src/foo</c>),
        /// 1 = directory index (<c>src/foo/index.ts</c> → key <c>src/foo</c>),
        /// 2 = other (e.g. full-path key only).
        /// </summary>
        private static int ModuleKeyRank(string relativePath, string key)
        {
            var noExt = StripKnownScriptExtension(NormalizePathSeparators(relativePath));
            if (string.Equals(noExt, key, StringComparison.Ordinal))
                return 0;
            if (string.Equals(noExt, key + "/index", StringComparison.Ordinal))
                return 1;
            return 2;
        }

        private static string? ResolveRelativeModulePath(string fromRelativePath, string specifier)
        {
            var from = NormalizePathSeparators(fromRelativePath);
            var spec = NormalizePathSeparators(specifier);
            if (from.Length == 0 || spec.Length == 0)
                return null;

            var dir = PathDirectory(from);
            var combined = string.IsNullOrEmpty(dir) ? spec : $"{dir}/{spec}";
            return NormalizeDotSegments(combined);
        }

        private static string NormalizePathSeparators(string path) =>
            path.Replace('\\', '/').Trim();

        private static string PathDirectory(string relativePath)
        {
            var path = NormalizePathSeparators(relativePath);
            var slash = path.LastIndexOf('/');
            return slash < 0 ? string.Empty : path[..slash];
        }

        private static string StripKnownScriptExtension(string path)
        {
            foreach (var ext in new[] { ".tsx", ".ts", ".jsx", ".js", ".mts", ".cts", ".mjs", ".cjs" })
            {
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return path[..^ext.Length];
            }

            return path;
        }

        private static string? NormalizeDotSegments(string path)
        {
            var parts = new List<string>();
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is ".")
                    continue;
                if (segment is "..")
                {
                    if (parts.Count == 0)
                        return null;
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }

                parts.Add(segment);
            }

            return string.Join('/', parts);
        }

        public IReadOnlyList<IndexedSymbol> LookupNamespaces(string qualifiedOrSimple, string simpleName)
        {
            var results = new List<IndexedSymbol>();
            if (_bySimpleName.TryGetValue(qualifiedOrSimple, out var exact))
                results.AddRange(exact.Where(s => s.Symbol.Kind == "namespace"));
            if (!string.Equals(qualifiedOrSimple, simpleName, StringComparison.Ordinal)
                && _bySimpleName.TryGetValue(simpleName, out var leaf))
            {
                foreach (var s in leaf.Where(s => s.Symbol.Kind == "namespace"))
                {
                    if (results.All(r => r.Symbol.Id != s.Symbol.Id))
                        results.Add(s);
                }
            }

            // Prefer exact full scope-path matches when the import is dotted.
            if (qualifiedOrSimple.Contains('.', StringComparison.Ordinal))
            {
                var exactName = results
                    .Where(s => string.Equals(s.ScopePath, qualifiedOrSimple, StringComparison.Ordinal)
                                || string.Equals(s.Symbol.Name, qualifiedOrSimple, StringComparison.Ordinal))
                    .ToArray();
                if (exactName.Length > 0)
                    results = exactName.ToList();
            }

            return CollapseLogicalDuplicates(results);
        }

        public IReadOnlyList<IndexedSymbol> LookupTypes(string simpleName)
        {
            if (!_bySimpleName.TryGetValue(simpleName, out var list))
                return [];
            return CollapseLogicalDuplicates(list.Where(s => TypeKinds.Contains(s.Symbol.Kind)).ToArray());
        }

        public IReadOnlyList<IndexedSymbol> LookupCallables(string simpleName)
        {
            if (!_bySimpleName.TryGetValue(simpleName, out var list))
                return [];
            return list.Where(s => CallableKinds.Contains(s.Symbol.Kind)).ToArray();
        }

        /// <summary>
        /// Build scope path with metadata-style arity on types (<c>Box`1</c>) so
        /// distinct generic arities do not share a collapse key.
        /// Nested block namespaces walk parents → <c>Outer.Nested</c>.
        /// </summary>
        private static string BuildScopePath(
            CodeSymbol symbol,
            IReadOnlyDictionary<string, CodeSymbol> byId,
            IReadOnlyDictionary<string, int> typeParamArity)
        {
            var parts = new List<string>();
            var current = symbol;
            for (var guard = 0; guard < 64; guard++)
            {
                parts.Add(FormatSegment(current, typeParamArity));
                if (current.ParentId is null || !byId.TryGetValue(current.ParentId, out var parent))
                    break;
                current = parent;
            }

            parts.Reverse();
            return string.Join(".", parts);
        }

        private static string FormatSegment(CodeSymbol symbol, IReadOnlyDictionary<string, int> typeParamArity)
        {
            if (TypeKinds.Contains(symbol.Kind)
                && !symbol.Name.Contains('`', StringComparison.Ordinal))
            {
                var arity = typeParamArity.GetValueOrDefault(symbol.Id);
                if (arity > 0)
                    return $"{symbol.Name}`{arity}";
            }

            return symbol.Name;
        }

        /// <summary>
        /// Full namespace scope path of the nearest namespace ancestor (e.g. <c>Outer.Nested</c>),
        /// never the raw inner Name alone.
        /// </summary>
        private static string ContainingNamespace(
            CodeSymbol symbol,
            IReadOnlyDictionary<string, CodeSymbol> byId,
            IReadOnlyDictionary<string, int> typeParamArity)
        {
            var current = symbol;
            for (var guard = 0; guard < 64; guard++)
            {
                if (current.Kind == "namespace")
                    return BuildScopePath(current, byId, typeParamArity);
                if (current.ParentId is null || !byId.TryGetValue(current.ParentId, out var parent))
                    break;
                current = parent;
            }

            return string.Empty;
        }
    }
}
