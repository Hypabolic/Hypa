using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class DoctorServiceTests
{
    [Fact]
    public void Run_ReturnsResultsFromAllChecks()
    {
        var check1 = Substitute.For<IDoctorCheck>();
        check1.Run().Returns(new DoctorCheckResult("Label1", "Value1", DoctorStatus.Ok));
        var check2 = Substitute.For<IDoctorCheck>();
        check2.Run().Returns(new DoctorCheckResult("Label2", "Value2", DoctorStatus.Warn, "detail"));

        var service = new DoctorService([check1, check2]);
        var results = service.Run();

        Assert.Equal(2, results.Count);
        Assert.Equal("Label1", results[0].Label);
        Assert.Equal(DoctorStatus.Ok, results[0].Status);
        Assert.Equal("Label2", results[1].Label);
        Assert.Equal(DoctorStatus.Warn, results[1].Status);
        Assert.Equal("detail", results[1].Detail);
    }

    [Fact]
    public void Run_NoChecks_ReturnsEmptyList()
    {
        var service = new DoctorService([]);
        Assert.Empty(service.Run());
    }

    [Fact]
    public void Run_CallsEachCheckExactlyOnce()
    {
        var check = Substitute.For<IDoctorCheck>();
        check.Run().Returns(new DoctorCheckResult("L", "V", DoctorStatus.Ok));

        new DoctorService([check]).Run();

        check.Received(1).Run();
    }
}
