using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Runtime.Domain.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class PiAdapterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public PiAdapterTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Key_IsPi()
    {
        var adapter = new PiAdapter();

        Assert.Equal("pi", adapter.Key);
        Assert.True(adapter.Capability.HasFlag(HarnessCapability.PreToolUse));
    }

    [Fact]
    public void ProjectInstallPlan_PatchesProjectSettingsPackagesArray()
    {
        var packageDir = Path.Combine(_tempDir, "packages", "pi-hypa");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{}");
        var adapter = new PiAdapter();

        var plan = adapter.GetInstallPlan(global: false, includeMcp: false, projectRoot: _tempDir);

        var op = Assert.IsType<InstallOperation.PatchJsonArrayValue>(Assert.Single(plan.Operations));
        Assert.Equal(Path.Combine(_tempDir, ".pi", "settings.json"), op.FilePath);
        Assert.Equal("packages", op.TopLevelKey);
        Assert.Equal(packageDir, op.Value);
    }

    [Fact]
    public void ProjectUninstallPlan_RemovesProjectSettingsPackage()
    {
        var packageDir = Path.Combine(_tempDir, "packages", "pi-hypa");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{}");
        var adapter = new PiAdapter();

        var plan = adapter.GetUninstallPlan(global: false, projectRoot: _tempDir);

        var op = Assert.IsType<UninstallOperation.RemoveJsonArrayValue>(Assert.Single(plan.Operations));
        Assert.Equal(Path.Combine(_tempDir, ".pi", "settings.json"), op.FilePath);
        Assert.Equal("packages", op.TopLevelKey);
        Assert.Equal(packageDir, op.Value);
    }
}
