using System.Security.Cryptography.X509Certificates;
using Hypa.Continuity.Tests.Impairment;
using Hypa.Continuity.Tests.RemoteTyping;
using Hypa.ControlPlane;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.Continuity.Tests;

/// <summary>
/// The remote typing matrix. The typing fixtures of the typing conformance
/// harness run through a live mux pane, the real accept stack and the
/// impairment proxy. The client clock measures <c>key_to_paint</c> with the
/// attach latency meter. The tests write per-key JSON and report p50, p95,
/// p99 and max for each cell.
/// <para>
/// The tests do not fail on latency. They fail when a typed pane does not
/// converge to the oracle screen, when no key of a fixture closes, when a key
/// stays open on a link with no loss, when the scripted pane reads other
/// bytes than the recorded keys, or when the client event loop stops on a
/// fault.
/// </para>
/// <para>
/// The tests skip unless <c>HYPA_REMOTE_TYPING_BENCH=1</c>
/// (<see cref="RemoteBenchOptIn"/>).
/// </para>
/// </summary>
[Trait("Category", "LiveQuic")]
[Trait("Category", "Ghostty")]
public sealed class RemoteTypingBenchTests(RemoteTypingCertificateFixture certificate, ITestOutputHelper output)
    : IClassFixture<RemoteTypingCertificateFixture>
{
    public static TheoryData<string, int, int, string> MatrixCells()
    {
        var data = new TheoryData<string, int, int, string>();
        foreach (var transport in new[] { EchoUnderBulkProbe.Quic, EchoUnderBulkProbe.Tcp })
        {
            foreach (var rtt in new[] { 0, 40, 80 })
            {
                foreach (var loss in new[] { 0, 1 })
                {
                    foreach (var load in new[] { RemoteTypingCell.Idle, RemoteTypingCell.Bulk })
                        data.Add(transport, rtt, loss, load);
                }
            }
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(MatrixCells))]
    public async Task Remote_typing_reports_key_to_paint_for_each_matrix_cell(
        string transport,
        int rttMs,
        int lossPercent,
        string load)
    {
        var profile = ImpairmentProfile.Wan(rttMs, lossPercent);
        var result = await RemoteTypingCellRun.RunAsync(
            new RemoteTypingCell(transport, load, profile),
            certificate,
            output,
            RemoteTypingOptions.FromEnvironment());
        RemoteTypingCellRun.AssertFunction(result);
        foreach (var fixture in result.Fixtures)
        {
            Assert.True(fixture.Divergence is null, $"{fixture.Fixture} did not converge: {fixture.Divergence}");
            Assert.True(fixture.Closed > 0, $"{fixture.Fixture}: no key closed; the meter saw no acked echo");
            // The recorded echo of each fixture changes the screen, and a
            // later frame also acks an earlier key, so on a link with no loss
            // every key closes. On a lossy link a key can wait past the close
            // wait. That is a latency result, and these tests do not gate on
            // latency.
            if (lossPercent == 0)
            {
                Assert.True(
                    fixture.Closed == fixture.Keys,
                    $"{fixture.Fixture}: {fixture.Keys - fixture.Closed} of {fixture.Keys} keys did not close on a link with no loss");
            }
        }
        if (lossPercent == 0)
            RemoteTypingLatencyGate.Assert(result, rttMs, load);
    }
}

