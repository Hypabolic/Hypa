using Hypa.Terminal.Pty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
///: Unix default is hypa-pty-host. Windows stays process-io.
/// process-io remains an explicit test/batch opt-in. Managed-fork keys are gone.
/// </summary>
[Collection("ProcessSpawnTests")]
public class PtyProviderSelectionTests
{
    [SkippableFact]
    public void FromEnvironment_unix_defaults_to_hypa_pty_host()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix default is hypa-pty-host");

        var previous = Environment.GetEnvironmentVariable("HYPA_PTY_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", null);
            var opts = PtyProviderOptions.FromEnvironment();
            Assert.Equal(PtyProviderKind.HypaPtyHost, opts.Provider);
            Assert.Equal("hypa-pty-host", opts.ProviderWireName);
            Assert.True(opts.Interactive);
            Assert.True(opts.RequireHelperBinary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", previous);
        }
    }

    [SkippableFact]
    public void FromEnvironment_windows_stays_process_io()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows default is process-io");

        var previous = Environment.GetEnvironmentVariable("HYPA_PTY_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", null);
            var opts = PtyProviderOptions.FromEnvironment();
            Assert.Equal(PtyProviderKind.ProcessIo, opts.Provider);
            Assert.Equal("process-io", opts.ProviderWireName);
            Assert.False(opts.Interactive);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", previous);
        }
    }

    [Fact]
    public void Explicit_process_io_still_selectable()
    {
        var previous = Environment.GetEnvironmentVariable("HYPA_PTY_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", "process-io");
            var opts = PtyProviderOptions.FromEnvironment();
            Assert.Equal(PtyProviderKind.ProcessIo, opts.Provider);
            Assert.Equal("process-io", opts.ProviderWireName);
            Assert.False(opts.Interactive);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", previous);
        }
    }

    [Theory]
    [InlineData("hypa-pty-host", PtyProviderKind.HypaPtyHost, "hypa-pty-host", true)]
    [InlineData("process-io", PtyProviderKind.ProcessIo, "process-io", false)]
    public void ParseProvider_maps_wire_names(
        string raw, PtyProviderKind kind, string wire, bool interactive)
    {
        Assert.Equal(kind, PtyProviderOptions.ParseProvider(raw));
        var opts = new PtyProviderOptions { Provider = kind };
        Assert.Equal(wire, opts.ProviderWireName);
        Assert.Equal(interactive, opts.Interactive);
    }

    [Fact]
    public void ParseProvider_rejects_unix_experimental()
    {
        var rejected = string.Concat("unix", "-", "experimental");
        var ex = Assert.Throws<ArgumentException>(() => PtyProviderOptions.ParseProvider(rejected));
        Assert.Contains("process-io", ex.Message, StringComparison.Ordinal);
        Assert.Contains("hypa-pty-host", ex.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            PtyProviderOptions.ParseProvider(string.Concat("unix", "_", "experimental")));
        Assert.Throws<ArgumentException>(() => PtyProviderOptions.ParseProvider("unix"));
        Assert.Throws<ArgumentException>(() => PtyProviderOptions.ParseProvider("experimental"));
    }

    [Fact]
    public void Legacy_use_real_pty_env_is_ignored()
    {
        var previous = Environment.GetEnvironmentVariable("HYPA_PTY_PROVIDER");
        var legacyKey = string.Concat("HYPA_USE_REAL", "_PTY");
        var previousLegacy = Environment.GetEnvironmentVariable(legacyKey);
        try
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", null);
            Environment.SetEnvironmentVariable(legacyKey, "1");
            var opts = PtyProviderOptions.FromEnvironment();
            if (OperatingSystem.IsWindows())
                Assert.Equal(PtyProviderKind.ProcessIo, opts.Provider);
            else
                Assert.Equal(PtyProviderKind.HypaPtyHost, opts.Provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", previous);
            Environment.SetEnvironmentVariable(legacyKey, previousLegacy);
        }
    }

    [Fact]
    public void Explicit_provider_wins_over_legacy_flag()
    {
        var previous = Environment.GetEnvironmentVariable("HYPA_PTY_PROVIDER");
        var legacyKey = string.Concat("HYPA_USE_REAL", "_PTY");
        var previousLegacy = Environment.GetEnvironmentVariable(legacyKey);
        try
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", "process-io");
            Environment.SetEnvironmentVariable(legacyKey, "1");
            var opts = PtyProviderOptions.FromEnvironment();
            Assert.Equal(PtyProviderKind.ProcessIo, opts.Provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", previous);
            Environment.SetEnvironmentVariable(legacyKey, previousLegacy);
        }
    }

    [SkippableFact]
    public async Task Process_io_factory_spawns_without_host_binary()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX /bin/echo");

        var factory = new PtyProcessFactory(new PtyProviderOptions
        {
            Provider = PtyProviderKind.ProcessIo,
            RequireHelperBinary = false,
        });

        Assert.Equal(PtyProviderKind.ProcessIo, factory.Options.Provider);

        var proc = factory.Spawn(
            "/bin/echo",
            ["provider-process-io"],
            Path.GetTempPath(),
            80,
            24);
        await using (proc.ConfigureAwait(false))
        {
            Assert.IsType<ProcessPtyFallback>(proc);
            Assert.True(proc.Pid > 0);
        }
    }

    [Fact]
    public void Hypa_pty_host_fails_closed_when_binary_missing_and_required()
    {
        var factory = new PtyProcessFactory(new PtyProviderOptions
        {
            Provider = PtyProviderKind.HypaPtyHost,
            HelperPath = Path.Combine(Path.GetTempPath(), "no-such-hypa-pty-host-" + Guid.NewGuid().ToString("N")),
            RequireHelperBinary = true,
        });

        Assert.ThrowsAny<Exception>(() => factory.Spawn(
            "/bin/true",
            [],
            Path.GetTempPath(),
            80,
            24));
    }
}
