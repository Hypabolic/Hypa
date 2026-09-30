using System;

namespace Checklist;

public interface IWorker
{
    void Run();
}

public class BaseWorker
{
    public virtual void Run() { }
}

public partial class Outer
{
    public int Field;
    public string Prop { get; set; } = "";
    public string ExprProp => "x";
    public event EventHandler? Changed;
    public event EventHandler? Changed2 { add { } remove { } }

    public Outer(int x)
    {
        Field = x;
    }

    public void Method<T>(T value) where T : class
    {
        Helper();
    }

    private void Helper() { }

    public class Nested
    {
        public int NestedField;
    }
}

public partial class Outer
{
    public void Other() { }
}

public record Person(string Name, int Age);

public record struct Point(int X, int Y);

public struct Vec
{
    public int X;
}

public enum Color
{
    Red,
    Green
}

public class Worker : BaseWorker, IWorker
{
    public Worker() : base() { }

    public override void Run()
    {
        Helper();
        Console.WriteLine("ok");
    }

    private void Helper() { }
}

public class Primary(int seed)
{
    public int Seed { get; } = seed;
}
