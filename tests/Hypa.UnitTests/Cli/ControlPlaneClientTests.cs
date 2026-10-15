using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.Cli.Attach;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class ControlPlaneClientTests
{
    [Fact]
    public async Task EventAdmittedCallbackSeesTheEventInTheQueue()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await accept;
        await using var client = ControlPlaneClient.FromConnectedStream(clientSocket.GetStream());
        var drained = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SetEventAdmitted(() => drained.TrySetResult(client.DrainPendingAllEvents().Count));
        await client.ConnectAsync();

        var payload = Encoding.UTF8.GetBytes(
            "{\"event\":\"runtime.event\",\"params\":{\"type\":\"ping\"}}\n");
        await serverSocket.GetStream().WriteAsync(payload);

        Assert.Equal(1, await drained.Task.WaitAsync(TestWait.HangGuard));
    }

    [Fact]
    public async Task ReaderReportsEveryLineToLineReceivedCallback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await accept;
        await using var client = ControlPlaneClient.FromConnectedStream(clientSocket.GetStream());
        var lines = 0;
        var bothLines = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SetLineReceived(() =>
        {
            if (Interlocked.Increment(ref lines) >= 2)
                bothLines.TrySetResult();
        });
        client.SetEventAdmitted(() => admitted.TrySetResult());
        await client.ConnectAsync();

        var id = client.PeekNextRequestId();
        var pending = client.CallAsync("session.snapshot");
        var server = serverSocket.GetStream();
        var payload = Encoding.UTF8.GetBytes(
            "{\"id\":\"" + id + "\",\"result\":{\"ok\":true}}\n"
            + "{\"event\":\"runtime.event\",\"params\":{\"type\":\"ping\"}}\n");
        await server.WriteAsync(payload);
        await server.FlushAsync();

        var result = await pending.WaitAsync(TestWait.HangGuard);
        await bothLines.Task.WaitAsync(TestWait.HangGuard);

        Assert.Equal(2, Volatile.Read(ref lines));
        Assert.True(result.GetProperty("ok").GetBoolean());
        // The line callback fires before the event is admitted to the queue.
        await admitted.Task.WaitAsync(TestWait.HangGuard);
        var queued = client.DrainPendingEvents().ToList();

        Assert.Single(queued);
        Assert.Equal("runtime.event", queued[0].GetProperty("event").GetString());
    }

    [Fact]
    public async Task ReaderSwitchEndsParkedReadWithoutLosingEvents()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await accept;
        await using var client = ControlPlaneClient.FromConnectedStream(clientSocket.GetStream());
        await client.ConnectAsync();

        using var readerSwitch = new CancellationTokenSource();
        var parked = AttachSession.ReadEventUntilReaderSwitchAsync(
            client,
            readerSwitch.Token,
            CancellationToken.None);
        readerSwitch.Cancel();
        Assert.Null(await parked.WaitAsync(TestWait.HangGuard));

        var server = serverSocket.GetStream();
        var payload = Encoding.UTF8.GetBytes(
            "{\"event\":\"runtime.event\",\"params\":{\"type\":\"ping\"}}\n");
        await server.WriteAsync(payload);
        await server.FlushAsync();

        var ev = await client.ReadEventAsync().WaitAsync(TestWait.HangGuard);
        Assert.Equal("runtime.event", ev.GetProperty("event").GetString());
    }

    [Fact]
    public async Task SessionCancelStillThrowsFromReaderSwitchRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await accept;
        await using var client = ControlPlaneClient.FromConnectedStream(clientSocket.GetStream());
        await client.ConnectAsync();

        using var session = new CancellationTokenSource();
        using var readerSwitch = new CancellationTokenSource();
        var parked = AttachSession.ReadEventUntilReaderSwitchAsync(
            client,
            readerSwitch.Token,
            session.Token);
        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parked.WaitAsync(TestWait.HangGuard));
    }

    [Fact]
    public async Task TransportClosedIsFalseWhileTheReaderRuns()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await accept;
        await using var client = ControlPlaneClient.FromConnectedStream(clientSocket.GetStream());
        await client.ConnectAsync();
        Assert.False(client.IsTransportClosed);
    }

    [Fact]
    public async Task TransportClosedIsTrueAfterThePeerCloses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await accept;
        await using var client = ControlPlaneClient.FromConnectedStream(clientSocket.GetStream());
        await client.ConnectAsync();
        serverSocket.Close();

        // A poll on the client state. The limit only stops a hung run.
        var deadline = DateTime.UtcNow + TestWait.HangGuard;
        while (!client.IsTransportClosed && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(client.IsTransportClosed);
    }
}
