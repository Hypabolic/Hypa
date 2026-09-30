using System.Net.NetworkInformation;
using Hypa.Connectivity.Application;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Unicast addresses from interfaces that are up.</summary>
public sealed class NetworkInterfaceHostReachCatalog : IHostReachCatalog
{
    public IReadOnlyList<string> ListInterfaceAddresses()
    {
        try
        {
            var list = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                IPInterfaceProperties properties;
                try
                {
                    properties = nic.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                    list.Add(unicast.Address.ToString());
            }

            return list;
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }
    }
}
