using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class CheckpointSecretFilePolicyTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData(".ENV")]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    [InlineData("id_rsa")]
    [InlineData("id_dsa")]
    [InlineData("id_ecdsa")]
    [InlineData("id_ed25519")]
    [InlineData("ID_RSA")]
    [InlineData("keys/server.pem")]
    [InlineData("tls/cert.PEM")]
    [InlineData("tls/client.key")]
    [InlineData(".ssh/config")]
    [InlineData(".ssh/id_rsa")]
    [InlineData(".aws/credentials")]
    [InlineData(".gnupg/secring.gpg")]
    [InlineData(".netrc")]
    [InlineData(".git-credentials")]
    [InlineData("nested/.netrc")]
    public void CheckpointSecretFilePolicy_denies_env_id_rsa_pem_key_and_well_known_paths(string relativePath)
    {
        Assert.True(CheckpointSecretFilePolicy.IsDenied(relativePath));
    }

    [Theory]
    [InlineData("hello.txt")]
    [InlineData("src/Program.cs")]
    [InlineData("readme.env.example")]
    [InlineData("id_rsa.pub")]
    [InlineData("notes.pem.bak")]
    public void CheckpointSecretFilePolicy_allows_non_secret_workspace_paths(string relativePath)
    {
        Assert.False(CheckpointSecretFilePolicy.IsDenied(relativePath));
    }

    [Fact]
    public void CheckpointSecretFilePolicy_warning_is_path_only()
    {
        var warning = CheckpointSecretFilePolicy.WarningFor(".env");
        Assert.Equal("workspace: omit secret file .env", warning);
        Assert.DoesNotContain("=", warning);
        Assert.Equal(
            "workspace: omit replaced file safe.txt",
            CheckpointSecretFilePolicy.WarningForReplaced("safe.txt"));
    }

    [Fact]
    public void CheckpointSecretFilePolicy_unsafe_root_is_filesystem_or_exact_home()
    {
        Assert.True(CheckpointSecretFilePolicy.IsExactUnsafeRoot("/", "/Users/demo"));
        Assert.True(CheckpointSecretFilePolicy.IsExactUnsafeRoot("/Users/demo", "/Users/demo"));
        Assert.False(CheckpointSecretFilePolicy.IsExactUnsafeRoot("/Users/demo/project", "/Users/demo"));
        Assert.False(CheckpointSecretFilePolicy.IsExactUnsafeRoot("/tmp/workspace", "/Users/demo"));
    }
}
