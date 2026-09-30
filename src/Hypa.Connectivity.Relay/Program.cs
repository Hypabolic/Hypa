using System.Net;
using System.Text.Json;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;

namespace Hypa.Connectivity.Relay;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!TryParseArgs(
                args,
                out var address,
                out var port,
                out var identity,
                out var pairingStore,
                out var reusableNonce,
                out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        DevicePairingService? pairing = null;
        if (!string.IsNullOrWhiteSpace(pairingStore))
        {
            var keys = new FileDeviceKeyStore(DevicePairingStatePaths.FallbackKeyDirectory(pairingStore));
            pairing = new DevicePairingService(new FileDevicePairingStore(pairingStore), keys);
        }

        RendezvousRelayServer server;
        try
        {
            server = new RendezvousRelayServer(
                identity,
                address,
                port,
                pairing,
                reusableNonce: reusableNonce);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Console.Error.WriteLine("relay listen failed: " + ex.Message);
            return 2;
        }

        pairing?.AttachJoinCloser(server);
        server.Run();
        var listen = new RelayListenDocument
        {
            Ok = true,
            Listen = server.BoundUrl,
            Audience = identity.Audience,
            TenantScope = identity.TenantScope,
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(listen, ConnectivityJsonContext.Default.RelayListenDocument));
        Console.Out.Flush();
        cts.Token.WaitHandle.WaitOne();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    internal static bool TryParseArgs(
        string[] args,
        out IPAddress address,
        out int port,
        out RendezvousRelayIdentity identity,
        out string? pairingStore,
        out string? reusableNonce,
        out string error)
    {
        address = IPAddress.Loopback;
        port = 0;
        identity = RendezvousRelayIdentity.SelfHostedV0;
        pairingStore = null;
        reusableNonce = null;
        error = "usage: hypa-relay --listen <host:port> [--audience hypa.rendezvous] [--tenant local] [--pairing-store dir] [--reusable-nonce <nonce>]";

        string? listen = null;
        string audience = RendezvousAudiences.Rendezvous;
        string tenant = RendezvousTenants.Local;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--listen" && i + 1 < args.Length)
            {
                listen = args[++i];
                continue;
            }

            if (arg is "--audience" && i + 1 < args.Length)
            {
                audience = args[++i];
                continue;
            }

            if (arg is "--tenant" && i + 1 < args.Length)
            {
                tenant = args[++i];
                continue;
            }

            if (arg is "--pairing-store" && i + 1 < args.Length)
            {
                pairingStore = args[++i];
                continue;
            }

            if (arg is "--reusable-nonce")
            {
                if (i + 1 >= args.Length
                    || string.IsNullOrWhiteSpace(args[i + 1])
                    || args[i + 1].StartsWith('-'))
                {
                    error = "--reusable-nonce requires a value";
                    return false;
                }

                reusableNonce = args[++i];
                continue;
            }

            error = "unknown argument: " + arg;
            return false;
        }

        if (string.IsNullOrWhiteSpace(listen))
            return false;

        if (!TryParseListen(listen, out address, out port, out error))
            return false;

        if (string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(tenant))
        {
            error = "audience and tenant are required";
            return false;
        }

        if (!string.Equals(audience, RendezvousAudiences.Rendezvous, StringComparison.Ordinal))
        {
            error = "self-hosted relay does not accept unsupported audience";
            return false;
        }

        if (!string.Equals(tenant, RendezvousTenants.Local, StringComparison.Ordinal))
        {
            error = "self-hosted relay tenant must be local";
            return false;
        }

        identity = new RendezvousRelayIdentity
        {
            Deployment = RendezvousDeployment.SelfHosted,
            Audience = audience,
            TenantScope = tenant,
            ProtocolVersion = ConnectivityProtocolVersion.V0,
        };
        error = "";
        return true;
    }

    private static bool TryParseListen(string listen, out IPAddress address, out int port, out string error)
    {
        address = IPAddress.Loopback;
        port = 0;
        error = "listen must be host:port";
        var value = listen.Trim();
        if (value.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".sock", StringComparison.OrdinalIgnoreCase))
        {
            error = "relay listen must not be a unix socket";
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "ws" or "wss")
        {
            if (uri.Scheme is "https" or "wss")
            {
                error = "self-hosted relay listen is not tls";
                return false;
            }

            if (!IPAddress.TryParse(uri.Host, out var parsedUriHost) || parsedUriHost is null)
            {
                error = "listen host must be an ip address";
                return false;
            }

            address = parsedUriHost;
            port = uri.Port > 0 ? uri.Port : 0;
            return true;
        }

        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return false;
        var host = value[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];
        if (!IPAddress.TryParse(host, out var parsedHost) || parsedHost is null)
        {
            error = "listen host must be an ip address";
            return false;
        }

        address = parsedHost;
        if (!int.TryParse(value[(colon + 1)..], out port) || port < 0)
            return false;
        return true;
    }
}
