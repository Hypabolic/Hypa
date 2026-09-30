using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class VisibleSetPublicationTests
{
    [Fact]
    public void Hide_then_reveal_without_hidden_capture_requires_full()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        service.NoteAttached("client");
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });

        var first = service.AdmitCapture("pane", clock.UtcNow);
        Assert.Equal(CaptureAdmissionKind.CaptureFull, first.Kind);
        service.CommitCapture("pane", first.Generation);

        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = [] });
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });

        var reveal = service.AdmitCapture("pane", clock.UtcNow);
        Assert.Equal(CaptureAdmissionKind.CaptureFull, reveal.Kind);
    }

    [Fact]
    public void Stale_commit_does_not_consume_newer_reveal()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        service.NoteAttached("client");
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });

        var first = service.AdmitCapture("pane", clock.UtcNow);
        Assert.Equal(CaptureAdmissionKind.CaptureFull, first.Kind);
        service.CommitCapture("pane", first.Generation);

        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = [] });
        var hidden = service.AdmitCapture("pane", clock.UtcNow);
        Assert.Equal(CaptureAdmissionKind.Skip, hidden.Kind);
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });
        service.CommitCapture("pane", first.Generation);
        service.CommitCapture("pane", hidden.Generation);

        var reveal = service.AdmitCapture("pane", clock.UtcNow);
        Assert.Equal(CaptureAdmissionKind.CaptureFull, reveal.Kind);
        Assert.NotEqual(first.Generation, reveal.Generation);
    }

    [Fact]
    public void Repeated_unchanged_heartbeat_does_not_force_full()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        service.NoteAttached("client");
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });
        var first = service.AdmitCapture("pane", clock.UtcNow);
        service.CommitCapture("pane", first.Generation);

        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });
        Assert.Equal(CaptureAdmissionKind.Capture, service.AdmitCapture("pane", clock.UtcNow).Kind);

        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(service.Renew("client").IsOk);
        Assert.Equal(CaptureAdmissionKind.Capture, service.AdmitCapture("pane", clock.UtcNow).Kind);
    }

    [Fact]
    public void Failed_capture_leaves_pending_full()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        service.NoteAttached("client");
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });

        var first = service.AdmitCapture("pane", clock.UtcNow);
        Assert.Equal(CaptureAdmissionKind.CaptureFull, first.Kind);
        var again = service.AdmitCapture("pane", clock.UtcNow);
        Assert.Equal(CaptureAdmissionKind.CaptureFull, again.Kind);
        Assert.Equal(first.Generation, again.Generation);
    }

    [Fact]
    public void Live_unknown_failed_and_expired_fail_open()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        service.NoteAttached("client");
        Assert.True(service.MayCapture("hidden", clock.UtcNow));
        Assert.Equal(CaptureAdmissionKind.CaptureFull, service.AdmitCapture("hidden", clock.UtcNow).Kind);

        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });
        var posted = service.AdmitCapture("pane", clock.UtcNow);
        service.CommitCapture("pane", posted.Generation);

        service.MarkFailed("client");
        Assert.True(service.MayCapture("other", clock.UtcNow));
        Assert.Equal(CaptureAdmissionKind.CaptureFull, service.AdmitCapture("other", clock.UtcNow).Kind);

        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });
        var fresh = service.AdmitCapture("pane", clock.UtcNow);
        service.CommitCapture("pane", fresh.Generation);
        clock.Advance(VisibleSetPublication.Freshness + TimeSpan.FromSeconds(1));
        Assert.True(service.MayCapture("expired", clock.UtcNow));
        Assert.Equal(CaptureAdmissionKind.CaptureFull, service.AdmitCapture("expired", clock.UtcNow).Kind);
        Assert.True(service.AdmitCapture("pane", clock.UtcNow).ShouldCapture);
    }

    [Fact]
    public void Zero_live_clients_skip()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        Assert.False(service.MayCapture("pane", clock.UtcNow));
        Assert.Equal(CaptureAdmissionKind.Skip, service.AdmitCapture("pane", clock.UtcNow).Kind);

        service.NoteAttached("client");
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane"] });
        service.Remove("client");
        Assert.False(service.MayCapture("pane", clock.UtcNow));
        Assert.Equal(CaptureAdmissionKind.Skip, service.AdmitCapture("pane", clock.UtcNow).Kind);
    }

    [Fact]
    public void Disconnect_drops_client_from_union()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        service.NoteAttached("a");
        service.NoteAttached("b");
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "a", PaneIds = ["one"] });
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "b", PaneIds = ["two"] });

        Assert.True(service.MayCapture("one", clock.UtcNow));
        Assert.True(service.MayCapture("two", clock.UtcNow));
        service.Remove("b");
        Assert.True(service.MayCapture("one", clock.UtcNow));
        Assert.False(service.MayCapture("two", clock.UtcNow));
        Assert.Equal(CaptureAdmissionKind.Skip, service.AdmitCapture("two", clock.UtcNow).Kind);
    }

    [Fact]
    public void Overlay_id_joins_union_for_two_clients()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        service.NoteAttached("a");
        service.NoteAttached("b");
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "a", PaneIds = ["tiled"] });
        service.Publish(new VisibleSetPublishRequest
        {
            ConnectionId = "b",
            PaneIds = ["other"],
            OverlayPaneId = "overlay",
        });

        Assert.True(service.MayCapture("tiled", clock.UtcNow));
        Assert.True(service.MayCapture("other", clock.UtcNow));
        Assert.True(service.MayCapture("overlay", clock.UtcNow));

        var published = service.PublishOverlay("b", null);
        Assert.True(published.IsOk);
        Assert.False(service.MayCapture("overlay", clock.UtcNow));
    }

    [Fact]
    public void Empty_publish_keeps_retained_pane_ids()
    {
        var clock = new MutableClock();
        var service = new VisibleSetPublication(clock);
        service.NoteAttached("client");
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = ["pane-a"] });
        service.Publish(new VisibleSetPublishRequest { ConnectionId = "client", PaneIds = [] });
        Assert.Equal(["pane-a"], service.RetainedPaneIds("client"));
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } =
            new(2026, 9, 8, 4, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan delta) => UtcNow += delta;
    }
}
