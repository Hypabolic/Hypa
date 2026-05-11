using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Services;

public sealed class CodeDiagnosticsService(ICodeIndexRepository repository, CodeStructureProviderRegistry providers)
{
    public Task<IReadOnlyList<CodeDiagnostic>> QueryDiagnosticsAsync(CancellationToken ct) =>
        repository.QueryDiagnosticsAsync(ct);

    public async Task<IReadOnlyList<CodeProviderHealth>> DoctorAsync(CancellationToken ct)
    {
        var health = providers.Providers.Select(p => p.CheckHealth()).ToArray();
        await repository.SaveProviderHealthAsync(health, ct);
        return health;
    }
}
