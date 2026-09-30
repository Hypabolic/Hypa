namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Korean IME toggle policy.
/// </summary>
public static class KoreanImeToggle
{
    // / <summary>Virtual key that toggles Hangul/English.
    public const ushort VkHangul = 0x15;

    // / <summary>Primary language id for Korean (low 10 bits).
    public const uint LangKorean = 0x12;

    /// <summary>
    /// <c>IMC_GETOPENSTATUS</c> is nonzero when the IME is open (Hangul).
    /// </summary>
    public static bool ImeOpen(nint openStatus) => openStatus != 0;

    /// <summary>
    // / Toggle key for a LANGID. Only Korean is mapped.
    /// <c>src/platform/windows.rs:2461-2466</c> <c>toggle_key_for_language</c>.
    /// </summary>
    public static ushort? ToggleKeyForLanguage(uint langid) =>
        (langid & 0x3FF) == LangKorean ? VkHangul : null;

    /// <summary>
    // / Queue a key-down then key-up.
    /// <c>src/platform/windows.rs:2516-2545</c> <c>send_vk_tap_with</c>.
    /// <paramref name="injectQueued"/> receives the event count and returns how
    /// many events the host queued.
    /// </summary>
    public static bool QueueToggleTap(ushort vk, Func<int, uint> injectQueued)
    {
        ArgumentNullException.ThrowIfNull(injectQueued);
        _ = vk;
        var sent = injectQueued(2);
        if (sent >= 2)
            return true;
        if (sent == 1)
        {
            _ = injectQueued(1);
            return true;
        }

        return false;
    }
}
