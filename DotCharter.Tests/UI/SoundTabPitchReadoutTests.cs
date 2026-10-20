using System.Xml.Linq;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Sound tab says what a note SOUNDS, not only where it sits.
///     <para>
///         "the sound tab in the inspector view should also show the note's
///         actual pitch, not [just] the fret". A string number and a fret number
///         are a POSITION; the pitch is what a voicing, a tuning and a capo are
///         reasoned about in, and it is the one piece of arithmetic a charter
///         cannot do in their head on this app's own turf — with a dropped
///         tuning or a capo, fret 5 on the low E is not A.
///     </para>
///     <para>
///         The pure half lives here (the readout is a function, so it does not
///         need the inspector's graph); the constructed-and-laid-out half is
///         <c>Perf/InspectorConstructionUiPerfSmokeTests</c> and
///         <c>Perf/ChordTemplateEditorConstructionUiPerfSmokeTests</c>, which need
///         the real Avalonia session and are outside the default gate.
///     </para>
/// </summary>
public sealed class SoundTabPitchReadoutTests
{
    // ── The pitch a guitarist would name ────────────────────────────────────

    /// <summary>Standard tuning, the notes anyone with a guitar can check.</summary>
    [Theory]
    [InlineData(0, 0, "E2")]
    [InlineData(0, 5, "A2")]  // the low E at the 5th IS the open A…
    [InlineData(1, 0, "A2")]  // …and here is the open A to prove it
    [InlineData(2, 3, "F3")]
    [InlineData(5, 12, "E5")]
    [InlineData(1, 1, "A#2")]
    public void StandardTuning_NamesTheNoteAGuitaristWould(int stringIndex, int fret, string expected) =>
        Assert.Equal(expected, SoundingPitchReadout.For(Guitar(), stringIndex, fret));

    /// <summary>
    ///     Drop D. The reason the readout exists: the FRET is unchanged and the
    ///     pitch is not, so the number on screen stopped being the answer.
    /// </summary>
    [Theory]
    [InlineData(0, 0, "D2")]
    [InlineData(0, 5, "G2")]
    [InlineData(0, 7, "A2")]
    [InlineData(1, 5, "D3")] // an untouched string keeps its standard pitch
    public void ADroppedTuning_MovesOnlyTheStringItDrops(int stringIndex, int fret, string expected) =>
        Assert.Equal(expected, SoundingPitchReadout.For(DropD(), stringIndex, fret));

    /// <summary>
    ///     A capo. Fret 0 is OPEN and the capo stops it at the capo fret, so the
    ///     same "0" on screen sounds three semitones higher than it did.
    /// </summary>
    [Theory]
    [InlineData(0, 0, "G2")] // open under a capo at 3 = the capo
    [InlineData(0, 3, "G2")] // and so is fret 3 — the same sound, two spellings
    [InlineData(0, 5, "A2")]
    public void ACapo_RaisesTheOpenString(int stringIndex, int fret, string expected) =>
        Assert.Equal(expected, SoundingPitchReadout.For(Capo3(), stringIndex, fret));

    /// <summary>A bass is written where a guitar is and sounds an octave below.</summary>
    [Theory]
    [InlineData(0, 0, "E1")]
    [InlineData(0, 5, "A1")]
    [InlineData(3, 0, "G2")]
    public void ABass_SoundsAnOctaveLower(int stringIndex, int fret, string expected) =>
        Assert.Equal(expected, SoundingPitchReadout.For(Bass(), stringIndex, fret));

    // ── …and nothing at all when there is nothing honest to say ─────────────

    /// <summary>
    ///     THE MUTED / UNPLAYED STRING. The chord editor's fret column is free
    ///     text: empty means "this shape does not play that string" and
    ///     <c>x</c> / <c>X</c> means muted. Neither sounds a pitch, and a
    ///     plausible-looking wrong note is worse than a blank.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    [InlineData("X")]
    [InlineData("-")]
    public void AMutedOrUnplayedString_ShowsNoPitchAtAll(string? fretText) =>
        Assert.Equal(string.Empty, SoundingPitchReadout.ForFretText(Guitar(), 0, fretText));

    /// <summary>A string that IS played still gets its name, through the same
    /// text path — otherwise the blank above would prove nothing.</summary>
    [Theory]
    [InlineData("0", "E2")]
    [InlineData("5", "A2")]
    [InlineData("12", "E3")]
    public void APlayedString_StillGetsItsNameThroughTheTextPath(string fretText, string expected) =>
        Assert.Equal(expected, SoundingPitchReadout.ForFretText(Guitar(), 0, fretText));

