using DotCharter.Services.Editing;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #118: the Sound tab names a string ONE way — the lane gutter's: the
///     tuning note and the guitarist's string number (the highest string is 1).
///     The String box printed the 0-based model index, the keyboard readout of
///     the mask printed a 1-based index counted from the LOW string and a
///     hard-coded English "all", and the fretboard between them said E A D G B e.
/// </summary>
public sealed class InspectorStringLabelTests
{
    private static readonly string[] Standard = ["E", "A", "D", "G", "B", "e"];
    private static readonly string[] DropD = ["D", "A", "D", "G", "B", "e"];
    private const string LabelFormat = "{0} ({1})";

    [Theory]
    [InlineData(0, "E (6)")]
    [InlineData(1, "A (5)")]
    [InlineData(5, "e (1)")]
    public void AString_IsNamedLikeTheLaneGutter(int index, string expected) =>
        Assert.Equal(expected, GuitarStringLabel.Format(index, 6, Standard, LabelFormat));

    [Fact]
    public void TheNumber_TellsDropDsTwoDStringsApart()
    {
        Assert.Equal("D (6)", GuitarStringLabel.Format(0, 6, DropD, LabelFormat));
        Assert.Equal("D (4)", GuitarStringLabel.Format(2, 6, DropD, LabelFormat));
    }

    [Fact]
    public void AStringTheTuningDoesNotName_IsUnknownNotAnInventedE()
    {
        Assert.Equal("? (4)", GuitarStringLabel.Format(0, 4, null, LabelFormat));
        Assert.Equal("? (1)", GuitarStringLabel.Format(3, 4, ["E", "A", "", "  "], LabelFormat));
    }

    /// <summary>The bass: four strings, so the low E is string 4.</summary>
    [Fact]
    public void TheNumber_FollowsTheArrangementsStringCount() =>
        Assert.Equal(["E (4)", "A (3)", "D (2)", "G (1)"], GuitarStringLabel.All(4, ["E", "A", "D", "G"], LabelFormat));

    [Fact]
    public void TheEmptyMask_ReadsFromTheResx_NeverTheHardCodedEnglishAll()
    {
        var keys = FakeLocalization.KeysOnly();
        Assert.Equal("Inspector_Queue_MaskAll",
            GuitarStringLabel.MaskReadout(StringEditMask.Empty, 6, Standard, keys));

        // …and the English the user sees is not the bare "all" any more.
        Assert.Equal("all strings",
            GuitarStringLabel.MaskReadout(StringEditMask.Empty, 6, Standard, FakeLocalization.English()));
    }

    [Fact]
    public void ASetMask_NamesItsStrings_LowestFirst()
    {
        var english = FakeLocalization.English();
        Assert.Equal("strings A (5), G (3)",
            GuitarStringLabel.MaskReadout(StringEditMask.FromStrings(3, 1), 6, Standard, english));
    }

    /// <summary>
    ///     The two places the issue names — the Placement card's String box and
    ///     the queue strip's mask readout — say the SAME thing for the same
    ///     string, because both are <see cref="GuitarStringLabel" />.
    /// </summary>
    [Fact]
    public void TheStringBoxAndTheMaskReadout_CountTheSameWay()
    {
        var english = FakeLocalization.English();
        var box = GuitarStringLabel.All(6, Standard, english["Inspector_StringLabelFmt"]);
        for (var i = 0; i < Standard.Length; i++)
        {
            var readout = GuitarStringLabel.MaskReadout(StringEditMask.Empty.Only(i), 6, Standard, english);
            Assert.Equal(english.Format("Inspector_Queue_MaskFmt", box[i]), readout);
        }
    }

    /// <summary>
    ///     The wiring half: the String picker's items, the chord-bend picker's
    ///     items and the mask readout all go through the shared formatter, and
    ///     the box is no longer a NumericUpDown over the raw index.
    /// </summary>
    [Fact]
    public void EveryStringLabelOnTheSoundTab_GoesThroughTheSharedFormatter()
    {
        var root = TestPaths.RepoRoot();
        var notes = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "ViewModels", "Editor", "InspectorViewModel.Notes.cs"));
        var bends = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "ViewModels", "Editor", "InspectorViewModel.Bends.cs"));
        var mask = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "ViewModels", "Editor", "Inspector", "StringMaskViewModel.cs"));
        // The Sound tab and the card views it hosts (#572).
        var view = string.Concat(Directory.GetFiles(Path.Combine(root, "DotCharter.UI", "Views", "Editor", "Inspector"), "Sound*View.axaml")
            .OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));

        Assert.Contains("GuitarStringLabel.All(", notes, StringComparison.Ordinal);
        Assert.Contains("GuitarStringLabel.Format(", bends, StringComparison.Ordinal);
        Assert.Contains("GuitarStringLabel.MaskReadout(", mask, StringComparison.Ordinal);
        Assert.DoesNotContain("StringEditMask.ToString()", mask, StringComparison.Ordinal);

        Assert.Contains("ItemsSource=\"{Binding NoteStringLabels}\"", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ChordBendStringLabels}\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"{Binding NoteStringIndex}\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Inspector_StringNumberFmt", view, StringComparison.Ordinal);
    }

    /// <summary>#118 item 8: a note with no sustain shows "0", not an empty
    /// box that reads as "not loaded" — and "0" parses back to no sustain.</summary>
    [Fact]
    public void AZeroLengthNote_ShowsZero()
    {
        Assert.Equal("0", InspectorViewModel.SustainBoxText(0));
        Assert.Equal("1/8", InspectorViewModel.SustainBoxText(0.5));
        Assert.True(DotCharter.UI.Controls.FractionalLengthFormat.TryParseBeats("0", out var num, out _));
        Assert.Equal(0, num);
    }
}
