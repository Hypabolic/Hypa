using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.System;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
