using System.Reflection;

namespace DotCharter.Tests.UI;

/// <summary>
///     The event dialog REPLACES the marker it edits: it removes the existing
///     <c>EventPoint</c> and adds a freshly built one. That makes every field it
///     forgets to carry a silent deletion — and until 0.39.2 it forgot
///     <c>Events</c>, so editing the section of an imported marker dropped every
///     crowd cue and metronome tick on it. Nothing failed, nothing warned; the
///     codes were simply gone from the next export.
///     <para>
///         A source lint, the same shape as <c>KeybindDefaultsTests</c>'s
///         reachability checks: the view model needs the whole editor graph to
///         construct, and the property set is what matters, not the wiring.
///     </para>
/// </summary>
public sealed class EventDialogFidelityTests
{
    /// <summary>
    ///     Fields the dialog deliberately does NOT carry, each with the reason.
    ///     Empty today — add to it only with a comment saying why the field is
    ///     safe to drop when a marker is rebuilt.
    /// </summary>
    private static readonly HashSet<string> DeliberatelyNotCarried = new(StringComparer.Ordinal);

    [Fact]
    public void TheRebuiltEventPointCarriesEveryFieldOfTheOneItReplaces()
    {
        // Scoped to the OBJECT INITIALIZER and with comments stripped: the first
        // version of this guard searched the whole file and was satisfied by a
        // commented-out `// Events = events`, which is precisely the state it is
        // supposed to catch.
        var initializer = StripComments(
            Between(UiSourceTree.ReadAllText(RepoPath("DotCharter.Services/Editing/Events/EventMarkerEdit.cs")),
                "new EventPoint", "}"));

        var missing = typeof(EventPoint)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .Where(name => !DeliberatelyNotCarried.Contains(name))
            .Where(name => !initializer.Contains(name + " =", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "EventMarkerEdit rebuilds the EventPoint the event dialog edits, so a field it never assigns is DELETED " +
            "the moment the user touches that marker. Missing: " + string.Join(", ", missing));
    }

    private static string StripComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment < 0 ? line : line[..comment];
        }));

    /// <summary>
    ///     A marker carrying only event codes — a crowd cue with no section and
    ///     no phrase — is a real marker. If the save gate only asked about the
    ///     section and the phrase, authoring one would appear to work and save
    ///     nothing.
    /// </summary>
    [Fact]
    public void AMarkerWithOnlyEventCodesIsStillWorthSaving()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.Services/Editing/Events/EventMarkerEdit.cs"));
        var gate = Between(source, "var shouldSaveEvent =", ";");

        Assert.Contains("fields.Events", gate, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The section field is a closed PICKER, not free text. It shipped as an
    ///     <c>AutoCompleteBox</c> in 0.39.0, which only opens its list once
    ///     something has been typed — so the preselection existed and was
    ///     undiscoverable, and the owner reported it as never having been ported.
    /// </summary>
    [Fact]
    public void TheSectionFieldIsAPickerAndTheEventCodesHaveAControl()
    {
        var view = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Dialogs/EventDialogView.axaml"));

        Assert.Contains("ItemsSource=\"{Binding SectionOptions}\"", view, StringComparison.Ordinal);
        // The ELEMENT, not the word — the markup comment explains why the
        // AutoCompleteBox went away and must not fail its own guard.
        Assert.DoesNotContain("<AutoCompleteBox", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding EventToggles}\"", view, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The tone field is a picker over the tones the chart actually HAS.
    ///     <para>
    ///         It was a bare <c>TextBox</c> until 0.57: the key had to be typed
    ///         from memory, nothing said what the chart defined, and a typo made a
    ///         marker that validated as "no matching tone" and silently never
    ///         applied. It is EDITABLE where the section picker is closed — a tone
    ///         name is an open vocabulary (naming one before authoring it is a
    ///         legitimate order of work, and imports carry arbitrary keys) — and a
    ///         ComboBox rather than an AutoCompleteBox for the same reason the
    ///         section field stopped being one in 0.39.2.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheToneFieldIsAnEditablePickerOverTheChartsOwnTones()
    {
        var view = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Dialogs/EventDialogView.axaml"));
        var vm = StripComments(
            UiSourceTree.ReadAllText(RepoPath("DotCharter.Presentation/Screens/Dialogs/EventViewModel.cs")));

        Assert.Contains("ItemsSource=\"{Binding ToneOptions}\"", view, StringComparison.Ordinal);
        // Editable, and two-way — a one-way Text binding on an editable ComboBox
        // reads perfectly and saves nothing the charter typed.
        Assert.Contains("IsEditable=\"True\"", view, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ToneName, Mode=TwoWay}\"", view, StringComparison.Ordinal);

        // A list nobody fills is a picker with no options — the exact shape of a
        // control that looks alive over nothing.
        Assert.Contains("_toneOptions = [.. ChartToneNames.Offerable(", vm, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A tone change whose name has no BODY is placed, exports, and plays
    ///     SILENTLY. That state was universal on psarc-opened charts and the editor
    ///     never said a word, so the dialog warns at the point of authoring.
    /// </summary>
    [Fact]
    public void AToneWithNoBodyIsWarnedAboutRatherThanRefused()
    {
        var view = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Dialogs/EventDialogView.axaml"));
        var vm = StripComments(
            UiSourceTree.ReadAllText(RepoPath("DotCharter.Presentation/Screens/Dialogs/EventViewModel.cs")));
        var edit = StripComments(
            UiSourceTree.ReadAllText(RepoPath("DotCharter.Services/Editing/Events/EventMarkerEdit.cs")));

        Assert.Contains("IsVisible=\"{Binding ToneHasNoBody}\"", view, StringComparison.Ordinal);
        Assert.Contains("Loc Dialog_ToneNoBody}", view, StringComparison.Ordinal);
        Assert.Contains("ChartToneNames.HasBody(", vm, StringComparison.Ordinal);
        // The warning must re-evaluate as the charter types, or it only ever
        // reflects whatever the dialog opened with.
        Assert.Contains("OnPropertyChanged(nameof(ToneHasNoBody))", vm, StringComparison.Ordinal);

        // Warned, never refused: naming a tone before authoring it is legitimate.
        var gate = Between(edit, "var shouldSaveTone =", ";");
        Assert.DoesNotContain("HasBody", gate, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two DEPENDENT rows grey out — the section status needs a section,
    ///     solo needs a phrase — and until 2026-09-04 the reason for each lived
    ///     only in a source comment. A greyed control with no reason printed is a
    ///     dead affordance: the charter sees an inert combo and an inert checkbox
    ///     and nothing that connects them to the row above.
    ///     <para>
    ///         The hint is bound to the INVERSE of the same predicate that
    ///         disables the control, so the two can never disagree — a hint that
    ///         shows while the control is live is worse than none.
    ///     </para>
    /// </summary>
    [Fact]
    public void EachDependentRow_PrintsTheReasonItIsUnavailable()
    {
        var view = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Dialogs/EventDialogView.axaml"));

        foreach (var (source, hintKey) in new[]
                 {
                     ("SectionName", "Dialog_Event_Status_NeedsSection"),
                     ("PhraseName", "Dialog_Event_SoloPhrase_NeedsPhrase")
                 })
        {
            // The control disables on IsNotNullOrEmpty…
            Assert.Contains(
                $"IsEnabled=\"{{Binding {source}, Converter={{x:Static StringConverters.IsNotNullOrEmpty}}}}\"",
                view, StringComparison.Ordinal);
            // …and the hint shows on exactly its inverse, off the same source.
            Assert.Contains(
                $"IsVisible=\"{{Binding {source}, Converter={{x:Static StringConverters.IsNullOrEmpty}}}}\"",
                view, StringComparison.Ordinal);
            Assert.Contains($"Loc {hintKey}}}", view, StringComparison.Ordinal);
        }

        // Typing goes to the dialog, not to the editor underneath it: the first
        // field takes focus through the shared attached behaviour (no timer —
        // see UI/DialogFocusOnOpenTests).
        Assert.Contains("dlgv:FocusOnOpen.IsTarget=\"True\"", view, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Picking a section fills the phrase with the same name, and only when the
    ///     box is empty or still holds the section's own name.
    ///     <para>
    ///         Measured over the 89 hand-finished arrangements in
    ///         <c>TestData/EOF</c>: 2,761 of 2,765 sections (99.9%) have a phrase
    ///         iteration at the same instant and 2,576 (93.2%) carry the same NAME.
    ///         The owner typed it by hand 42 times in one 12-minute session.
    ///     </para>
    /// </summary>
    [Fact]
    public void PickingASectionFillsThePhraseButNeverOverwritesATypedOne()
    {
        var source = StripComments(
            UiSourceTree.ReadAllText(RepoPath("DotCharter.Presentation/Screens/Dialogs/EventViewModel.cs")));
        var handler = Between(source, "public IEventSectionOption? SelectedSection", "public string SectionName");

        Assert.Contains("PhraseName = SectionName", handler, StringComparison.Ordinal);
        // The guard is the whole safety property: a phrase the charter typed is
        // theirs, and the ~7% of sections that carry a different phrase name must
        // survive a section change.
        Assert.Contains("_phraseIsAutoFilled", handler, StringComparison.Ordinal);
        Assert.Contains("IsNullOrWhiteSpace(PhraseName)", handler, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' is gone from the event dialog's source — this guard no longer guards.");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        return source.Substring(from, to - from);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
