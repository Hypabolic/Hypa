using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

public static class RemoteKeybindingPolicy
{
    public static bool TryParseMode(string? value, out RemoteKeybindingMode mode)
    {
        mode = RemoteKeybindingMode.Local;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("local", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Equals("server", StringComparison.OrdinalIgnoreCase))
        {
            mode = RemoteKeybindingMode.Server;
            return true;
        }

        return false;
    }
}
