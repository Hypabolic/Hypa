using Hypa.Runtime.Domain.Parsers;

namespace Hypa.Runtime.Application.Ports;

public interface ITokenFormatter<T>
{
    string Format(T result, FormatMode mode);
}
