using System.Reflection;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Fakes.PlayMode;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     Every fake screen in <c>DotCharter.Presentation.Fakes</c> publishes its
///     states as a static <c>Variants</c> list (target-architecture.md § 3), and
///     that list is complete: every public factory on the fake is in it, under
///     a unique name, and builds a fresh instance of the contract.
///     <para>
///         The required names follow the contract's shape, the rule
///         <see cref="ScreenCatalogTests.RequiredVariants" /> states: <c>Default</c>,
///         <c>Empty</c> and <c>LongText</c> always, <c>Full</c> with a list,
///         <c>Busy</c> / <c>Error</c> with a progress or failure flag. There is no
///         pin: a fake without one of its names fails.
///     </para>
/// </summary>
public sealed class FakeVariantsTests
{
    public static TheoryData<string> FakeScreens
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var t in FakeScreenTypes()) data.Add(t.FullName!);
            return data;
        }
    }

    [Fact]
    public void The_fake_screen_set_is_not_empty() => Assert.True(FakeScreenTypes().Count >= 9, $"found {FakeScreenTypes().Count} fake screens");

    [Theory]
    [MemberData(nameof(FakeScreens))]
    public void Every_fake_screen_publishes_a_complete_variant_list(string fakeName)
    {
        var fake = FakeScreenTypes().Single(t => t.FullName == fakeName);
        var variants = ReadVariants(fake);

        var names = variants.Select(v => v.Name).ToList();
        Assert.True(names.Count == names.Distinct(StringComparer.Ordinal).Count(), $"{fake.Name}: duplicate variant names {string.Join(", ", names)}");

        foreach (var (name, create) in variants)
        {
            var first = create();
            var second = create();
            Assert.IsType(fake, first);
            Assert.NotSame(first, second);
            _ = name;
        }

        var factories = fake.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters().Length == 0 && m.ReturnType == fake)
            .ToList();
        Assert.True(factories.Count == variants.Count,
            $"{fake.Name} has {factories.Count} factories ({string.Join(", ", factories.Select(m => m.Name))}) " +
            $"but {variants.Count} variants ({string.Join(", ", names)}) — every state belongs in Variants");
    }

    [Theory]
    [MemberData(nameof(FakeScreens))]
    public void Every_fake_screen_has_the_variants_its_contract_calls_for(string fakeName)
    {
        var fake = FakeScreenTypes().Single(t => t.FullName == fakeName);
        var names = ReadVariants(fake).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var missing = ScreenCatalogTests.RequiredVariants(ContractOf(fake)).Where(r => !names.Contains(r)).ToArray();

        Assert.True(missing.Length == 0, $"{fake.Name} lacks {string.Join(", ", missing)} (target-architecture.md § 3)");
    }

    [Fact]
    public void Probe_a_fake_without_its_Full_variant_is_caught()
    {
        var required = ScreenCatalogTests.RequiredVariants(ContractOf(typeof(FakeCountdownScreen)));
        var names = ReadVariants(typeof(FakeCountdownScreen)).Select(v => v.Name).Where(n => n != FakeVariantNames.Full);

        Assert.Equal(new[] { FakeVariantNames.Full }, required.Except(names, StringComparer.Ordinal).ToArray());
    }

    private static List<Type> FakeScreenTypes() =>
        typeof(FakeVariantNames).Assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Name.StartsWith("Fake", StringComparison.Ordinal)
                        && t.Name.EndsWith("Screen", StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>The contract a fake's <c>Variants</c> is typed to.</summary>
    private static Type ContractOf(Type fake) =>
        fake.GetProperty("Variants", BindingFlags.Public | BindingFlags.Static)!.PropertyType
            .GetGenericArguments()[0].GetGenericArguments()[0];

    /// <summary>Reads <c>Variants</c> as (name, factory) pairs whatever the contract type argument is.</summary>
    private static List<(string Name, Func<object> Create)> ReadVariants(Type fake)
    {
        var property = fake.GetProperty("Variants", BindingFlags.Public | BindingFlags.Static);
        Assert.True(property != null, $"{fake.Name} has no public static Variants");
        var list = (System.Collections.IEnumerable)property.GetValue(null)!;

        var result = new List<(string, Func<object>)>();
        foreach (var variant in list)
        {
            var type = variant.GetType();
            Assert.True(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FakeVariant<>),
                $"{fake.Name}.Variants holds a {type.Name}, not a FakeVariant<T>");
            Assert.True(type.GetGenericArguments()[0].IsAssignableFrom(fake),
                $"{fake.Name}.Variants is typed to {type.GetGenericArguments()[0].Name}, which {fake.Name} does not implement");
            var name = (string)type.GetProperty(nameof(FakeVariant<>.Name))!.GetValue(variant)!;
            var create = (Delegate)type.GetProperty(nameof(FakeVariant<>.Create))!.GetValue(variant)!;
            result.Add((name, () => create.DynamicInvoke()!));
        }

        return result;
    }
}
