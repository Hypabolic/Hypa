using System.Text.Json;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Runtime.Application.Ports;

public interface IAgentHarnessAdapter
{
    string Key { get; }
    HarnessCapability Capability { get; }
    AgentHookInput? Parse(JsonElement json);
    AgentHookOutput Format(HookDecision decision, AgentHookInput input);
    bool IsDetected(bool global, string? projectRoot = null);
    InstallPlan GetInstallPlan(bool global, string? projectRoot = null);
}
