// AOT smoke fixture — constructs regex cannot extract (property/field/constructor).
namespace AotSmoke;

public sealed class Sample
{
    public int FieldValue;

    public string Name { get; set; } = "";

    public Sample(string name)
    {
        Name = name;
        FieldValue = 1;
    }

    public void Run()
    {
        System.Console.WriteLine(Name);
    }
}
