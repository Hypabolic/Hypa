using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachSurfaceWriterLaneTests
{
    [Fact]
    public async Task Dequeue_skips_render_when_binding_recheck_fails()
    {
        var lanes = new ClientWriterQueue();
        var binding = SampleBinding();
        var line = Encoding.UTF8.GetBytes("{\"event\":\"terminal.render\"}\n");
        var admitted = lanes.EnqueueOrderedRender(line.AsMemory(), binding);
        Assert.True(admitted.IsOk);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var item = await lanes.DequeueAsync(cts.Token, _ => false);

        Assert.Null(item);
    }

    [Fact]
    public async Task Dequeue_writes_render_when_binding_recheck_passes()
    {
        var lanes = new ClientWriterQueue();
        var binding = SampleBinding();
        var line = Encoding.UTF8.GetBytes("{\"event\":\"terminal.render\"}\n");
        var admitted = lanes.EnqueueOrderedRender(line.AsMemory(), binding);
        Assert.True(admitted.IsOk);

        var item = await lanes.DequeueAsync(CancellationToken.None, _ => true);

        Assert.NotNull(item);
        Assert.Same(binding, item!.EmitBinding);
    }

    [Fact]
    public async Task DiscardPendingRender_drops_replaceable_render_slot()
    {
        var lanes = new ClientWriterQueue();
        var line = Encoding.UTF8.GetBytes("{\"event\":\"terminal.render\"}\n");
        Assert.True(lanes.TryEnqueueRender(line).IsOk);
        lanes.DiscardPendingRender();
        lanes.Complete();

        var item = await lanes.DequeueAsync(CancellationToken.None);

        Assert.Null(item);
    }

    [Fact]
    public async Task Ordered_batch_preserves_emit_binding_on_every_line()
    {
        var lanes = new ClientWriterQueue();
        var binding = SampleBinding();
        var lines = new ReadOnlyMemory<byte>[]
        {
            Encoding.UTF8.GetBytes("{\"event\":\"terminal.render\",\"seq\":1}\n"),
            Encoding.UTF8.GetBytes("{\"event\":\"terminal.render\",\"seq\":2}\n"),
        };
        Assert.True(lanes.EnqueueOrderedRenderBatch(lines, binding).IsOk);

        var first = await lanes.DequeueAsync(CancellationToken.None, _ => true);
        var second = await lanes.DequeueAsync(CancellationToken.None, _ => true);

        Assert.Same(binding, first!.EmitBinding);
        Assert.Same(binding, second!.EmitBinding);
    }

    [Fact]
    public async Task Dequeue_drops_unbound_render_when_attach_admission_required()
    {
        var lanes = new ClientWriterQueue();
        var line = Encoding.UTF8.GetBytes("{\"event\":\"terminal.render\"}\n");
        Assert.True(lanes.TryEnqueueRender(line).IsOk);
        lanes.Complete();

        var item = await lanes.DequeueAsync(CancellationToken.None, binding => binding is not null);

        Assert.Null(item);
    }

    [Fact]
    public async Task Dequeue_writes_unbound_render_when_local_attach_allows_null()
    {
        var lanes = new ClientWriterQueue();
        var line = Encoding.UTF8.GetBytes("{\"event\":\"terminal.render\"}\n");
        Assert.True(lanes.TryEnqueueRender(line).IsOk);

        var item = await lanes.DequeueAsync(CancellationToken.None, binding => binding is null);

        Assert.NotNull(item);
        Assert.Null(item!.EmitBinding);
    }

    [Fact]
    public async Task Dequeue_skips_bound_reliable_when_binding_recheck_fails()
    {
        var lanes = new ClientWriterQueue();
        var binding = SampleBinding();
        lanes.EnqueueResponseWait("{\"event\":\"terminal.live\"}\n", binding, out var written);
        lanes.Complete();

        var item = await lanes.DequeueAsync(CancellationToken.None, _ => false);

        Assert.Null(item);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => written);
    }

    [Fact]
    public async Task Dequeue_admits_bound_reliable_when_binding_recheck_passes()
    {
        var lanes = new ClientWriterQueue();
        var binding = SampleBinding();
        lanes.EnqueueResponseWait("{\"event\":\"terminal.live\"}\n", binding, out _);

        var item = await lanes.DequeueAsync(CancellationToken.None, _ => true);

        Assert.NotNull(item);
        Assert.True(item!.Reliable);
        Assert.Same(binding, item.EmitBinding);
    }

    [Fact]
    public async Task DiscardPendingRender_cancels_bound_reliable_control_item()
    {
        var lanes = new ClientWriterQueue();
        var binding = SampleBinding();
        lanes.EnqueueResponseWait("{\"event\":\"terminal.live\"}\n", binding, out var written);
        lanes.DiscardPendingRender();
        lanes.Complete();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => written);
        Assert.Null(await lanes.DequeueAsync(CancellationToken.None, _ => true));
    }

    [Fact]
    public async Task DiscardPendingRender_clears_in_flight_bound_render()
    {
        var lanes = new ClientWriterQueue();
        var binding = SampleBinding();
        var line = Encoding.UTF8.GetBytes("{\"event\":\"terminal.render\"}\n");
        Assert.True(lanes.EnqueueOrderedRender(line.AsMemory(), binding).IsOk);

        var admitted = true;
        var item = await lanes.DequeueAsync(CancellationToken.None, _ => admitted);
        Assert.NotNull(item);
        admitted = false;
        lanes.DiscardPendingRender();
        lanes.NotifyWriteCompleted();
        lanes.Complete();

        Assert.Null(await lanes.DequeueAsync(CancellationToken.None, _ => false));
    }

    private static AttachSurfaceEmitBinding SampleBinding() =>
        new()
        {
            ConnectionId = "conn_1",
            BootId = "boot_a",
            ConnectionGeneration = 1,
            LeaseId = "lease_1",
            ProjectionRevision = 1,
            SurfaceRevision = 1,
            SnapshotRevision = 1,
            GeometryRevision = 1,
            Columns = 80,
            Rows = 24,
        };
}
