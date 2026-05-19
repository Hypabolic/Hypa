using System.Text.Json;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks;

public interface IHookIo
{
    Task<JsonElement?> ReadStdinAsync(CancellationToken ct = default);
    void WriteOutput(AgentHookOutput output);
}
