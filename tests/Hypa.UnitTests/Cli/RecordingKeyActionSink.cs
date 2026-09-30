using Hypa.Cli.Attach.Keys;

namespace Hypa.UnitTests.Cli;

internal sealed class RecordingKeyActionSink : IKeyActionSink
{
    public List<KeyActionRequest> Handled { get; } = [];

    public void Handle(KeyActionRequest request) => Handled.Add(request);
}
