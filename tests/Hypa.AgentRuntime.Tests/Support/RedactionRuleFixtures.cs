namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// One known-positive input for each generated redaction rule.
/// Extra rows cover gh[ousr]_ and pwd, which the pre-filter must also catch.
/// </summary>
internal static class RedactionRuleFixtures
{
    internal readonly record struct RuleFixture(
        string RuleName,
        string Input,
        string Secret,
        bool ExpectReplacement);

    internal static readonly string[] RuleNames =
    [
        "AuthorizationHeader",
        "EnvAssignment",
        "JsonSecretField",
        "BearerToken",
        "SkToken",
        "GhToken",
        "AwsAccessKey",
        "AwsSecretPair",
        "PemBlock",
        "PasswordInline",
        "IncompletePem",
        "IncompleteSk",
        "IncompleteGh",
        "IncompleteAkia",
        "IncompleteBearer",
        "IncompleteSecretAtEnd",
        "IncompleteAssignmentAtEnd",
        "IncompleteKeyPrefixAtEnd",
    ];

    internal static IReadOnlyList<RuleFixture> All { get; } =
    [
        new("AuthorizationHeader", "Authorization: Bearer a.b.c\n", "a.b.c", true),
        new("BearerToken", "bearer abcdefghijklmnopqrst\n", "abcdefghijklmnopqrst", true),
        new("EnvAssignment", "API_KEY=hunter2\n", "hunter2", true),
        new("JsonSecretField", """{"api_key":"hunter2"}""" + "\n", "hunter2", true),
        new("SkToken", "sk-abcdefghijklmnopqrstuvwxyz0123\n", "sk-abcdefghijklmnopqrstuvwxyz0123", true),
        new("GhToken", "ghp_abcdefghijklmnopqrst\n", "ghp_abcdefghijklmnopqrst", true),
        new("GhToken", "gho_abcdefghijklmnopqrst\n", "gho_abcdefghijklmnopqrst", true),
        new("GhToken", "ghu_abcdefghijklmnopqrst\n", "ghu_abcdefghijklmnopqrst", true),
        new("GhToken", "ghs_abcdefghijklmnopqrst\n", "ghs_abcdefghijklmnopqrst", true),
        new("GhToken", "ghr_abcdefghijklmnopqrst\n", "ghr_abcdefghijklmnopqrst", true),
        new("AwsAccessKey", "AKIAAAAAAAAAAAAAAAAA\n", "AKIAAAAAAAAAAAAAAAAA", true),
        new(
            "AwsSecretPair",
            "aws_secret_access_key=wJalrXUtnFEMI/K7MDENG/bPxRfiCY\n",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCY",
            true),
        new(
            "PemBlock",
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF6PZGFw=\n-----END RSA PRIVATE KEY-----\n",
            "MIIEowIBAAKCAQEA0Z3VS5JJcds3xfn",
            true),
        new("PasswordInline", "password=hunter2\n", "hunter2", true),
        new("PasswordInline", "passwd=x\n", "x", true),
        new("PasswordInline", "pwd=x\n", "x", true),
        new("PasswordInline", "secret=x\n", "x", true),
        new("PasswordInline", "token=x\n", "x", true),
        new("IncompletePem", "-----BEGIN RSA PRIVATE KEY-----\nMII", "MII", true),
        new("IncompleteSk", "sk-abc", "sk-abc", true),
        new("IncompleteGh", "ghp_abc", "ghp_abc", true),
        new("IncompleteAkia", "AKIAABC", "AKIAABC", true),
        new("IncompleteBearer", "Bearer abc", "abc", true),
        new("IncompleteSecretAtEnd", "log line sk-abc", "sk-abc", true),
        new("IncompleteAssignmentAtEnd", "TOKEN=abc", "abc", true),
        new("IncompleteKeyPrefixAtEnd", "export MY_TOKE", "", false),
    ];
}
