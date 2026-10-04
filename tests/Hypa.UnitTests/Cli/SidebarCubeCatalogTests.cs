using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SidebarCubeCatalogTests
{
    [Fact]
    public async Task Empty_directory_lists_the_local_machine()
    {
        var dir = CreateStore();
        try
        {
            var loaded = await SidebarCubeCatalog.LoadFromDirectoryAsync(dir, configuredIdentity: null, userName: "local:test");

            Assert.Equal(SidebarCubeCatalogState.Ready, loaded.State);
            var local = Assert.Single(loaded.Items);
            Assert.Equal(SidebarCubeCatalog.LocalCubeId, local.Id);
            Assert.Equal(SidebarCubeKind.Local, local.Kind);
            Assert.Equal(SidebarCubeReachability.Local, local.Reachability);
            Assert.True(local.ConnectEnabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Added_remote_cube_keeps_the_local_machine_listed_first()
    {
        var dir = CreateStore();
        try
        {
            var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
            var added = await catalog.AddAsync(new QuicPlacementAddRequest
            {
                Owner = Owner(),
                Label = "Lab",
                Target = "127.0.0.1:7443",
                Session = "default",
                EnrolledDeviceId = "dev_lab",
            });
            Assert.True(added.Ok, added.Detail);

            var directory = new PlacementDirectoryService(new FilePlacementDirectoryStore(dir));
            var loaded = await SidebarCubeCatalog.LoadAsync(directory, Owner());

            Assert.Collection(
                loaded.Items,
                item => Assert.Equal(SidebarCubeKind.Local, item.Kind),
                item => Assert.Equal("Lab", item.Name));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Registered_local_row_is_not_duplicated()
    {
        var dir = CreateStore();
        try
        {
            var directory = new PlacementDirectoryService(new FilePlacementDirectoryStore(dir));
            var registered = await directory.RegisterAsync(new PlacementRegistration
            {
                Owner = Owner(),
                DisplayName = "Workstation",
                Kind = PlacementDirectoryKind.Local,
                MuxIdentity = MuxIdentity.Parse("mux_default"),
            });
            Assert.True(registered.Ok, registered.Detail);

            var loaded = await SidebarCubeCatalog.LoadAsync(directory, Owner());

            var local = Assert.Single(loaded.Items);
            Assert.Equal(registered.Value!.Id.Value, local.Id);
            Assert.Equal("Workstation", local.Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Blank_machine_name_still_yields_a_named_local_row()
    {
        var items = SidebarCubeCatalog.WithLocal([], machineName: " ");

        var local = Assert.Single(items);
        Assert.Equal(SidebarCubeKind.Local, local.Kind);
        Assert.False(string.IsNullOrWhiteSpace(local.Name));
    }

    private static DirectoryIdentity Owner() => DirectoryIdentity.Parse("local:test");

    private static string CreateStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-cubes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