    /// <summary>
    ///     No arrangement, a string the arrangement does not have, and a fret
    ///     BEHIND the capo (malformed — inspector rule <c>L01</c>) each print
    ///     nothing rather than a guess.
    /// </summary>
    [Fact]
    public void WithNothingHonestToName_TheReadoutIsEmpty()
    {
        Assert.Equal(string.Empty, SoundingPitchReadout.For(null, 0, 5));
        Assert.Equal(string.Empty, SoundingPitchReadout.For(Guitar(), 9, 5));
        Assert.Equal(string.Empty, SoundingPitchReadout.For(Guitar(), -1, 5));
        Assert.Equal(string.Empty, SoundingPitchReadout.For(Capo3(), 0, 2));
    }

    // ── The chord editor's per-string rows ──────────────────────────────────

    /// <summary>
    ///     A row names the pitch ITS fret sounds, on ITS string, in the
    ///     arrangement it was loaded for.
    /// </summary>
    [Fact]
    public void AChordRow_NamesThePitchOfItsOwnStringAndFret()
    {
        var arr = Capo3();

        Assert.Equal("G2", new StringFingeringInput(0) { Arrangement = arr, FretText = "0" }.PitchName);
        Assert.Equal("D3", new StringFingeringInput(1) { Arrangement = arr, FretText = "5" }.PitchName);
        Assert.Equal("G4", new StringFingeringInput(5) { Arrangement = arr, FretText = "3" }.PitchName);
    }

    /// <summary>The muted / unplayed string, at the row level.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x")]
    public void AChordRow_ForAMutedStringNamesNothing(string? fretText) =>
        Assert.Equal(string.Empty,
            new StringFingeringInput(0) { Arrangement = Guitar(), FretText = fretText }.PitchName);

    /// <summary>
    ///     …and it FOLLOWS the fret, rather than being filled once and left to
    ///     rot. The readout is computed from <c>FretText</c>, so no edit path can
    ///     rewrite a fret and forget to re-derive it — which is the failure mode
    ///     a pushed value has.
    /// </summary>
    [Fact]
    public void AChordRow_RepitchesItselfWhenTheFretIsTyped()
    {
        var row = new StringFingeringInput(0) { Arrangement = Guitar(), FretText = "0" };
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.FretText = "5";

        Assert.Equal("A2", row.PitchName);
        Assert.Contains(nameof(StringFingeringInput.PitchName), raised);
    }

    /// <summary>Before the editor hands a row its arrangement there is nothing to
    /// measure against, and a row says nothing rather than assuming E standard.</summary>
    [Fact]
    public void AChordRow_WithNoArrangementNamesNothing() =>
        Assert.Equal(string.Empty, new StringFingeringInput(0) { FretText = "5" }.PitchName);

    /// <summary>
    ///     …which is exactly why the editor has to HAND it one. A row whose
    ///     <c>Arrangement</c> nobody sets is silently blank for every string, in
    ///     every chord — the whole column dead with nothing failing, the shape
    ///     this repo keeps re-shipping. The rows get it beside their tuning
    ///     letter, from the same source.
    /// </summary>
    [Fact]
    public void TheEditor_HandsEveryRowTheArrangementItIsMeasuredAgainst()
    {
        var source = string.Concat(UiSourceTree.EnumerateFiles(
                UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Panels")),
                "ChordTemplateEditorViewModel*.cs")
            .Select(UiSourceTree.ReadAllText));

        Assert.Contains(".Arrangement = ", source, StringComparison.Ordinal);
    }

    // ── The single-note card's readout ──────────────────────────────────────

