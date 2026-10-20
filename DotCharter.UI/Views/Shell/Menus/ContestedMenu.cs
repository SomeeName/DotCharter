using Avalonia;
using Avalonia.Controls;
using DotCharter.Services.Infrastructure.Features;
using DotCharter.Services.Plugins;

namespace DotCharter.UI.Views.Shell.Menus;

/// <summary>
/// Marks a menu row as belonging to a contested feature:
///     <c>menus:ContestedMenu.Feature="youtube-import"</c>. A menu file that
///     carries a marked row calls <see cref="Prune" /> after
///     <c>InitializeComponent</c>; with the public-build switch off the row is
///     REMOVED from its parent (absent, not greyed). The ids are
///     <see cref="ContestedFeatures.All" />; <c>ContestedFeaturesDocSyncTests</c>
///     checks that every registered menu key is marked and every menu file with a
///     mark prunes. A row that READS game packages or the game install is marked
///     with <see cref="PackageFeatures.GamePackagesId" /> / <see cref="PackageFeatures.GameInstallId" />
/// and is removed when <see cref="IPackageFeatures" /> says the build cannot.
/// </summary>
public static class ContestedMenu
{
    public static readonly AttachedProperty<string?> FeatureProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, string?>("Feature", typeof(ContestedMenu));

    public static string? GetFeature(MenuItem item) => item.GetValue(FeatureProperty);

    public static void SetFeature(MenuItem item, string? value) => item.SetValue(FeatureProperty, value);

    /// <summary>
    ///     Removes every marked row under <paramref name="root" /> whose feature is off: a
    ///     contested one when the switch is off, a package one when <paramref name="packages" />
    ///     does not allow it (null = not known yet, kept). Returns how many went.
    /// </summary>
    public static int Prune(ItemsControl root, bool enabled = ContestedFeatures.Enabled, IPackageFeatures? packages = null)
    {
        var removed = 0;
        foreach (var child in root.Items.OfType<MenuItem>().ToList())
        {
            if (GetFeature(child) is { } id && !IsOn(id, enabled, packages))
            {
                root.Items.Remove(child);
                removed++;
                continue;
            }

            removed += Prune(child, enabled, packages);
        }

        return removed;
    }

    /// <summary>Whether a row marked <paramref name="id" /> stays.</summary>
    public static bool IsOn(string id, bool contestedEnabled, IPackageFeatures? packages) =>
        ContestedFeatures.All.Any(f => f.Id == id)
            ? contestedEnabled
            : packages is null || PackageFeatures.Allows(packages, id);
}
