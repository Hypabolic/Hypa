using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// AEAD for joined application frames. The relay must not implement this.
/// </summary>
public interface IApplicationFrameCipher
{
    ConnectivityOutcome<StreamFrame> Seal(StreamFrame frame);

    ConnectivityOutcome<StreamFrame> Open(StreamFrame frame);
}
