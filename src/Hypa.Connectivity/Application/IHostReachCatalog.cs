namespace Hypa.Connectivity.Application;

/// <summary>Interface addresses of this host. Selection rules are not this port.</summary>
public interface IHostReachCatalog
{
    IReadOnlyList<string> ListInterfaceAddresses();
}
