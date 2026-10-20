using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     The settings footer's "N unsaved changes" counter must read ZERO on a
///     page nobody has touched.
///
///     <para>
///         It shipped reading three, and the reason a screenshot was needed to
///         catch it is the whole point: a wrong count is indistinguishable from
///         a right one. Two separate causes, both of which these guards pin.
///     </para>
/// </summary>
public sealed class SettingsUnsavedCounterHonestyTests
{
    /// <summary>
    ///     <b>The counter must not count itself, nor a readout.</b> A count or an
    ///     applied factor on the tracked type reads its own output back as an
    ///     edit: under Auto the interface scale is re-derived on every window
    ///     resize, so tracking the READOUT would report unsaved changes nobody
    ///     made. Appearance tracks the CHOICE instead.
    /// </summary>
    [Fact]
    public void TheCounterIsNotItselfASetting()
    {
        var tracked = TrackedOf(typeof(AppearanceSettingsViewModel));

        Assert.DoesNotContain("UnsavedChangeCount", tracked);
        Assert.DoesNotContain("UnsavedSettings", tracked);
        Assert.DoesNotContain(nameof(AppearanceSettingsViewModel.UiScaleReadout), tracked);
        // …and the list still names real settings, so this is not passing
        // because the whole set went empty.
        Assert.Contains(nameof(AppearanceSettingsViewModel.UiScaleChoiceIndex), tracked);
    }

    /// <summary>
    ///     <b>A selection INDEX into an asynchronously-rebuilt list is not a
    ///     setting.</b> What the audio tab persists is the device NAME; the index
    ///     is resolved against a device list enumerated on a worker after the
    ///     page opens, so every one of them starts at −1 and lands on its real
    ///     value milliseconds later — reading as five settings the user changed.
    ///     <para>
    ///         <c>IsComparable</c> already refuses selection state that compares
    ///         by reference for exactly this reason; an int index slipped through
    ///         only because int is comparable.
    ///     </para>
    /// </summary>
    [Fact]
    public void AsyncResolvedDeviceIndicesAreNotSettings()
    {
        var tracked = AudioSettingsViewModel.TrackedSettings;

        Assert.DoesNotContain(tracked, n => n.EndsWith("DeviceIndex", StringComparison.Ordinal));
        foreach (var name in new[]
                 {
                     "SelectedAsioInputIndex", "SelectedAsioOutputIndex",
                     "SelectedWasapiInputIndex", "SelectedWasapiOutputIndex",
                     "SelectedMidiOutputIndex", "SelectedStreamingMirrorIndex"
                 })
        {
            Assert.DoesNotContain(name, tracked);
        }

        // …and the one index on this type that IS a setting stays one: it
        // indexes a fixed map, not an enumerated device list.
        Assert.Contains("SelectedDropTunerEngineIndex", tracked);

        Assert.NotEmpty(tracked);
    }

    /// <summary>
    ///     A SELECTED INDEX is a position in a list, and the thing that survives
    ///     a restart is the identity the list is rebuilt from — so every tracked
    ///     one is suspect and has to be justified here by name.
    ///
    ///     <para>
    ///         Two are legitimate, and both for the same reason — their list is
    ///         FIXED rather than enumerated. <c>SelectedLanguageIndex</c> indexes
    ///         <c>LanguageOptions</c>, built once at startup (built-ins plus any
    ///         community packs) and stable for as long as the page is open;
    ///         <c>SelectedDropTunerEngineIndex</c> indexes the hard-coded
    ///         <c>DropTunerEngineMap</c> and round-trips through a config STRING.
    ///         Neither can move on its own. The audio DEVICE indices are the
    ///         opposite case in every respect: their lists are enumerated on a
    ///         worker AFTER the page opens.
    ///     </para>
    ///
    ///     <para>
    ///         The guard is the allowlist, not the ban: a NEW selected index
    ///         appearing in the tracked set fails here and has to be argued for.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryTrackedSelectedIndexIsOneWeArguedFor()
    {
        // SelectedStartupScreenIndex indexes the two-entry StartupScreenOptions
        // literal and round-trips through the AppConfig.StartupScreen enum, and
        // SelectedStringPaletteIndex indexes the fixed StringColorPresets.All and
        // round-trips through its id, so neither can move on its own.
        string[] justified =
        [
            "SelectedLanguageIndex", "SelectedDropTunerEngineIndex", "SelectedStartupScreenIndex",
            "SelectedStringPaletteIndex"
        ];

        var sources = TrackedTabs.Select(t => (t.Name, Names: TrackedOf(t)));

        foreach (var (typeName, names) in sources)
        {
            var offenders = names
                .Where(n => n.StartsWith("Selected", StringComparison.Ordinal)
                            && n.EndsWith("Index", StringComparison.Ordinal))
                .Where(n => !justified.Contains(n, StringComparer.Ordinal))
                .ToList();

            Assert.True(offenders.Count == 0,
                $"{typeName} counts {string.Join(", ", offenders)} as unsaved settings. " +
                "A selection index moves on its own when its list is rebuilt — either " +
                "exclude it, or add it to this test's `justified` list with the reason " +
                "its list is stable.");
        }

        // The allowlist must not rot: the one entry has to still be tracked, or
        // it is silently permitting nothing.
        Assert.Contains("SelectedLanguageIndex", TrackedOf(typeof(GeneralSettingsViewModel)));
    }

    /// <summary>The tabs that name what they count in a static <c>Tracked</c>.</summary>
    private static readonly Type[] TrackedTabs =
    [
        typeof(GeneralSettingsViewModel), typeof(EditorSettingsViewModel),
        typeof(PlaySettingsViewModel), typeof(FilesSettingsViewModel), typeof(AppearanceSettingsViewModel)
    ];

    private static List<string> TrackedOf(Type type) =>
    [
        .. (string[])type.GetField("Tracked",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!
    ];
}
