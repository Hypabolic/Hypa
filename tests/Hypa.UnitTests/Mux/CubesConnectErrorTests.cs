using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.UnitTests.Mux;

public sealed class CubesConnectErrorTests
{
    [Fact]
    public async Task Local_destination_fails_with_local_destination_code()
    {
        var resolver = new DirectoryCubesConnectEndpointResolver();

        var result = await resolver.ResolveAsync(Item(SidebarCubeKind.Local, "plc_local01"));

        Assert.False(result.IsOk);
        Assert.Equal(CubesConnectErrorReasons.LocalDestination, result.Error.Reason);
    }

    [Fact]
    public async Task Disabled_quic_cube_fails_with_connect_disabled_code()
    {
        var resolver = new DirectoryCubesConnectEndpointResolver();

        var result = await resolver.ResolveAsync(Item(
            SidebarCubeKind.Peer,
            "plc_quic01",
            providerSuffix: "QUIC · Reachable",
            connectEnabled: false));

        Assert.False(result.IsOk);
        Assert.Equal(CubesConnectErrorReasons.ConnectDisabled, result.Error.Reason);
    }

    [Fact]
    public async Task Unreachable_peer_fails_with_unreachable_code_and_detail()
    {
        var resolver = new DirectoryCubesConnectEndpointResolver();

        var result = await resolver.ResolveAsync(Item(
            SidebarCubeKind.Peer,
            "plc_peer01",
            reachability: SidebarCubeReachability.Unreachable));

        Assert.False(result.IsOk);
        Assert.Equal(CubesConnectErrorReasons.Unreachable, result.Error.Reason);
        Assert.Equal("sidebar reachability is Unreachable", result.Error.Detail);
    }

    [Fact]
    public async Task Destination_id_that_is_not_a_placement_fails_with_placement_id_invalid_code()
    {
        var resolver = new DirectoryCubesConnectEndpointResolver();

        var result = await resolver.ResolveAsync(Item(SidebarCubeKind.Peer, "not a placement"));

        Assert.False(result.IsOk);
        Assert.Equal(CubesConnectErrorReasons.PlacementIdInvalid, result.Error.Reason);
    }

    [Fact]
    public void Refused_outbound_join_keeps_its_connectivity_reason()
    {
        var outcome = CubesConnectOutboundJoin.ToResult(
            ConnectivityOutcome<OutboundFramedSession>.Failure(
                ConnectivityReasons.JoinDenied,
                "join denied by rendezvous"));

        Assert.False(outcome.IsOk);
        Assert.Equal(ConnectivityReasons.JoinDenied, outcome.Error.Reason);
        Assert.Equal("join denied by rendezvous", outcome.Error.Detail);
    }

    [Fact]
    public void Connect_error_detail_is_one_line()
    {
        var error = ConnectErrors.FromException(
            CubesConnectErrorReasons.DialFailed,
            new IOException("first line\nsecond line"));

        Assert.Equal(CubesConnectErrorReasons.DialFailed, error.Reason);
        Assert.Equal("first line", error.Detail);
    }

    [Fact]
    public void Outcome_log_record_carries_reason_and_detail()
    {
        var sink = new RecordingLog();

        AttachProcessLog.CubesOutcome(
            sink,
            "connect",
            ProcessLogEvents.OutcomeError,
            sessionId: "default",
            attachClientId: "client-1",
            reason: ConnectivityReasons.JoinDenied,
            detail: "join denied by rendezvous");

        var record = Assert.Single(sink.Records);
        Assert.Equal(ProcessLogEvents.CubesConnectOutcome, record.Event);
        Assert.Equal(ProcessLogEvents.OutcomeError, record.Outcome);
        Assert.Equal(ConnectivityReasons.JoinDenied, record.Reason);
        Assert.Equal("join denied by rendezvous", record.Err);
    }

    [Fact]
    public void Failed_stage_record_is_error_with_reason()
    {
        var sink = new RecordingLog();

        AttachProcessLog.CubesStage(
            sink,
            CubesConnectStages.Join,
            elapsedMs: 16,
            stageMs: 3,
            endpointId: "plc_quic01",
            transport: null,
            sessionId: null,
            attachClientId: null,
            failureReason: ConnectivityReasons.Unauthorized);

        var record = Assert.Single(sink.Records);
        Assert.Equal(ProcessLogEvents.OutcomeError, record.Outcome);
        Assert.Equal(CubesConnectStages.Join, record.Stage);
        Assert.Equal(ConnectivityReasons.Unauthorized, record.Reason);
    }

    [Fact]
    public void Successful_stage_record_is_ok_without_reason()
    {
        var sink = new RecordingLog();

        AttachProcessLog.CubesStage(
            sink,
            CubesConnectStages.Dial,
            elapsedMs: 13,
            stageMs: 13,
            endpointId: "plc_quic01",
            transport: "quic",
            sessionId: null,
            attachClientId: null);

        var record = Assert.Single(sink.Records);
        Assert.Equal(ProcessLogEvents.OutcomeOk, record.Outcome);
        Assert.Null(record.Reason);
    }

    [Fact]
    public void Join_rejection_line_names_reason_placement_and_peer()
    {
        var writer = new StringWriter();

        ConnectivityCommand.WriteJoinRejected(
            writer,
            new object(),
            new AcceptJoinRejection(
                ConnectivityReasons.JoinDenied,
                "device is revoked",
                "plc_remote01",
                "10.0.0.7:51234"));

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("join_rejected", root.GetProperty("event").GetString());
        Assert.Equal("join_denied", root.GetProperty("reason").GetString());
        Assert.Equal("device is revoked", root.GetProperty("detail").GetString());
        Assert.Equal("plc_remote01", root.GetProperty("placement_id").GetString());
        Assert.Equal("10.0.0.7:51234", root.GetProperty("peer").GetString());
    }

    [Theory]
    [InlineData(ConnectivityReasons.JoinDenied, "Remote refused the connection (join denied) [join_denied]")]
    [InlineData(ConnectivityReasons.Unauthorized, "Remote did not accept this device (unauthorized) [unauthorized]")]
    [InlineData(ConnectivityReasons.PeerUnavailable, "Remote is not reachable (peer unavailable) [peer_unavailable]")]
    [InlineData(CubesConnectErrorReasons.PlacementChanged, "Remote changed while connecting. Try again. [placement_changed]")]
    [InlineData(CubesConnectErrorReasons.DialFailed, "Remote connect failed [dial_failed]")]
    public void Connect_failure_copy_names_the_cause_in_brackets(string code, string expected)
    {
        var copy = AttachEndpointUserCopy.ConnectFailed("Remote", new ConnectError(code));

        Assert.Equal(expected, copy);
    }

    private static SidebarCubeItem Item(
        SidebarCubeKind kind,
        string id,
        SidebarCubeReachability reachability = SidebarCubeReachability.Reachable,
        string? providerSuffix = null,
        bool connectEnabled = true) =>
        new()
        {
            Id = id,
            Name = "Remote",
            Kind = kind,
            Reachability = reachability,
            ProviderSuffix = providerSuffix,
            ConnectEnabled = connectEnabled,
        };

    private sealed class RecordingLog : IProcessLogSink
    {
        public List<ProcessLogRecord> Records { get; } = [];

        public bool IsEnabled(ProcessLogLevel level) => true;

        public bool Disabled => false;

        public string? Path => null;

        public void Write(ProcessLogRecord record) => Records.Add(record);
    }
}
