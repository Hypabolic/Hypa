using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpServerImportService
{
    Task<Result<McpImportReport, Error>> ImportAsync(McpImportRequest request, CancellationToken ct);
}
