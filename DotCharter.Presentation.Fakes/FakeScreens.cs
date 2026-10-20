using System.Collections;
using System.Reflection;

namespace DotCharter.Presentation.Fakes;

/// <summary>One variant of a fake, typed loosely so a catalog can enumerate every contract the same way.</summary>
public sealed record FakeScreenVariant(string Name, Func<object> Create);

/// <summary>Finds the fake of a contract and reads its <c>Variants</c>, for the gallery and the tests.</summary>
public static class FakeScreens
{
    /// <summary>Every fake whose static <c>Variants</c> is typed to exactly <paramref name="contract" />.</summary>
    public static IReadOnlyList<Type> FakesOf(Type contract)
    {
        var variantsType = typeof(IReadOnlyList<>).MakeGenericType(typeof(FakeVariant<>).MakeGenericType(contract));
        return typeof(FakeScreens).Assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && contract.IsAssignableFrom(t))
            .Where(t => t.GetProperty("Variants", BindingFlags.Public | BindingFlags.Static)?.PropertyType == variantsType)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The variants of the ONE fake of <paramref name="contract" />; empty when there is none or several.</summary>
    public static IReadOnlyList<FakeScreenVariant> VariantsOf(Type contract)
    {
        var fakes = FakesOf(contract);
        if (fakes.Count != 1) return [];

        var list = (IEnumerable)fakes[0].GetProperty("Variants", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var variants = new List<FakeScreenVariant>();
        foreach (var variant in list)
        {
            var type = variant.GetType();
            var name = (string)type.GetProperty(nameof(FakeVariant<>.Name))!.GetValue(variant)!;
            var create = (Func<object>)type.GetProperty(nameof(FakeVariant<>.Create))!.GetValue(variant)!;
            variants.Add(new FakeScreenVariant(name, create));
        }

        return variants;
    }
}
