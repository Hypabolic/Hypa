using System;

namespace Corpus.Graph;

// Checklist constructs for fidelity: property, field, constructor, event,
// record, struct, enum, nested type, primary ctor, contains edges via ParentId.
public interface WorkerContract
{
    void Execute();
}

public class INamedWidget
{
    public string Label { get; set; } = "";
}

public partial class Service
{
    public int Counter;
    public string Name { get; set; } = "";
    public event EventHandler? Updated;

    public Service(string name)
    {
        Name = name;
    }

    public class Nested
    {
        public int NestedField;
    }
}

public partial class Service
{
    public void Tick()
    {
        Counter++;
    }
}

public record Measurement(string Metric, double Value);

public struct Point2
{
    public int X;
    public int Y;
}

public enum Status
{
    Ready,
    Busy
}

// Non-I-prefixed interface + I-prefixed class: relationship kind must use symbol kind.
public class Runner : WorkerContract
{
    public void Execute()
    {
    }
}

public class WidgetHost : INamedWidget
{
}

// Qualified base types
public class QualifiedRunner : Corpus.Graph.WorkerContract
{
    public void Execute()
    {
    }
}

public class PrimaryHost(int seed)
{
    public int Seed { get; } = seed;
}