    /// <summary>
    ///     <c>InspectorViewModel</c> takes far too much of the app to construct
    ///     in the gate (nothing in this suite does), so its one-line readout is
    ///     pinned at the source instead of by driving it. Two ways it can be
    ///     wrong and both are silent: measuring against the wrong three inputs,
    ///     and — the sneakier one — never being RE-READ, which leaves the card
    ///     naming the pitch of the previous fret while the number beside it
    ///     moves.
    /// </summary>
    [Fact]
    public void TheSingleNoteCard_ReadsThePitchOffTheStringAndFretAndFollowsThem()
    {
        var files = UiSourceTree.EnumerateFiles(
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor")),
            "InspectorViewModel*.cs").ToList();
        Assert.NotEmpty(files);

        var source = string.Concat(files.Select(UiSourceTree.ReadAllText));
        var flat = string.Join(" ", source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(
            "SoundingPitchReadout.For(_chartEditor.ActiveArrangement, NoteStringIndex, NoteFret)",
            flat, StringComparison.Ordinal);

        // Order-independent: whatever else decorates the field, the pitch hop
        // has to be among the attributes immediately above it.
        foreach (var field in new[] { "private int _noteFret;", "private int _noteStringIndex;" })
        {
            var at = flat.IndexOf(field, StringComparison.Ordinal);
            Assert.True(at > 0, $"{field} is gone — repoint this guard at whatever replaced it");

            var attributes = flat[Math.Max(0, at - 200)..at];
            Assert.Contains("NotifyPropertyChangedFor(nameof(NotePitchName))", attributes,
                StringComparison.Ordinal);
        }
    }

    // ── The markup that shows it ────────────────────────────────────────────

    /// <summary>
    ///     The single-note card prints the pitch beside the fret, and hides the
    ///     row rather than showing an empty one.
    /// </summary>
    [Fact]
    public void ThePlacementCard_BindsThePitchAndHidesItWhenThereIsNone()
    {
        var view = XDocument.Load(SoundTabPath());
        var ns = view.Root!.Name.Namespace;

        var readout = view.Descendants(ns + "TextBlock")
            .Single(e => (string?)e.Attribute("Text") == "{Binding NotePitchName}");

        // Since #118 the pitch shares its line with the length rather than a row
        // of its own, so the readout hides ITSELF.
        Assert.Equal("{Binding HasNotePitch}", (string?)readout.Attribute("IsVisible"));
    }

    /// <summary>
    ///     Every string of a selected chord names the pitch its fret produces,
    ///     in the row template the per-string editor repeats.
    /// </summary>
    [Fact]
    public void TheChordRows_EachNameTheirOwnPitch()
    {
        var view = XDocument.Load(ChordEditorPath());
        var ns = view.Root!.Name.Namespace;

        var rowTemplate = view.Descendants(ns + "DataTemplate")
            .Single(e => ((string?)e.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}DataType"))
                ?.EndsWith("StringFingeringInput", StringComparison.Ordinal) == true);

        Assert.Single(rowTemplate.Descendants(ns + "TextBlock"),
            e => (string?)e.Attribute("Text") == "{Binding PitchName}");
    }

    /// <summary>
    ///     THE STRING ORDER, which the owner has reported inverted twice. The
    ///     pitch column may not invent a third convention: it rides
    ///     <c>DisplayStringInputs</c>, the flip-aware mirror the rows already use
    ///     (and which follows <c>ChartRenderContext.LaneTopFor</c> —
    ///     <c>flip ? count - 1 - index : index</c>, lane 0 at the top).
    /// </summary>
    [Fact]
    public void TheChordRows_KeepRidingTheHighwaysOwnOrder()
    {
        var view = XDocument.Load(ChordEditorPath());
        var ns = view.Root!.Name.Namespace;

        var rows = view.Descendants(ns + "ItemsControl")
            .Single(e => e.Descendants(ns + "TextBlock")
                .Any(t => (string?)t.Attribute("Text") == "{Binding PitchName}"));

        Assert.Equal("{Binding DisplayStringInputs}", (string?)rows.Attribute("ItemsSource"));
    }

    /// <summary>
    ///     A key that does not exist prints its own IDENTIFIER on screen, in
    ///     every language, and nothing throws or logs.
    /// </summary>
    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void TheNewKeys_ExistInEveryLanguage(string fileName)
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName));
        var values = doc.Root!.Elements("data")
            .Where(e => (string?)e.Attribute("name") is "Inspector_SoundingPitch" or "Inspector_SoundingPitchTip")
            .Select(e => e.Element("value")?.Value ?? "")
            .ToList();

        Assert.Equal(2, values.Count);
        Assert.All(values, v => Assert.False(string.IsNullOrWhiteSpace(v), $"an empty value in {fileName}"));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static Arrangement Guitar() =>
        new() { Name = "Lead", ArrangementType = ArrangementType.Lead };

    private static Arrangement DropD()
    {
        var arr = Guitar();
        foreach (var step in new[] { -2, 0, 0, 0, 0, 0 })
            arr.TuningPitches.Add(new TuningOffset(step));
        return arr;
    }

    private static Arrangement Capo3()
    {
        var arr = Guitar();
        arr.Capo = 3;
        return arr;
    }

    private static Arrangement Bass() =>
        new() { Name = "Bass", ArrangementType = ArrangementType.Bass };

    /// <summary>The note's Placement section, its own view since #601.</summary>
    private static string SoundTabPath() =>
        UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector", "SoundPlacementView.axaml"));

    private static string ChordEditorPath() =>
        UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Panels", "ChordTemplateEditorView.axaml"));
}
