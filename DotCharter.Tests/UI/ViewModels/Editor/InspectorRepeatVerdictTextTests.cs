using System.Text.RegularExpressions;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The inspector's repeat line for a selected chord (#564): every reason
///     <see cref="RepeatChords.Explain" /> can give has its words in all three languages, a per-string
///     reason names the string the way the rest of the inspector does, and the view model's verdict is the
///     export rule's own answer for that chord.
/// </summary>
public sealed partial class InspectorRepeatVerdictTextTests
{
    private static readonly string[] Tuning = ["E", "A", "D", "G", "B", "e"];

    public static TheoryData<RepeatChordReason> AllReasons() => [.. Enum.GetValues<RepeatChordReason>()];

    [Theory]
    [MemberData(nameof(AllReasons))]
    public void EveryReason_HasItsWords_InAllThreeLanguages(RepeatChordReason reason)
    {
        var key = RepeatVerdictText.ReasonKey(reason);
        string? english = null;
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var value = ResxValue(file, key);
            Assert.False(string.IsNullOrWhiteSpace(value), $"{file} has no {key}");
            english ??= value;
            // A per-string reason has exactly one {0}; the rest have none — in every language alike.
            Assert.Equal(Placeholders(english), Placeholders(value!));
        }
    }

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void TheYesAndNoFrames_TakeTheReason(string file)
    {
        Assert.Equal(1, Placeholders(ResxValue(file, "Inspector_RepeatVerdict_Yes")!));
        Assert.Equal(1, Placeholders(ResxValue(file, "Inspector_RepeatVerdict_No")!));
        Assert.False(string.IsNullOrWhiteSpace(ResxValue(file, "Inspector_RepeatVerdictTip")));
    }

    [Fact]
    public void APerStringReason_NamesTheStringLikeTheInspector()
    {
        var text = RepeatVerdictText.Format(new RepeatChordVerdict(RepeatChordReason.Vibrato, 1), 6, Tuning,
            FakeLocalization.English());

        Assert.Equal("Repeat: no — vibrato on string A (5)", text);
    }

    [Fact]
    public void AShippedRepeat_SaysYes()
    {
        var text = RepeatVerdictText.Format(new RepeatChordVerdict(RepeatChordReason.Repeat), 6, Tuning,
            FakeLocalization.English());

        Assert.Equal("Repeat: yes — same chord as the previous strum", text);
    }

    [Fact]
    public void TheInspectorsVerdict_IsTheExportRulesAnswer()
    {
        // Shape A, then shape A again with vibrato on the A string (keeps its chord notes),
        // then shape A plain (repeats the strum before it).
        var template = new ChordTemplate { Id = 1, Name = "A" };
        template.Frets[0] = 3;
        template.Frets[1] = 5;
        template.Frets[2] = 5;
        var beats = Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 }).ToList();
        var chords = new List<Chord> { Strum(1), Strum(2), Strum(3) };
        chords[1].ChordNotes[1].Vibrato = true;

        var vibrato = InspectorViewModel.ExplainRepeat([template], [], chords, [], beats, chords[1]);
        var plain = InspectorViewModel.ExplainRepeat([template], [], chords, [], beats, chords[2]);
        var shipped = RepeatChords.Compute([template], [], chords, [], beats);

        Assert.Equal(new RepeatChordVerdict(RepeatChordReason.Vibrato, 1), vibrato);
        Assert.Equal(new RepeatChordVerdict(RepeatChordReason.Repeat), plain);
        Assert.Equal(shipped.Contains(chords[1]), vibrato!.Value.Ships);
        Assert.Equal(shipped.Contains(chords[2]), plain!.Value.Ships);
        Assert.Null(InspectorViewModel.ExplainRepeat([template], [], chords, [], beats, Strum(9)));

        static Chord Strum(int beat)
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = 1 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
            return chord;
        }
    }

    private static int Placeholders(string value) => PlaceholderPattern().Matches(value).Count;

    private static string? ResxValue(string file, string key)
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file));
        var m = Regex.Match(resx,
            "<data name=\"" + Regex.Escape(key) + "\" xml:space=\"preserve\">\\s*<value>(?<v>.*?)</value>",
            RegexOptions.Singleline);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value) : null;
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
