using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class WindowsKoreanImeSwitchTests
{
    [Fact]
    public void Non_korean_ime_is_unchanged()
    {
        var probe = new FakeWindowsIme { LangId = 0x0411 };
        Assert.Null(WindowsKoreanImeSwitch.SwitchToAscii(probe));
        Assert.Empty(probe.Taps);

        probe.LangId = 0x0804;
        Assert.Null(WindowsKoreanImeSwitch.SwitchToAscii(probe));
        Assert.Empty(probe.Taps);

        probe.LangId = 0x0409;
        Assert.Null(WindowsKoreanImeSwitch.SwitchToAscii(probe));
        Assert.Empty(probe.Taps);
    }

    [Fact]
    public void Korean_open_ime_toggles_and_restore_taps_again()
    {
        var probe = new FakeWindowsIme { LangId = 0x0412, OpenStatus = 1 };
        var token = WindowsKoreanImeSwitch.SwitchToAscii(probe);
        Assert.NotNull(token);
        Assert.Equal(new ushort[] { KoreanImeToggle.VkHangul }, probe.Taps);
        probe.OpenStatus = 0;
        WindowsKoreanImeSwitch.Restore(token.Value, probe);
        Assert.Equal(new ushort[] { KoreanImeToggle.VkHangul, KoreanImeToggle.VkHangul }, probe.Taps);
    }

    [Fact]
    public void Korean_ascii_ime_does_not_toggle()
    {
        var probe = new FakeWindowsIme { LangId = 0x0812, OpenStatus = 0 };
        Assert.Null(WindowsKoreanImeSwitch.SwitchToAscii(probe));
        Assert.Empty(probe.Taps);
    }

    [Fact]
    public void Toggle_key_maps_korean_only()
    {
        Assert.Equal(KoreanImeToggle.VkHangul, KoreanImeToggle.ToggleKeyForLanguage(0x0412));
        Assert.Equal(KoreanImeToggle.VkHangul, KoreanImeToggle.ToggleKeyForLanguage(0x0812));
        Assert.Null(KoreanImeToggle.ToggleKeyForLanguage(0x0411));
        Assert.Null(KoreanImeToggle.ToggleKeyForLanguage(0x0804));
        Assert.Null(KoreanImeToggle.ToggleKeyForLanguage(0x0409));
        Assert.True(KoreanImeToggle.ImeOpen(1));
        Assert.False(KoreanImeToggle.ImeOpen(0));
    }

    [Fact]
    public void Queue_toggle_retries_keyup_when_only_keydown_lands()
    {
        var calls = 0;
        var ok = KoreanImeToggle.QueueToggleTap(KoreanImeToggle.VkHangul, count =>
        {
            calls++;
            return calls == 1 ? 1u : (uint)count;
        });
        Assert.True(ok);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Queue_toggle_fails_when_nothing_is_queued()
    {
        var calls = 0;
        var ok = KoreanImeToggle.QueueToggleTap(KoreanImeToggle.VkHangul, _ =>
        {
            calls++;
            return 0;
        });
        Assert.False(ok);
        Assert.Equal(1, calls);
    }

    private sealed class FakeWindowsIme : IWindowsImeProbe
    {
        public nint Fg { get; set; } = 10;
        public uint LangId { get; set; } = 0x0412;
        public nint Ime { get; set; } = 20;
        public nint OpenStatus { get; set; } = 1;
        public List<ushort> Taps { get; } = [];

        public nint ForegroundWindow() => Fg;

        public uint KeyboardLanguageId(nint hwnd) => LangId;

        public nint DefaultImeWindow(nint hwnd) => Ime;

        public nint? ReadOpenStatus(nint imeHwnd) => OpenStatus;

        public bool SendVkTap(ushort vk)
        {
            Taps.Add(vk);
            return true;
        }
    }
}
