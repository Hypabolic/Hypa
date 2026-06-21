using Hypa.Cli.DI;
using Hypa.Infrastructure.DI;
using Hypa.Runtime.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class CapabilityCommandRegistrationTests
{
    [Theory]
    [InlineData(typeof(FileReadService))]
    [InlineData(typeof(CompressService))]
    [InlineData(typeof(SearchService))]
    public void AddInfrastructureAndCli_RegisterCapabilityServicesOnce(Type serviceType)
    {
        var services = new ServiceCollection();

        services.AddInfrastructure();
        services.AddCli();

        Assert.Single(services, d => d.ServiceType == serviceType);
    }
}
