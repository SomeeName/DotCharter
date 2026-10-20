using DotCharter.Services.Editing.Catalog;

namespace DotCharter.Tests.Services.Editing.Catalog;

/// <summary>
///     The rule that stops a phrase paste from silently producing an unplayable
///     or wrongly-pitched chart. Pure — no files, no services.
/// </summary>
public class PhrasePasteCompatibilityTests
{
    private static Arrangement Arr(int strings = 6, int capo = 0, string tuningType = "E_STANDARD",
        int[]? offsets = null)
    {
        var arrangement = new Arrangement { StringCount = strings, Capo = capo, TuningType = tuningType };
        var steps = offsets ?? TuningUtilities.GetOffsetsForTuningType(tuningType, strings);
        foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
        return arrangement;
    }

    private static PhraseCatalogEntry Entry(
        IEnumerable<(int StringIndex, int Fret)> sounds,
        int strings = 6, int capo = 0, int[]? offsets = null)
    {
        var payload = new ClipboardPayload();
        foreach (var (stringIndex, fret) in sounds)
        {
            payload.Notes.Add(new GuitarNote
            {
                Position = FractionalPosition.Zero, StringIndex = stringIndex, Fret = fret
            });
        }

        return new PhraseCatalogEntry
        {
            Id = "p",
            Name = "P",
            Payload = payload,
            Source = new PhraseSourceContext
            {
                StringCount = strings,
                Capo = capo,
                TuningOffsets = offsets ?? TuningUtilities.GetOffsetsForTuningType("E_STANDARD", strings)
            }
        };
    }

    [Fact]
    public void SameTuningSameCapo_IsOk()
    {
        var check = PhrasePasteCompatibility.Evaluate(Entry([(0, 5), (1, 7)]), Arr());

        Assert.Equal(PhrasePasteSeverity.Ok, check.Severity);
        Assert.Empty(check.Issues);
    }

    [Fact]
    public void NullTarget_IsOk() => Assert.Equal(PhrasePasteSeverity.Ok, PhrasePasteCompatibility.Evaluate(Entry([(0, 5)]), null).Severity);

    [Fact]
    public void AStringTheDestinationDoesNotHave_IsBlocked()
    {
        // Captured off a 7-string; pasted into a 6-string bass part.
        var entry = Entry([(0, 5), (6, 3)], strings: 7);

        var check = PhrasePasteCompatibility.Evaluate(entry, Arr());

        Assert.True(check.IsBlocked);
        Assert.Contains(check.Issues,
            i => i.LocalizationKey == PhrasePasteCompatibility.KeyStringOutOfRange);
    }

    [Fact]
    public void AFretPastTheModelCeiling_IsBlocked()
    {
        var check = PhrasePasteCompatibility.Evaluate(Entry([(0, 25)]), Arr());

        Assert.True(check.IsBlocked);
        Assert.Contains(check.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyFretOutOfRange);
    }

    [Fact]
    public void ANonZeroFretUnderTheDestinationCapo_IsBlocked()
    {
        // Fret 2 with a capo on 3 is the malformed state inspector L01 and
        // ChartExportValidator.KeyNoteBelowCapo both reject.
        var check = PhrasePasteCompatibility.Evaluate(Entry([(0, 2), (1, 7)]), Arr(capo: 3));

        Assert.True(check.IsBlocked);
        Assert.Contains(check.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyFretBelowCapo);
    }

    [Fact]
    public void AnOpenStringUnderACapo_IsNotBelowTheCapo()
    {
        // Fret 0 IS the capo — it is never "below" it. Only the changed pitch
        // is worth reporting, and only as a warning.
        var check = PhrasePasteCompatibility.Evaluate(Entry([(0, 0), (1, 7)]), Arr(capo: 3));

        Assert.Equal(PhrasePasteSeverity.Warning, check.Severity);
        Assert.DoesNotContain(check.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyFretBelowCapo);
        Assert.Contains(check.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyCapoDiffers);
    }

    [Fact]
    public void ADifferentCapoWithNoOpenStrings_IsOk()
    {
        // Every fretted note sounds the same whatever the capo, so there is
        // nothing to warn about.
        var check = PhrasePasteCompatibility.Evaluate(Entry([(0, 5), (1, 7)], capo: 0), Arr(capo: 2));

        Assert.Equal(PhrasePasteSeverity.Ok, check.Severity);
    }

    [Fact]
    public void ADifferentTuningOnAStringThePhraseUses_Warns()
    {
        var dropD = new[] { -2, 0, 0, 0, 0, 0 };

        var check = PhrasePasteCompatibility.Evaluate(Entry([(0, 5)]), Arr(offsets: dropD));

        Assert.Equal(PhrasePasteSeverity.Warning, check.Severity);
        Assert.Contains(check.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyTuningDiffers);
    }

    [Fact]
    public void ADifferentTuningOnAStringThePhraseNeverTouches_IsOk()
    {
        // Drop D changes only string 1; a phrase played on strings 4-6 does not
        // care, and telling the user otherwise would train them to ignore the
        // warning that matters.
        var dropD = new[] { -2, 0, 0, 0, 0, 0 };

        var check = PhrasePasteCompatibility.Evaluate(Entry([(3, 5), (4, 5)]), Arr(offsets: dropD));

        Assert.Equal(PhrasePasteSeverity.Ok, check.Severity);
    }

    [Fact]
    public void ChordNotesAreCheckedToo()
    {
        var payload = new ClipboardPayload();
        var chord = new Chord { Position = FractionalPosition.Zero };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 6, Fret = 3 });
        payload.Chords.Add(chord);

        var entry = new PhraseCatalogEntry
        {
            Id = "p", Name = "P", Payload = payload,
            Source = new PhraseSourceContext { StringCount = 7, TuningOffsets = new[] { 0, 0, 0, 0, 0, 0, 0 } }
        };

        Assert.True(PhrasePasteCompatibility.Evaluate(entry, Arr()).IsBlocked);
    }

    [Fact]
    public void BlockedWinsOverWarning()
    {
        var dropD = new[] { -2, 0, 0, 0, 0, 0 };
        var entry = Entry([(0, 0), (6, 4)], strings: 7);

        var check = PhrasePasteCompatibility.Evaluate(entry, Arr(capo: 2, offsets: dropD));

        Assert.Equal(PhrasePasteSeverity.Blocked, check.Severity);
        Assert.True(check.Issues.Count > 1);
    }
}
