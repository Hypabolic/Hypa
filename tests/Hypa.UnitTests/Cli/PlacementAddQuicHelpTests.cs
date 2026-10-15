using Hypa.Cli.Commands.Work;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class PlacementAddQuicHelpTests
{
    [Fact]
    public void Device_option_describes_the_join_rule()
    {
        var work = new WorkCommand().Build();
        var placements = Assert.Single(work.Subcommands, c => c.Name == "placements");
        var addQuic = Assert.Single(placements.Subcommands, c => c.Name == "add-quic");
        var device = Assert.Single(addQuic.Options, o => o.Name == "--device");

        var text = device.Description ?? string.Empty;

        Assert.Contains("tries the paired devices", text, StringComparison.Ordinal);
        Assert.Contains("trusted first, then pending", text, StringComparison.Ordinal);
        Assert.Contains("saves the first one the host accepts", text, StringComparison.Ordinal);
        Assert.Contains("refresh keeps an enrolled device", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the one trusted paired device", text, StringComparison.Ordinal);
    }
}
