using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Settings;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class OfficialIntegrationSettingsTests
{
    [Fact]
    public void Integrations_apply_queues_installable_targets()
    {
        var model = new SettingsOverlayModel(SettingsPageRegistry.Core());
        model.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        model.OpenAt(SettingsPageRegistry.IntegrationsId);
        Assert.False(model.ShowsApply);
        model.BindIntegrations(
        [
            new OfficialIntegrationStatus
            {
                Target = OfficialIntegrationTarget.Claude,
                Path = "/tmp/hypa-agent-state.sh",
                State = OfficialIntegrationStatusKind.NotInstalled,
                ExpectedVersion = 1,
                Available = true,
            },
            new OfficialIntegrationStatus
            {
                Target = OfficialIntegrationTarget.Pi,
                Path = "/tmp/hypa-agent-state.ts",
                State = OfficialIntegrationStatusKind.Current,
                InstalledVersion = 1,
                ExpectedVersion = 1,
                Available = true,
            },
        ]);
        Assert.True(model.ShowsApply);
        Assert.Contains(model.Items, item => item.Id == "claude" && item.Label.Contains("available", StringComparison.Ordinal));
        Assert.Contains(model.Items, item => item.Id == "pi" && item.Label.Contains("installed", StringComparison.Ordinal));
        Assert.True(model.Apply());
        Assert.NotNull(model.PendingInstallTargets);
        Assert.Equal([OfficialIntegrationTarget.Claude], model.PendingInstallTargets);
        Assert.Equal("installing…", model.IntegrationsHint);
    }
}