/// <summary>
/// Bulk output through a bottleneck with a 64 KiB queue. The live baseline had
/// no cap. These cells show whether a cap puts the echo behind the bulk
/// render. They are opt-in (<c>Category=LinkImpairment</c>), and they report
/// only: a cap below the render rate can starve a pane, and that is the result.
/// </summary>
[Trait("Category", "LiveQuic")]
[Trait("Category", "Ghostty")]
[Trait("Category", "LinkImpairment")]
public sealed class RemoteTypingCapBenchTests(RemoteTypingCertificateFixture certificate, ITestOutputHelper output)
    : IClassFixture<RemoteTypingCertificateFixture>
{
    public static TheoryData<string, int, int> CapCells()
    {
        var data = new TheoryData<string, int, int>();
        foreach (var transport in new[] { EchoUnderBulkProbe.Quic, EchoUnderBulkProbe.Tcp })
        {
            foreach (var rtt in new[] { 0, 40 })
            {
                // 20 Mbit/s is above the bulk render rate of the mux. 1 Mbit/s
                // is below it, so the bottleneck queue fills.
                foreach (var capMbit in new[] { 20, 1 })
                    data.Add(transport, rtt, capMbit);
            }
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(CapCells))]
    public async Task Remote_typing_through_a_bandwidth_cap_reports_key_to_paint(string transport, int rttMs, int capMbit)
    {
        var profile = ImpairmentProfile.Wan(rttMs, 0) with
        {
            Name = "cap" + capMbit,
            BandwidthBytesPerSecond = capMbit * 1_000_000L / 8,
        };
        var cell = new RemoteTypingCell(transport, RemoteTypingCell.Bulk, profile);
        if (RemoteTypingLatencyGate.RenderCreditOffered)
        {
            // Render credit keeps the echo going below the render rate: the
            // cell completes, every fixture converges and every key closes.
            var credited = await RemoteTypingCellRun.RunAsync(cell, certificate, output, RemoteTypingOptions.FromEnvironment());
            RemoteTypingCellRun.AssertFunction(credited);
            Assert.True(credited.RenderCredit, "the connection did not use render credit");
            foreach (var fixture in credited.Fixtures)
            {
                Assert.True(fixture.Divergence is null, $"{fixture.Fixture} did not converge: {fixture.Divergence}");
                Assert.True(
                    fixture.Closed == fixture.Keys,
                    $"{fixture.Fixture}: {fixture.Keys - fixture.Closed} of {fixture.Keys} keys did not close");
            }

            // Without credit, the echo at 1 Mbit/s waited 0.5 to 1.4 s or
            // stopped. With credit, it waits for at most a budget and one
            // slice. The bar is loose, so a loaded host does not fail it.
            var bound = rttMs + 300.0;
            Assert.True(
                credited.KeyToPaint.P99 <= bound,
                $"{cell.Id}: echo p99 {credited.KeyToPaint.P99:0.0} ms, bound {bound:0} ms");
            return;
        }

        RemoteTypingCellResult result;
        try
        {
            result = await RemoteTypingCellRun.RunAsync(
                cell,
                certificate,
                output,
                RemoteTypingOptions.FromEnvironment() with { ReportOnly = true });
        }
        catch (Exception ex) when (ex is IOException or ControlPlaneClientTimeoutException)
        {
            // A cap below the render rate can stop the attach: a control
            // reply waits too long, or the ordered event lane of the client
            // goes over its byte budget. That is the result of this cell.
            output.WriteLine($"remote-typing {cell.Id} link=[{profile.Describe()}] did not complete: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        RemoteTypingCellRun.AssertFunction(result);
    }
}

/// <summary>
/// The latency gate of the matrix. It is off unless
/// <c>HYPA_REMOTE_TYPING_LATENCY_GATE=1</c>, because the bars are PROPOSED
/// values that H-412 has not agreed yet. With it on, a cell with no loss fails
/// when the echo misses its bar: loopback with bulk output p99 below 5 ms,
/// an idle pane p95 at most RTT + 4 ms, and a bulk pane p99 at most RTT + 16 ms.
/// </summary>
internal static class RemoteTypingLatencyGate
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("HYPA_REMOTE_TYPING_LATENCY_GATE"), "1", StringComparison.Ordinal);

    /// <summary>False when <c>HYPA_REMOTE_TYPING_RENDER_CREDIT=0</c> turns the offer off for an A/B.</summary>
    public static bool RenderCreditOffered =>
        !string.Equals(Environment.GetEnvironmentVariable("HYPA_REMOTE_TYPING_RENDER_CREDIT"), "0", StringComparison.Ordinal);

    public static void Assert(RemoteTypingCellResult result, int rttMs, string load)
    {
        if (!Enabled)
            return;
        var stats = result.KeyToPaint;
        if (load == RemoteTypingCell.Bulk)
        {
            var met = rttMs == 0 ? stats.P99 < 5.0 : stats.P99 <= rttMs + 16.0;
            var bar = rttMs == 0 ? "< 5" : "<= " + (rttMs + 16);
            Xunit.Assert.True(met, $"{result.Cell.Id}: p99 {stats.P99:0.0} ms, bar {bar} ms");
        }
        else
        {
            var bar = rttMs + 4.0;
            Xunit.Assert.True(stats.P95 <= bar, $"{result.Cell.Id}: p95 {stats.P95:0.0} ms, bar {bar:0.0} ms");
        }
    }
}

/// <summary>Runs one cell, writes its JSON, and prints its summary.</summary>
internal static class RemoteTypingCellRun
{
    public static async Task<RemoteTypingCellResult> RunAsync(
        RemoteTypingCell cell,
        RemoteTypingCertificateFixture certificate,
        ITestOutputHelper output,
        RemoteTypingOptions options)
    {
        RemoteBenchOptIn.Require();
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var library = LiveMuxLaunch.RequireGhosttyLibraryPath();
        var result = await RemoteTypingBench.RunAsync(cell, certificate.Certificate, library, options);
        var path = RemoteTypingBench.WriteJson(result, RemoteTypingBench.ResultsDirectory());
        output.WriteLine(result.Summary);
        output.WriteLine("per-key JSON: " + path);
        foreach (var fixture in result.Fixtures)
        {
            output.WriteLine(
                $"  {fixture.Fixture}: keys={fixture.Keys} closed={fixture.Closed} "
                + (fixture.Divergence is null ? "converged" : "DIVERGED " + fixture.Divergence));
        }

        return result;
    }

    /// <summary>The bench itself worked: the client loop ran and the scripted panes read the recorded keys.</summary>
    public static void AssertFunction(RemoteTypingCellResult result)
    {
        Assert.Null(result.ClientFault);
        Assert.True(
            result.InputMismatches == 0,
            $"{result.InputMismatches} key(s) reached the scripted pane with other bytes than the recorded input");
    }
}

/// <summary>
/// One loopback certificate for a test class. It is made before any QUIC
/// listener starts: on macOS a live MsQuic listener stops a later RSA key
/// export in the same process.
/// </summary>
public sealed class RemoteTypingCertificateFixture : IDisposable
{
    public X509Certificate2 Certificate { get; } = JoinTestPairs.CreateLoopbackCertificate();

    public void Dispose() => Certificate.Dispose();
}
