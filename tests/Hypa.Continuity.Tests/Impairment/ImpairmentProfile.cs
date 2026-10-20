using System.Globalization;

namespace Hypa.Continuity.Tests.Impairment;

/// <summary>
/// The link that <see cref="ImpairmentProxy"/> applies. Each direction gets
/// half of <see cref="Rtt"/>, a uniform jitter in
/// [-<see cref="Jitter"/>, +<see cref="Jitter"/>], and a Bernoulli loss
/// at <see cref="LossRate"/>. A <see cref="BandwidthBytesPerSecond"/> above
/// zero adds a bottleneck queue of <see cref="QueueLimitBytes"/> to each
/// direction.
/// </summary>
internal sealed record ImpairmentProfile
{
    /// <summary>A pass-through link: no delay, no loss, no cap.</summary>
    public static ImpairmentProfile None { get; } = new() { Name = "none" };

    public string Name { get; init; } = "";

    /// <summary>The round-trip time that the proxy adds. Each direction gets one half.</summary>
    public TimeSpan Rtt { get; init; }

    /// <summary>
    /// The largest jitter for each direction. A packet gets a uniform value in
    /// [-Jitter, +Jitter]. The proxy keeps the packet order, so jitter never
    /// reorders packets.
    /// </summary>
    public TimeSpan Jitter { get; init; }

    /// <summary>
    /// The probability that one packet is lost. On UDP the proxy drops the
    /// datagram. On TCP the proxy cannot drop bytes, so a lost segment and
    /// every segment behind it wait for <see cref="TcpLossPenalty"/>.
    /// </summary>
    public double LossRate { get; init; }

    /// <summary>The bottleneck rate for each direction. Zero means no cap.</summary>
    public long BandwidthBytesPerSecond { get; init; }

    /// <summary>
    /// The bottleneck queue for each direction when a cap is set. UDP drops a
    /// datagram that does not fit (tail drop). TCP stops the read until the
    /// queue has room, so the sender sees back pressure.
    /// </summary>
    public int QueueLimitBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// The limit of TCP bytes in flight in one direction when no cap is set.
    /// It stands in for the TCP window, so a fast sender cannot fill memory.
    /// </summary>
    public int TcpWindowBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>The seed of the loss and jitter random sequence.</summary>
    public int Seed { get; init; } = 7919;

    /// <summary>
    /// The stall of a lost TCP segment: one tail loss probe time,
    /// max(2 x RTT, 10 ms). The retransmit then has the normal one-way delay.
    /// </summary>
    public TimeSpan TcpLossPenalty =>
        TimeSpan.FromTicks(Math.Max(2 * Rtt.Ticks, TimeSpan.FromMilliseconds(10).Ticks));

    public bool IsPassThrough =>
        Rtt <= TimeSpan.Zero && Jitter <= TimeSpan.Zero && LossRate <= 0 && BandwidthBytesPerSecond <= 0;

    public static ImpairmentProfile Wan(int rttMs, double lossPercent, string? name = null) =>
        new()
        {
            Name = name ?? $"rtt{rttMs}-loss{lossPercent.ToString(CultureInfo.InvariantCulture)}",
            Rtt = TimeSpan.FromMilliseconds(rttMs),
            // One tenth of the one-way delay, as a light WAN jitter.
            Jitter = TimeSpan.FromMilliseconds(rttMs / 20.0),
            LossRate = lossPercent / 100.0,
        };

    public string Describe()
    {
        var cap = BandwidthBytesPerSecond > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"cap={BandwidthBytesPerSecond * 8 / 1_000_000.0:0.##}Mbit/s queue={QueueLimitBytes / 1024}KiB")
            : "cap=none";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"rtt={Rtt.TotalMilliseconds:0.#}ms jitter=+-{Jitter.TotalMilliseconds:0.#}ms loss={LossRate * 100:0.##}% {cap} seed={Seed}");
    }
}
