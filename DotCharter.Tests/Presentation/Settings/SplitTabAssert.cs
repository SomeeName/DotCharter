using System.ComponentModel;
using System.Text.RegularExpressions;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>Assertions every tab split out of General shares.</summary>
internal static class SplitTabAssert
{
    /// <summary>
    ///     Every setting the contract lets the view WRITE is counted by the footer,
    ///     once, and putting it back counts nothing. Derived by reflection so a new
    ///     row added without its tracker entry fails here instead of reading
    ///     "Everything saved".
    /// </summary>
    public static void EverySettableMember_CountsOnce<TContract>(Func<TContract> create, int atLeast, params string[] notSettings)
        where TContract : class
    {
        var settable = typeof(TContract).GetProperties()
            .Where(p => p.CanWrite && !notSettings.Contains(p.Name))
            .ToList();
        Assert.True(settable.Count >= atLeast, $"only {settable.Count} settable members found; the walk went vacuous");

        foreach (var property in settable)
        {
            var screen = create();
            var source = (ISettingsChangeSource)screen;
            var original = property.GetValue(screen);
            property.SetValue(screen, Nudged(original, property.PropertyType));
            Assert.True(source.UnsavedSettings.Count == 1,
                $"{property.Name}: {source.UnsavedSettings.Count} unsaved change(s) after one edit");

            property.SetValue(screen, original);
            Assert.True(source.UnsavedSettings.Count == 0, $"{property.Name}: putting it back still counts");
        }
    }

    /// <summary>Every edit raises <c>UnsavedSettings</c>, which is what the shell's footer listens for.</summary>
    public static void AnEditRaisesUnsavedSettings(INotifyPropertyChanged screen, Action edit)
    {
        var raised = new List<string?>();
        screen.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        edit();
        Assert.Contains(nameof(ISettingsChangeSource.UnsavedSettings), raised);
    }

    private static readonly Regex RawKey = new(@"^[A-Za-z]+_[A-Za-z0-9_]+$");

    /// <summary>The catalogue is read through the real strings, so a missing key surfaces here as itself.</summary>
    public static void TheSearchCatalogue_HasNoRawResourceKeys(ISettingsTab tab)
    {
        Assert.NotEmpty(tab.SearchGroups);
        foreach (var group in tab.SearchGroups)
        {
            Assert.DoesNotMatch(RawKey, group.Title);
            Assert.DoesNotMatch(RawKey, group.Subtitle ?? "");
            Assert.NotEmpty(group.Rows);
            foreach (var row in group.Rows)
            {
                Assert.DoesNotMatch(RawKey, row.Label);
                Assert.DoesNotMatch(RawKey, row.Description ?? "");
                Assert.StartsWith(tab.Key + ".", row.Key, StringComparison.Ordinal);
            }
        }
    }

    private static object? Nudged(object? value, Type type) => value switch
    {
        bool b => !b,
        int i => i + 1,
        double d => d + 1,
        string s => s + "x",
        _ => throw new NotSupportedException($"no nudge for {type.Name}")
    };
}
