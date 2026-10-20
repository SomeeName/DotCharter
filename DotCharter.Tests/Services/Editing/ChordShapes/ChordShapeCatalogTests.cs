using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>The Chord shapes list's pure row model: use counts, duplicate grouping and finger state (#486).</summary>
public sealed class ChordShapeCatalogTests
{
    private readonly ChordShapesFixture _f = new();

    private ChordShapeEntry EntryOf(IReadOnlyList<ChordShapeEntry> entries, ChordTemplate template) =>
        entries.Single(e => ReferenceEquals(e.Template, template));

    [Fact]
    public void OneEntryPerTemplate_InTheArrangementsOrder()
    {
        var entries = ChordShapeCatalog.Build(_f.Arrangement, _f.Level1);

        Assert.Equal(_f.Arrangement.ChordTemplates, entries.Select(e => e.Template));
    }

    [Fact]
    public void UsesAreCountedOnTheLevelOnScreen_AndOnEveryLevel_ChordsAndHandShapesApart()
    {
        var entries = ChordShapeCatalog.Build(_f.Arrangement, _f.Level1);

        Assert.Equal(new ChordShapeUses(3, 0, 4, 1), EntryOf(entries, _f.Am).Uses);
        Assert.Equal(new ChordShapeUses(2, 1, 3, 1), EntryOf(entries, _f.AmAlt).Uses);
        Assert.Equal(new ChordShapeUses(1, 0, 1, 0), EntryOf(entries, _f.C).Uses);
        Assert.Equal(new ChordShapeUses(0, 0, 0, 0), EntryOf(entries, _f.G).Uses);
        Assert.Equal(2, EntryOf(entries, _f.Am).Uses.OnOtherLevels);
    }

    [Fact]
    public void TheLevelOnScreenDecidesTheLevelCount_NotTheFirstLevel()
    {
        var entries = ChordShapeCatalog.Build(_f.Arrangement, _f.Level0);

        Assert.Equal(new ChordShapeUses(1, 1, 4, 1), EntryOf(entries, _f.Am).Uses);
    }

    [Fact]
    public void AUseWhoseIdNoTemplateDeclares_CountsForNothing_AndASharedIdCountsForTheFirstTemplate()
    {
        _f.Level1.Chords.Add(new Chord { Position = new FractionalPosition(15), TemplateId = 99 });
        _f.Level1.Chords.Add(new Chord { Position = new FractionalPosition(15), TemplateId = -1 });
        var twin = _f.Am.Clone();
        _f.Arrangement.ChordTemplates.Add(twin);

        var entries = ChordShapeCatalog.Build(_f.Arrangement, _f.Level1);

        Assert.Equal(_f.Arrangement.Levels.Sum(l => l.Chords.Count(c => c.TemplateId is >= 0 and < 6)),
            entries.Sum(e => e.Uses.ChordsAll));
        Assert.Equal(0, EntryOf(entries, twin).Uses.All);
    }

    [Fact]
    public void DuplicatesAreSameFrets_InTheSameArpeggioFlavour()
    {
        var entries = ChordShapeCatalog.Build(_f.Arrangement, _f.Level1);

        Assert.Equal([_f.AmAlt], EntryOf(entries, _f.Am).Duplicates);
        Assert.Equal([_f.Am], EntryOf(entries, _f.AmAlt).Duplicates);
        // Same frets, but an RS-XML arpeggio (ForceArpeggioInRs alone): its own group.
        Assert.Empty(EntryOf(entries, _f.AmArp).Duplicates);
        Assert.False(EntryOf(entries, _f.C).IsDuplicate);
    }

    [Fact]
    public void BothArpeggioSpellings_AreDifferentFlavours_SoTheyNeverGroup()
    {
        var sng = _f.AmArp.Clone();
        sng.Id = 40;
        sng.ForceArpeggioInRs = false;
        sng.Arpeggio = true;
        _f.Arrangement.ChordTemplates.Add(sng);
        var xml = _f.AmArp.Clone();
        xml.Id = 41;
        _f.Arrangement.ChordTemplates.Add(xml);

        var entries = ChordShapeCatalog.Build(_f.Arrangement, _f.Level1);

        Assert.Equal([xml], EntryOf(entries, _f.AmArp).Duplicates);
        Assert.Empty(EntryOf(entries, sng).Duplicates);
    }

    [Fact]
    public void ADifferentNameOrFingeringIsStillADuplicate_ButASingleMovedFretIsNot()
    {
        var renamed = _f.C.Clone();
        renamed.Id = 50;
        renamed.Name = "Cadd9 (renamed)";
        renamed.Fingers[1] = 3;
        var moved = _f.C.Clone();
        moved.Id = 51;
        moved.Frets[1] = 5;
        _f.Arrangement.ChordTemplates.Add(renamed);
        _f.Arrangement.ChordTemplates.Add(moved);

        var entries = ChordShapeCatalog.Build(_f.Arrangement, _f.Level1);

        Assert.Equal([renamed], EntryOf(entries, _f.C).Duplicates);
        Assert.Empty(EntryOf(entries, moved).Duplicates);
    }

    [Fact]
    public void FingerState_IsMissingSuggestedOrComplete()
    {
        var entries = ChordShapeCatalog.Build(_f.Arrangement, _f.Level1);

        Assert.Equal(ChordFingerState.Missing, EntryOf(entries, _f.C).FingerState);
        Assert.Equal(ChordFingerState.Suggested, EntryOf(entries, _f.Am).FingerState);
        Assert.Equal(ChordFingerState.Complete, EntryOf(entries, _f.AmAlt).FingerState);
        // Only open strings: nothing to finger, so nothing is missing.
        Assert.Equal(ChordFingerState.Complete, EntryOf(entries, _f.Open).FingerState);
    }

    [Fact]
    public void OneFrettedStringWithoutAFinger_IsMissing_EvenWhenTheOthersAreFingered()
    {
        _f.AmAlt.Fingers.Remove(2);

        Assert.Equal(ChordFingerState.Missing, ChordShapeCatalog.FingerStateOf(_f.AmAlt, 6));
    }

    [Fact]
    public void NoLevelOnScreen_StillCountsEveryLevel()
    {
        var entries = ChordShapeCatalog.Build(_f.Arrangement, activeLevel: null);

        Assert.Equal(new ChordShapeUses(0, 0, 4, 1), EntryOf(entries, _f.Am).Uses);
    }
}
