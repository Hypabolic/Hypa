namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Switch a Korean IME to ASCII for prefix mode. Other IME languages stay.
/// </summary>
public static class WindowsKoreanImeSwitch
{
    public static WindowsImeRestore? SwitchToAscii(IWindowsImeProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var fg = probe.ForegroundWindow();
        if (fg == 0)
            return null;
        var toggle = KoreanImeToggle.ToggleKeyForLanguage(probe.KeyboardLanguageId(fg));
        if (toggle is null)
            return null;
        var ime = probe.DefaultImeWindow(fg);
        if (ime == 0)
            return null;
        var open = probe.ReadOpenStatus(ime);
        if (open is null)
            return null;
        if (!KoreanImeToggle.ImeOpen(open.Value))
            return null;
        if (probe.ForegroundWindow() != fg)
            return null;
        if (!probe.SendVkTap(toggle.Value))
            return null;
        return new WindowsImeRestore(toggle.Value, fg);
    }

    public static void Restore(WindowsImeRestore token, IWindowsImeProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var fg = probe.ForegroundWindow();
        if (fg == 0 || fg != token.OriginHwnd)
            return;
        var ime = probe.DefaultImeWindow(fg);
        if (ime == 0)
            return;
        var open = probe.ReadOpenStatus(ime);
        if (open is null)
            return;
        if (KoreanImeToggle.ImeOpen(open.Value))
            return;
        if (probe.ForegroundWindow() != fg)
            return;
        _ = probe.SendVkTap(token.ToggleVk);
    }
}
