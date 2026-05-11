using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Reducers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Reducers;

public sealed class KubectlOutputCompressorTests
{
    private static KubectlOutputCompressor Make() => new(new CharDivTokenCounter());

    private static CommandInvocation KubectlInvocation(params string[] args) =>
        CommandInvocation.Buffered("kubectl", args, $"kubectl {string.Join(' ', args)}");

    private static CommandOutput Out(string stdout, int exitCode = 0) =>
        CommandOutput.Captured(stdout, string.Empty, exitCode, TimeSpan.Zero);

    [Fact]
    public void CanHandle_KubectlGet_ReturnsTrue() =>
        Assert.True(Make().CanHandle(KubectlInvocation("get", "pods")));

    [Fact]
    public void CanHandle_KubectlDescribe_ReturnsTrue() =>
        Assert.True(Make().CanHandle(KubectlInvocation("describe", "pod", "my-pod")));

    [Fact]
    public void CanHandle_KubectlApply_ReturnsFalse() =>
        Assert.False(Make().CanHandle(KubectlInvocation("apply", "-f", "manifest.yaml")));

    [Fact]
    public void CanHandle_Git_ReturnsFalse() =>
        Assert.False(Make().CanHandle(CommandInvocation.Buffered("git", ["status"], "git status")));

    [Fact]
    public void Compress_Get_ReducerId_IsKubectlGet()
    {
        var result = Make().Compress(KubectlInvocation("get", "pods"), Out("NAME   STATUS\npod-1  Running\n"), CompressionOptions.Default);
        Assert.Equal("kubectl-get", result.ReducerId);
    }

    [Fact]
    public void Compress_Get_PreservesTabularOutput()
    {
        var input = "NAME     READY   STATUS    RESTARTS\npod-1    1/1     Running   0\npod-2    1/1     Running   0\n";
        var result = Make().Compress(KubectlInvocation("get", "pods"), Out(input), CompressionOptions.Default);
        Assert.Contains("pod-1", result.Text);
        Assert.Contains("pod-2", result.Text);
    }

    [Fact]
    public void Compress_Get_LargeOutput_TruncatesButPreservesHeader()
    {
        var header = "NAME     READY   STATUS    RESTARTS";
        var rows = Enumerable.Range(1, 400).Select(i => $"pod-{i}    1/1     Running   0");
        var input = header + "\n" + string.Join('\n', rows);
        var result = Make().Compress(KubectlInvocation("get", "pods"), Out(input), CompressionOptions.Default);
        Assert.StartsWith("NAME     READY", result.Text);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Compress_Describe_ReducerId_IsKubectlDescribe()
    {
        var input = "Name:         my-pod\nNamespace:    default\nStatus:       Running\n";
        var result = Make().Compress(KubectlInvocation("describe", "pod", "my-pod"), Out(input), CompressionOptions.Default);
        Assert.Equal("kubectl-describe", result.ReducerId);
    }

    [Fact]
    public void Compress_Describe_PreservesNameNamespaceStatus()
    {
        var input = "Name:         my-pod\nNamespace:    default\nStatus:       CrashLoopBackOff\n";
        var result = Make().Compress(KubectlInvocation("describe", "pod", "my-pod"), Out(input), CompressionOptions.Default);
        Assert.Contains("Name:", result.Text);
        Assert.Contains("Namespace:", result.Text);
        Assert.Contains("Status:", result.Text);
    }

    [Fact]
    public void Compress_Describe_PreservesWarningEvents()
    {
        var input = "Name:         my-pod\nAnnotations:  kubectl.kubernetes.io/last-applied: {}\nEvents:\n  Type    Reason     Message\n  Warning BackOff    Back-off restarting failed container\n";
        var result = Make().Compress(KubectlInvocation("describe", "pod", "my-pod"), Out(input), CompressionOptions.Default);
        Assert.Contains("BackOff", result.Text);
    }

    [Fact]
    public void Compress_CompressedNotLargerThanOriginal()
    {
        var lines = new List<string> { "Name:   my-pod", "Namespace:   default", "Annotations:   many-annotations" };
        lines.AddRange(Enumerable.Range(1, 50).Select(i => $"  annotation-{i}: value-{i}"));
        lines.Add("Status:   Running");
        var result = Make().Compress(KubectlInvocation("describe", "pod", "my-pod"), Out(string.Join('\n', lines)), CompressionOptions.Default);
        Assert.True(result.CompressedTokens <= result.OriginalTokens);
    }
}
