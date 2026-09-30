using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal Task<JsonElement> HandleIntegrationListAsync(EmptyParams unused, CancellationToken ct)
    {
        _ = unused;
        _ = ct;
        var statuses = _integrations.ListStatuses();
        var items = new IntegrationInfoDto[statuses.Count];
        for (var i = 0; i < statuses.Count; i++)
            items[i] = ToInfo(statuses[i]);
        return Task.FromResult(OkTyped(
            new IntegrationListResult { Integrations = items },
            ProtocolJsonContext.Default.IntegrationListResult));
    }

    internal Task<JsonElement> HandleIntegrationInstallAsync(
        IntegrationTargetParams p, CancellationToken ct)
    {
        _ = ct;
        if (p.Detected)
            return Task.FromResult(DetectedResult(_integrations.InstallDetected(p.Plan)));

        var target = RequireIntegrationTarget(p.Target);
        var result = _integrations.Install(target, p.Plan);
        if (!result.IsOk)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InternalError,
                result.Error.Message);
        }

        return Task.FromResult(OkTyped(ToAction(result.Value), ProtocolJsonContext.Default.IntegrationActionResult));
    }

    internal Task<JsonElement> HandleIntegrationUninstallAsync(
        IntegrationTargetParams p, CancellationToken ct)
    {
        _ = ct;
        if (p.Plan)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "plan applies to install");
        }

        if (p.Detected)
            return Task.FromResult(DetectedResult(_integrations.UninstallDetected()));

        var target = RequireIntegrationTarget(p.Target);
        var result = _integrations.Uninstall(target);
        if (!result.IsOk)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InternalError,
                result.Error.Message);
        }

        return Task.FromResult(OkTyped(ToAction(result.Value), ProtocolJsonContext.Default.IntegrationActionResult));
    }

    private sealed record PendingOfficialAgentResume(
        PaneId PaneId,
        OfficialAgentResumePlan Plan);

    /// <summary>
    /// during restore. Do not type the command until the control socket is up.
    /// </summary>
    private void QueueOfficialAgentResume(PaneState pane)
    {
        var plan = OfficialAgentResumePlanner.TryPlan(
            pane.AgentSession,
            AttachConfig.Session.ResumeAgentsOnRestore);
        if (plan is null)
            return;

        var duplicate = false;
        lock (_gate)
        {
            if (!_resumedAgentSessions.Add(plan.DedupeKey))
                duplicate = true;
            else
                _pendingOfficialAgentResumes.Add(new PendingOfficialAgentResume(pane.Id, plan));
        }

        if (duplicate)
            _state.UpdatePane(pane.Id, p => p with { AgentSession = null });
    }

    /// <summary>
    /// type the stored resume command after the control socket accepts connections.
    /// </summary>
    public async Task FlushOfficialAgentResumesAsync(CancellationToken ct)
    {
        if (IsShuttingDown)
            return;

        List<PendingOfficialAgentResume> pending;
        lock (_gate)
        {
            pending = [.. _pendingOfficialAgentResumes];
            _pendingOfficialAgentResumes.Clear();
        }

        foreach (var item in pending)
        {
            if (IsShuttingDown)
                return;

            IPaneRuntime? runtime;
            lock (_gate)
                _runtimes.TryGetValue(item.PaneId.Value, out runtime);
            if (runtime is null)
                continue;

            await TypeOfficialAgentResumeAsync(runtime, item.PaneId, item.Plan, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task TypeOfficialAgentResumeAsync(
        IPaneRuntime runtime,
        PaneId paneId,
        OfficialAgentResumePlan plan,
        CancellationToken ct)
    {
        if (IsShuttingDown)
            return;

        try
        {
            await runtime.WriteTextAsync(OfficialAgentResumePlanner.ToShellCommand(plan) + "\r", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _resumedAgentSessions.Remove(plan.DedupeKey);
            _logger.LogWarning(
                ex,
                "failed to type official agent resume pane_id={PaneId}",
                paneId.Value);
        }
    }

    private static OfficialIntegrationTarget RequireIntegrationTarget(string? raw)
    {
        if (!OfficialIntegrationTargets.TryParse(raw, out var target))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "unknown integration target: " + (raw ?? ""));
        }

        return target;
    }

    private IntegrationInfoDto ToInfo(OfficialIntegrationStatus status) =>
        new()
        {
            Target = status.Target.WireName(),
            Label = status.Target.Label(),
            Command = status.Target.CommandName(),
            Available = status.Available,
            State = status.State.WireName(),
            InstalledVersion = status.InstalledVersion,
            ExpectedVersion = status.ExpectedVersion,
            Path = status.Path,
            SkillState = status.SkillState.WireName(),
            Consent = _integrations.ConsentLines(status.Target),
        };

    private static IntegrationActionResult ToAction(OfficialIntegrationActionResult result) =>
        new()
        {
            Target = result.Target.WireName(),
            Messages = result.Messages,
        };

    private static JsonElement DetectedResult(IReadOnlyList<OfficialIntegrationActionResult> results)
    {
        var lines = new string[results.Count];
        var outcomes = new IntegrationDetectedOutcome[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            lines[i] = OfficialIntegrationService.DetectedLine(result);
            outcomes[i] = new IntegrationDetectedOutcome
            {
                Target = result.Target.WireName(),
                Ok = result.Succeeded,
                Messages = result.Messages,
            };
        }

        return OkTyped(
            new IntegrationActionResult
            {
                Target = "detected",
                Messages = lines,
                Outcomes = outcomes,
            },
            ProtocolJsonContext.Default.IntegrationActionResult);
    }
}
