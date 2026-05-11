using System.Security.Cryptography;
using System.Text;

namespace Hypa.Runtime.Application.Services;

public static class CodeStableId
{
    public static string ForSymbol(string filePath, string kind, string name, int startByte) =>
        "sym_" + Hash($"{Normalize(filePath)}|{kind}|{name}|{startByte}");

    public static string ForReference(string filePath, string kind, string target, int startByte) =>
        "ref_" + Hash($"{Normalize(filePath)}|{kind}|{target}|{startByte}");

    public static string ForEdge(string sourceId, string targetId, string kind) =>
        "edge_" + Hash($"{sourceId}|{targetId}|{kind}");

    public static string ForEdge(string sourceId, string targetId, string kind, int startByte) =>
        "edge_" + Hash($"{sourceId}|{targetId}|{kind}|{startByte}");

    public static string ForDiagnostic(string filePath, string code, int startByte) =>
        "diag_" + Hash($"{Normalize(filePath)}|{code}|{startByte}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    private static string Normalize(string value) => value.Replace('\\', '/');
}
