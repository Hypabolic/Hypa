using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class MacOsAsciiInputSwitchTests
{
    [Fact]
    public void Prefix_switch_restores_the_previous_source()
    {
        var api = new FakeTisApi();
        var source = new PrefixAsciiInputAdapter(
            () => MacOsAsciiInputSwitch.SwitchToAscii(api),
            api.PumpRunLoop);
        source.SwitchToAscii();
        Assert.Equal(1, api.PumpCount);
        Assert.Equal(2, api.Current);
        Assert.Equal(new nint[] { 2 }, api.Selected);
        source.Restore();
        Assert.Equal(1, api.Current);
        Assert.Equal(new nint[] { 2, 1 }, api.Selected);
    }

    [Fact]
    public void Already_ascii_source_does_not_switch()
    {
        var api = new FakeTisApi { Current = 2, Ascii = 2 };
        var restore = MacOsAsciiInputSwitch.SwitchToAscii(api);
        Assert.Null(restore);
        Assert.Empty(api.Selected);
    }

    [Fact]
    public void Second_switch_keeps_the_first_saved_source()
    {
        var api = new FakeTisApi();
        var source = new PrefixAsciiInputAdapter(
            () => MacOsAsciiInputSwitch.SwitchToAscii(api),
            api.PumpRunLoop);
        source.SwitchToAscii();
        api.Current = 3;
        source.SwitchToAscii();
        Assert.Equal(1, api.PumpCount);
        Assert.Equal(3, api.Current);
        source.Restore();
        Assert.Equal(1, api.Current);
    }

    [Fact]
    public void Restore_is_idempotent_after_prefix_switch()
    {
        var restores = 0;
        var source = new PrefixAsciiInputAdapter(() => new CountingRestore(() => restores++));
        source.SwitchToAscii();
        source.Restore();
        source.Restore();
        Assert.Equal(1, restores);
    }

    private sealed class CountingRestore : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public CountingRestore(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _onDispose();
        }
    }

    private sealed class FakeTisApi : IMacOsTisApi
    {
        public nint Current { get; set; } = 1;
        public nint Ascii { get; set; } = 2;
        public int PumpCount { get; private set; }
        public List<nint> Selected { get; } = [];

        public void PumpRunLoop() => PumpCount++;

        public nint CopyCurrentKeyboardInputSource() => Current;

        public nint CopyAsciiCapableKeyboardLayoutInputSource() => Ascii;

        public bool SameInputSourceId(nint left, nint right) => left == right;

        public int SelectInputSource(nint source)
        {
            Selected.Add(source);
            Current = source;
            return 0;
        }

        public void Release(nint source)
        {
        }
    }
}
