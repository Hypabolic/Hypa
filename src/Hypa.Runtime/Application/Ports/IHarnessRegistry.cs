namespace Hypa.Runtime.Application.Ports;

public interface IHarnessRegistry
{
    IReadOnlyList<IAgentHarnessAdapter> All { get; }
    IAgentHarnessAdapter? Find(string key);
}
