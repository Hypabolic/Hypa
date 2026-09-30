using System.Reflection;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Domain;
using Xunit;

namespace Hypa.UnitTests.Connectivity;

public sealed class ProductJoinDefaultsTests
{
    private const string LabNonce = "nonce_homelab";
    private const string LabDevice = "dev_lab01";

    [Fact]
    public void Product_assemblies_do_not_embed_lab_join_defaults()
    {
        var connectivity = typeof(JoinNonce).Assembly;
        var cli = typeof(EnvironmentCubesConnectJoinMaterialSource).Assembly;
        Assert.Equal("Hypa.Connectivity", connectivity.GetName().Name);
        Assert.Equal("hypa", cli.GetName().Name);
        Assert.StartsWith("Hypa.Cli", typeof(EnvironmentCubesConnectJoinMaterialSource).Namespace);

        AssertAssembly(connectivity);
        AssertAssembly(cli);
    }

    private static void AssertAssembly(Assembly assembly)
    {
        foreach (var type in TypesOf(assembly))
        {
            Assert.NotEqual("HomelabRendezvous", type.Name);
            const BindingFlags flags =
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly;
            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType != typeof(string))
                    continue;

                var value = ReadStaticString(field);
                Assert.NotEqual(LabNonce, value);
                Assert.NotEqual(LabDevice, value);
            }
        }
    }

    private static Type[] TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Assert.DoesNotContain(ex.Types, type => type?.Name == "HomelabRendezvous");
            return ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
    }

    private static string? ReadStaticString(FieldInfo field)
    {
        if (field.IsLiteral)
            return field.GetRawConstantValue() as string;

        try
        {
            return field.GetValue(null) as string;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}
