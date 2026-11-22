using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A Library column rebuilds its filter rows on every change of a facet's option list,
///     which during <see cref="BrowserFacet.ResetOptions" /> is once per ADDED option. Each
///     rebuild wrapped every option in a fresh row subscribed to that option and never
///     unsubscribed, so a reset of N options left option k holding N - k + 1 rows: quadratic
///     retention for as long as the options live. Each option must hold exactly one row.
/// </summary>
public sealed class LibraryColumnOptionRetentionTests
{
    private static readonly FieldInfo PropertyChangedField =
        typeof(ObservableObject).GetField(nameof(INotifyPropertyChanged.PropertyChanged),
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ObservableObject.PropertyChanged has no backing field any more.");

    [Fact]
    public void AResetLeavesEachOptionFollowedByOneRow()
    {
        var facet = new BrowserFacet("tuning", "Tuning");
        var column = new LibraryColumn(FakeLocalization.KeysOnly(), () => { }, "Tuning", null, () => "", [(facet, null)], null, null);

        facet.ResetOptions(Enumerable.Range(0, 20).Select(i => ($"v{i}", $"Option {i}")));

        Assert.Equal(20, column.FilterOptions.Count);
        foreach (var option in facet.Options)
            Assert.Equal(1, Subscribers(option));
    }

    [Fact]
    public void ARepeatedResetDetachesTheRowsOfDroppedOptions()
    {
        var facet = new BrowserFacet("tuning", "Tuning");
        var column = new LibraryColumn(FakeLocalization.KeysOnly(), () => { }, "Tuning", null, () => "", [(facet, null)], null, null);

        facet.ResetOptions([("a", "A"), ("b", "B")]);
        var dropped = facet.Options.ToList();
        facet.ResetOptions([("c", "C")]);

        Assert.Single(column.FilterOptions);
        foreach (var option in dropped)
            Assert.Equal(0, Subscribers(option));
        Assert.Equal(1, Subscribers(facet.Options[0]));
    }

    /// <summary>The rows still follow their option: a tick on the option reaches the row.</summary>
    [Fact]
    public void AReusedRowStillFollowsItsOption()
    {
        var facet = new BrowserFacet("tuning", "Tuning");
        var column = new LibraryColumn(FakeLocalization.KeysOnly(), () => { }, "Tuning", null, () => "", [(facet, null)], null, null);
        facet.ResetOptions([("a", "A"), ("b", "B")]);

        var row = column.FilterOptions[0];
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        facet.Options[0].IsChecked = true;

        Assert.Contains(nameof(ILibraryFilterOption.IsSelected), raised);
        Assert.True(row.IsSelected);
    }

    private static int Subscribers(ObservableObject source) =>
        (PropertyChangedField.GetValue(source) as Delegate)?.GetInvocationList().Length ?? 0;
}
