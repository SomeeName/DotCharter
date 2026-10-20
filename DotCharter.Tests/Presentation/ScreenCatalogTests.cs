using System.Collections;
using System.Reflection;
using DotCharter.Presentation.Fakes;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The catalog-derived rules of target-architecture.md § 4: every
///     <see cref="ScreenCatalog" /> contract has exactly one view and exactly one
///     fake, and the fake carries the variant names the contract's shape calls
///     for — <c>Default</c>, <c>Empty</c>, <c>LongText</c> always, <c>Full</c> with
///     a list, <c>Busy</c> / <c>Error</c> with a progress or failure flag.
///     <para>
///         Nothing is pinned: a catalog fake without a name its contract calls for
///         fails. Each rule is a pure function with a probe fact over a known-bad
///         fixture.
///     </para>
/// </summary>
public sealed class ScreenCatalogTests
{

    public static TheoryData<string> Contracts
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var entry in ScreenCatalog.Default.Entries) data.Add(entry.Contract.FullName!);
            return data;
        }
    }

    [Fact]
    public void The_catalog_holds_the_redesigned_screens_and_the_message_prompt()
    {
        var entries = ScreenCatalog.Default.Entries;
        Assert.True(entries.Count >= 10, $"the catalog holds {entries.Count} entries");
        Assert.Contains(entries, e => e.Kind == ScreenKind.Dialog);
        // Nine until the menu went (2026-09-22).
        Assert.Equal(8, ScreenCatalog.Default.FlowOf(ScreenCatalog.PlayMode).Count);
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Every_catalog_contract_has_one_view_and_one_fake(string contractName)
    {
        var entry = EntryOf(contractName);

        Assert.True(entry.Contract is { IsInterface: true, Namespace: not null }
                    && entry.Contract.Namespace.StartsWith("DotCharter.Presentation.Contracts.", StringComparison.Ordinal),
            $"{entry.Contract.FullName} is not a contract under DotCharter.Presentation/Contracts/");
        Assert.True(entry.ViewType.Namespace?.StartsWith("DotCharter.UI.Views.", StringComparison.Ordinal) == true
                    && entry.ViewType.Name.EndsWith("View", StringComparison.Ordinal),
            $"{entry.ViewType.FullName} is not a view under DotCharter.UI/Views/");
        Assert.Single(ScreenCatalog.Default.Entries, e => e.ViewType == entry.ViewType);

        var fakes = FakeScreens.FakesOf(entry.Contract);
        Assert.True(fakes.Count == 1,
            $"{entry.Contract.Name} has {fakes.Count} fakes ({string.Join(", ", fakes.Select(f => f.Name))}) — exactly one " +
            "Fake<Screen> in DotCharter.Presentation.Fakes, with a static Variants typed to the contract");
        Assert.NotEmpty(FakeScreens.VariantsOf(entry.Contract));
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Every_catalog_fake_carries_the_variants_its_contract_calls_for(string contractName)
    {
        var entry = EntryOf(contractName);
        var fake = FakeScreens.FakesOf(entry.Contract).Single();
        var names = FakeScreens.VariantsOf(entry.Contract).Select(v => v.Name).ToList();

        var missing = RequiredVariants(entry.Contract).Except(names, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, $"{fake.Name} lacks {string.Join(", ", missing)} (target-architecture.md § 3)");
    }

    [Fact]
    public void Golden_paths_are_unique_across_the_catalog()
    {
        var paths = ScreenCatalog.Default.Entries
            .SelectMany(e => FakeScreens.VariantsOf(e.Contract).Select(v => e.GoldenPath(v.Name)))
            .ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("play-mode/song-select--no-match.png", paths);
        Assert.Contains("dialogs/message--long-text.png", paths);
    }

    // ── probes: each rule bites on a known-bad input ─────────────────────

    private interface IProbeListScreen
    {
        IReadOnlyList<string> Rows { get; }

        bool IsBusy { get; }

        bool HasError { get; }
    }

    private interface IProbePlainScreen
    {
        string Title { get; }
    }

    [Fact]
    public void Probe_a_contract_with_a_list_and_state_flags_requires_Full_Busy_and_Error()
    {
        Assert.Equal(
            [FakeVariantNames.Default, FakeVariantNames.Empty, FakeVariantNames.LongText,
                FakeVariantNames.Full, FakeVariantNames.Busy, FakeVariantNames.Error],
            RequiredVariants(typeof(IProbeListScreen)));
        Assert.Equal(
            [FakeVariantNames.Default, FakeVariantNames.Empty, FakeVariantNames.LongText],
            RequiredVariants(typeof(IProbePlainScreen)));
    }

    [Fact]
    public void Probe_the_catalog_refuses_a_contract_or_view_registered_twice()
    {
        var entry = ScreenCatalog.Default.Entries[0];
        Assert.Throws<ArgumentException>(() => new ScreenCatalog([entry, entry with { Name = "again" }]));
    }

    [Fact]
    public void Probe_a_contract_without_a_fake_finds_none()
    {
        Assert.Empty(FakeScreens.FakesOf(typeof(IProbePlainScreen)));
        Assert.Empty(FakeScreens.VariantsOf(typeof(IProbePlainScreen)));
    }

    // ── the rule ─────────────────────────────────────────────────────────

    internal static List<string> RequiredVariants(Type contract)
    {
        var required = new List<string> { FakeVariantNames.Default, FakeVariantNames.Empty, FakeVariantNames.LongText };
        var properties = ContractProperties(contract).ToList();
        if (properties.Any(p => p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType)))
            required.Add(FakeVariantNames.Full);
        if (properties.Any(p => p.PropertyType == typeof(bool) && p.Name is "IsBusy" or "IsLoading"))
            required.Add(FakeVariantNames.Busy);
        if (properties.Any(p => p.PropertyType == typeof(bool) && p.Name is "HasError" or "IsError"))
            required.Add(FakeVariantNames.Error);
        return required;
    }

    internal static IEnumerable<PropertyInfo> ContractProperties(Type contract) =>
        new[] { contract }.Concat(contract.GetInterfaces()).SelectMany(t => t.GetProperties());

    private static ScreenEntry EntryOf(string contractName) =>
        ScreenCatalog.Default.Entries.Single(e => e.Contract.FullName == contractName);
}
