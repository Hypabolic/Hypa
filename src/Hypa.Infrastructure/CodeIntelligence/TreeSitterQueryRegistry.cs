namespace Hypa.Infrastructure.CodeIntelligence;

internal static class TreeSitterQueryRegistry
{
    public const string QueryVersion = "tree-sitter-syntactic-graph-1";

    public static readonly IReadOnlyDictionary<string, TreeSitterGrammar> Grammars = new Dictionary<string, TreeSitterGrammar>(StringComparer.OrdinalIgnoreCase)
    {
        ["c-sharp"] = new("tree-sitter-c-sharp", "tree_sitter_c_sharp"),
        ["typescript"] = new("tree-sitter-typescript", "tree_sitter_typescript"),
        ["tsx"] = new("tree-sitter-tsx", "tree_sitter_tsx"),
        ["javascript"] = new("tree-sitter-javascript", "tree_sitter_javascript"),
        ["jsx"] = new("tree-sitter-javascript", "tree_sitter_javascript"),
        ["python"] = new("tree-sitter-python", "tree_sitter_python"),
        ["go"] = new("tree-sitter-go", "tree_sitter_go"),
        ["rust"] = new("tree-sitter-rust", "tree_sitter_rust"),
        ["java"] = new("tree-sitter-java", "tree_sitter_java"),
        ["c"] = new("tree-sitter-c", "tree_sitter_c"),
        ["cpp"] = new("tree-sitter-cpp", "tree_sitter_cpp"),
        ["bash"] = new("tree-sitter-bash", "tree_sitter_bash"),
        ["json"] = new("tree-sitter-json", "tree_sitter_json"),
        ["yaml"] = new("tree-sitter-yaml", "tree_sitter_yaml"),
        ["toml"] = new("tree-sitter-toml", "tree_sitter_toml"),
        ["markdown"] = new("tree-sitter-markdown", "tree_sitter_markdown"),
    };

    public static readonly IReadOnlyDictionary<string, SyntacticQueryPack> QueryPacks = new Dictionary<string, SyntacticQueryPack>(StringComparer.OrdinalIgnoreCase)
    {
        ["c-sharp"] = SyntacticQueryPack.Full,
        ["typescript"] = SyntacticQueryPack.Full,
        ["tsx"] = SyntacticQueryPack.Full,
        ["javascript"] = SyntacticQueryPack.CallsAndReferences,
        ["jsx"] = SyntacticQueryPack.CallsAndReferences,
        ["python"] = SyntacticQueryPack.Full,
        ["go"] = SyntacticQueryPack.Full,
        ["rust"] = SyntacticQueryPack.Full,
        ["java"] = SyntacticQueryPack.Full,
        ["c"] = SyntacticQueryPack.CallsAndReferences,
        ["cpp"] = SyntacticQueryPack.Full,
        ["bash"] = SyntacticQueryPack.CallsAndReferences,
        ["json"] = SyntacticQueryPack.Config,
        ["yaml"] = SyntacticQueryPack.Config,
        ["toml"] = SyntacticQueryPack.Config,
        ["markdown"] = SyntacticQueryPack.Markdown,
    };
}

internal sealed record TreeSitterGrammar(string Library, string Function);

internal sealed record SyntacticQueryPack(bool Symbols, bool Imports, bool Calls, bool References, bool Inheritance, bool Implements, bool Overrides)
{
    public static SyntacticQueryPack Full { get; } = new(true, true, true, true, true, true, true);
    public static SyntacticQueryPack CallsAndReferences { get; } = new(true, true, true, true, false, false, false);
    public static SyntacticQueryPack Config { get; } = new(true, true, false, false, false, false, false);
    public static SyntacticQueryPack Markdown { get; } = new(true, true, false, true, false, false, false);
}
