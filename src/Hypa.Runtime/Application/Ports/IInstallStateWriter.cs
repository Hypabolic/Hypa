using Hypa.Runtime.Domain;

namespace Hypa.Runtime.Application.Ports;

public interface IInstallStateWriter
{
    void Write(HypaInstallState state);
}
