using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     A 9-string guitar is C#1 F#1 B1 E2 A2 D3 G3 B3 E4: the ninth string extends DOWN like the
///     7th and 8th. Every site that used to stop at eight strings has its case here (the table,
///     the tuning names and presets, the property deriver, refret, the MIDI preview, the string
///     mask and the live confidence tracker); the export refusal is pinned at the end.
///     The open-string table, the gutter, the tuner, live judging, alphaTex, Guitar Pro, the dialog's
///     presets and the colour slots are pinned beside their 7- and 8-string cases.
/// </summary>
public sealed class NineStringGuitarTests
{
    private static readonly int[] NineStandard = [25, 30, 35, 40, 45, 50, 55, 59, 64];

    private static Arrangement NineString()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 9 };
        for (var i = 0; i < 9; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    [Fact]
    public void TheTuningNamer_NamesTheNineStringStandardAndItsDrops()
    {
        Assert.Equal("9-String Standard", TuningNameResolver.ResolveFromOffsets(9, new int[9]));
        Assert.Equal("9-String Drop B", TuningNameResolver.ResolveFromOffsets(9, [-2, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("9_STRING_STANDARD", ExtendedRangeTunings.TypeNameFor(new int[9]));
        Assert.Contains(ExtendedRangeTunings.All, t => t.StringCount == 9 && t.TypeName == "9_STRING_DROP_B");
        Assert.True(ExtendedRangeTunings.TryGetOffsets("9_STRING_DROP_B", 9, out var offsets));
        Assert.Equal([-2, 0, 0, 0, 0, 0, 0, 0, 0], offsets);
        Assert.Equal(["C#", "F#", "B", "E", "A", "D", "G", "B", "E"], TuningUtilities.GetTuningNames(9, new int[9]));
    }

    [Fact]
    public void ThePropertyDeriver_PricesTheNinthString()
    {
        var arrangement = NineString();
        // B3 open and E4 at fret 2 (F#4) on the adjacent strings 7 and 8: a fifth.
        var chord = new Chord { Position = new FractionalPosition(0), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 7, Fret = 0 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 8, Fret = 2 });
        arrangement.Levels[0].Chords.Add(chord);

        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(arrangement).PowerChords);
    }

    [Fact]
    public void Refret_KeepsEveryPitchOfANineStringScale_AndSpreadsItOffTheTopString()
    {
        var arrangement = NineString();
        var beats = Enumerable.Range(0, 40)
            .Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 }).ToList();
        int[] frets = [12, 14, 16, 17, 19, 21, 23, 24];
        for (var i = 0; i < frets.Length; i++)
        {
            arrangement.Levels[0].Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(1 + i / 2, new Fraction((i % 2) * 2, 4)),
                StringIndex = 8,
                Fret = frets[i]
            });
        }

        int[] Pitches() => [.. arrangement.Levels[0].Notes.Select(n => SoundingPitch.MidiFor(arrangement, n.StringIndex, n.Fret) ?? -1)
            .Order()];
        var before = Pitches();

        var result = new RefretService(new FretAnchorGenerationService(), new FingeringGenerationService())
            .RefretArrangement(arrangement, beats);

        Assert.Equal(before, Pitches());
        Assert.True(result.NotesChanged > 0, "A scale on the top string should spread across strings.");
        Assert.All(arrangement.Levels[0].Notes, n => Assert.InRange(n.StringIndex, 0, 8));
    }

    [Fact]
    public void TheMidiPreview_PlaysAllNineOpenStrings()
    {
        var arrangement = NineString();
        var pitches = new MidiStringPitches();
        pitches.Refresh(arrangement);

        Assert.Equal(NineStandard, Enumerable.Range(0, 9).Select(s => pitches.MidiFor(s, 0, 0)));
        Assert.Equal(0, pitches.MidiFor(9, 0, 0));
    }

    [Fact]
    public void TheStringMask_AddressesTheNinthString()
    {
        var mask = StringEditMask.FromStrings(8);

        Assert.True(mask.IsExplicit(8));
        Assert.True(mask.Includes(8));
        Assert.False(mask.Includes(0));
    }

    [Fact]
    public void TheLiveConfidenceTracker_HasASlotForEveryString() =>
        Assert.Equal(StandardOpenStrings.MaxStringCount, LiveStringConfidenceTracker.MaxStrings);

    /// <summary>
    ///     Export decision: a package spells at most eight strings (<see cref="Rs7StringLayout.MaxStrings" />)
    ///     and the rs7string mod plays seven, so a ninth string has no RS7 layout. The validator reports an
    ///     error and the SNG mapper refuses with the reason, rather than folding the ninth string's notes
    ///     onto the last string the package has.
    /// </summary>
    [Fact]
    public void ANineStringGuitar_IsRefusedOnExport_WithItsReason()
    {
        var arrangement = NineString();
        Assert.False(Rs7StringLayout.IsExtendedGuitar(arrangement));
        Assert.True(Rs7StringLayout.HasUnsupportedStringCount(arrangement));

        var refusal = SngFormatLimits.ExportRefusal(arrangement);
        Assert.NotNull(refusal);
        Assert.Contains("9 strings", refusal, StringComparison.Ordinal);
        Assert.Contains("at most 8", refusal, StringComparison.Ordinal);

        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 8, Fret = 3 });
        var project = new ChartProject();
        project.Beats.Add(new BeatMarker { Time = 0, Bpm = 120, IsMeasure = true });
        project.Arrangements.Add(arrangement);
        var error = Assert.Single(new ChartExportValidator().Validate(project),
            f => f.Severity == ExportFindingSeverity.Error && f.MessageKey == ChartExportValidator.KeyStringCountUnsupported);
        Assert.Equal("9", error.Detail);
    }

    [Fact]
    public void TheArrangementDialog_OffersUpToTheModelsMaximum()
    {
        var axaml = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs",
            "ArrangementSettingsDialogView.axaml"));

        Assert.Contains($"Minimum=\"1\" Maximum=\"{StandardOpenStrings.MaxStringCount}\"", axaml, StringComparison.Ordinal);
    }
}
