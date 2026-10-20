using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// AEAD for joined application frames. The relay must not implement this.
/// </summary>
public interface IApplicationFrameCipher
{
    ConnectivityOutcome<StreamFrame> Seal(StreamFrame frame);

    /// <summary>The payload length of the frame after <see cref="SealTo"/>.</summary>
    int SealedLength(StreamFrame frame);

    /// <summary>
    /// Writes the sealed payload into <paramref name="destination"/>, which is
    /// <see cref="SealedLength"/> bytes. A kind that the cipher does not
    /// protect is copied as it is.
    /// </summary>
    ConnectivityOutcome SealTo(StreamFrame frame, Span<byte> destination);

    ConnectivityOutcome<StreamFrame> Open(StreamFrame frame);
}
