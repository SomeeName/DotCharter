using System.Text.RegularExpressions;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>A setting the footer counts must have a ROW, and that row must bind
///     the property that persists.</b>
///     <para>
///         <c>SettingsRoundTripTests</c> proves a tracked setting is loaded from
///         and saved to <c>AppConfig</c>. Nothing proved it was REACHABLE. Three
///         of them were not, all on the Stem Models tab:
///     </para>
///     <list type="bullet">
///         <item><c>MaxConcurrentSeparations</c> and <c>EnableCpuFallback</c>
///         had no row at all — persisted, counted by the footer, pushed at the
///         JUCE orchestrator, and settable only by hand-editing
///         <c>appconfig.json</c>. (Both were later deleted with that
///         orchestrator, which never separated anything, #662.)</item>
///         <item>The "Execution provider" row bound a SECOND, near-identically
///         named property (<c>ExecutionProvider</c>, a pass-through to
///         <c>StemModelManager</c>'s in-memory field) instead of
///         <c>PreferredExecutionProvider</c>. The control looked and felt
///         live — it even changed the managed ONNX backend's preference for the
///         session — while writing no config field, never reaching the
///         orchestrator's <c>Device</c>, and being overwritten from the
///         persisted value on the next load.</item>
///     </list>
///     <para>
///         This is the <c>CoopRigApplier</c> shape one level in: the state is
///         real, the save path is real, the reachability is not. So the lint
///         goes the other way from the round-trip one — from the tracked
///         property to the MARKUP.
///     </para>
/// </summary>
public sealed class SettingsTabReachabilityTests
{
    /// <summary>The settings surfaces whose rows all live under
    /// <c>Views/Settings/</c>. Play Mode's gameplay modal is a second door onto
    /// some of the same fields and is deliberately not counted as one.</summary>
    private static readonly Type[] Surfaces =
    [
        typeof(AppearanceSettingsViewModel),
        typeof(AudioSettingsViewModel),
        typeof(GeneralSettingsViewModel),
        typeof(EditorSettingsViewModel),
        typeof(PlaySettingsViewModel),
        typeof(FilesSettingsViewModel)
    ];

    /// <summary>
    ///     Tracked settings that legitimately have no binding of their own,
    ///     each with the route. Only ever shrinks: a new entry is a claim about
    ///     a setting the user cannot reach.
    /// </summary>
    private static readonly Dictionary<string, string> NotBoundDirectly = new(StringComparer.Ordinal)
    {
        // The Audio tab's guitar-jack ComboBox binds SelectedInputJackIndex and
        // mirrors this field BOTH ways, so Save writes the field it always
        // wrote (docs/agents/ui/settings.md, the input-jack picker). The row IS
        // the box; the setting simply is not the thing bound.
        ["AudioSettingsViewModel.AsioInputChannelLeft"] =
            "mirrored both ways by the SelectedInputJackIndex ComboBox",
        // The output-pair ComboBox binds SelectedOutputPairIndex and mirrors
        // both output channels the same way (the jack picker's shape).
        ["AudioSettingsViewModel.AsioOutputChannelLeft"] =
            "mirrored both ways by the SelectedOutputPairIndex ComboBox",
        ["AudioSettingsViewModel.AsioOutputChannelRight"] =
            "mirrored both ways by the SelectedOutputPairIndex ComboBox",
        // Reachable, but not from Settings: the mixer inspector's MIDI-offset
        // slider binds Transport.MidiOffsetMs, whose setter persists this exact
        // field (TransportViewModel.PersistMidiOffset) and pushes it at the
        // engine. One field, two doors, and the other door is the live one.
        ["AudioSettingsViewModel.MidiDelayMs"] =
            "the mixer inspector's Transport.MidiOffsetMs slider persists this field"
    };

    [Fact]
    public void EveryTrackedSetting_HasARowOnASettingsTab()
    {
        var bound = BoundPathsAcrossTheSettingsMarkup();
        var unreachable = new List<string>();

        foreach (var surface in Surfaces)
        {
            foreach (var name in TrackedNamesOf(surface))
            {
                var key = surface.Name + "." + name;
                if (NotBoundDirectly.ContainsKey(key)) continue;
                if (!bound.Contains(name)) unreachable.Add(key);
            }
        }

        Assert.True(unreachable.Count == 0,
            "These are persisted, counted by the settings footer's \"N unsaved changes\", and bound by " +
            "NO row under Views/Settings — so the only way to set them is to hand-edit appconfig.json:\n  "
            + string.Join("\n  ", unreachable));
    }

    // ---- helpers --------------------------------------------------------------

    // Audio counts through its own explicit list; every other tab names its
    // settings in a static `Tracked`.
    private static IEnumerable<string> TrackedNamesOf(Type surface)
    {
        if (surface == typeof(AudioSettingsViewModel)) return AudioSettingsViewModel.TrackedSettings;
        var tracked = surface.GetField("Tracked",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.True(tracked is not null, $"{surface.Name} declares no static Tracked list");
        return (string[])tracked!.GetValue(null)!;
    }

    private static HashSet<string> BoundPathsAcrossTheSettingsMarkup()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings"));
        var files = UiSourceTree.GetFiles(dir, "*.axaml", SearchOption.AllDirectories);
        Assert.True(files.Length >= 8, $"only {files.Length} settings markup files found — the walk moved.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (Match match in Regex.Matches(UiSourceTree.ReadAllText(file),
                         @"\{Binding\s+(?:Path=)?([A-Za-z_][A-Za-z0-9_.]*)"))
            {
                var path = match.Groups[1].Value;
                paths.Add(path);
                paths.Add(path.Split('.')[^1]);
            }
        }

        return paths;
    }
}
