using System.Collections.ObjectModel;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Highway.HighwayCommands;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.Fingering;

/// <summary>
///     #934: the edit resolver's mint (a highway reshape) and the Shapes panel's Suggest fingers
///     follow the song-wide 1·3 / 1·4 style: a power chord the arrangement states no majority for
///     takes the song's other arrangements' choice, and an arrangement's own majority still wins.
/// </summary>
public sealed class SongWideEditFingeringTests
{
    private static ChordTemplate PowerChord(int id, int fret, int? upperFinger)
    {
        var template = new ChordTemplate { Id = id };
        template.Frets[0] = fret;
        template.Frets[1] = fret + 2;
        if (upperFinger is not { } finger) return template;
        template.Fingers[0] = 1;
        template.Fingers[1] = finger;
        return template;
    }

    private static Arrangement Part(string name, params ChordTemplate[] templates)
    {
        var arrangement = new Arrangement { Name = name };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        foreach (var template in templates) arrangement.ChordTemplates.Add(template);
        return arrangement;
    }

    // The lead fingers its power chords 1·4; the edited part has none of its own.
    private static (ChartProject Project, Arrangement Edited) Song(params ChordTemplate[] edited)
    {
        var project = new ChartProject();
        project.Arrangements.Add(Part("Lead", PowerChord(0, 3, 4), PowerChord(1, 5, 4)));
        var part = Part("Rhythm", edited);
        project.Arrangements.Add(part);
        return (project, part);
    }

    private static int ReshapedUpperFinger(ChartProject project, Arrangement arrangement)
    {
        var open = new ChordTemplate { Id = 50 };
        open.Frets[2] = 0;
        arrangement.ChordTemplates.Add(open);
        var target = new Chord { TemplateId = open.Id, ChordNotes = [new ChordNote { StringIndex = 2, Fret = 0 }] };
        var placed = new ObservableCollection<Chord> { target };
        var newState = target.Clone();
        newState.ChordNotes = [new ChordNote { StringIndex = 0, Fret = 8 }, new ChordNote { StringIndex = 1, Fret = 10 }];

        ChordReshapeAction.Build(placed, arrangement, project, target, target.Clone(), newState, null).Execute();

        return arrangement.FindChordTemplate(newState.TemplateId)!.Fingers[1];
    }

    [Fact]
    public void AReshapeMintsThePowerChordTheSongFingers()
    {
        var (project, rhythm) = Song();

        Assert.Equal(4, ReshapedUpperFinger(project, rhythm));
    }

    [Fact]
    public void AReshapeKeepsTheArrangementsOwnMajority()
    {
        var (project, rhythm) = Song(PowerChord(10, 1, 3), PowerChord(11, 6, 3));

        Assert.Equal(3, ReshapedUpperFinger(project, rhythm));
    }

    [Fact]
    public void TheResolverFollowsTheSongStyleItIsGiven()
    {
        var (project, rhythm) = Song();
        var frets = new Dictionary<int, int> { [0] = 8, [1] = 10 };

        var minted = ChordTemplateResolver.Resolve(rhythm, frets, ArpeggioFlavour.Plain, null, [],
            ArrangementFingeringStyle.OfSong(project, rhythm));

        Assert.Equal(4, minted.Fingers[1]);
        Assert.Equal(3, ChordTemplateResolver.Resolve(rhythm, frets, ArpeggioFlavour.Plain, null, []).Fingers[1]);
    }

    [Fact]
    public void SuggestFingersFollowsTheSongsOtherArrangements()
    {
        var harness = new ChartEditHarness();
        harness.Project.Arrangements.Add(Part("Rhythm", PowerChord(0, 3, 4), PowerChord(1, 5, 4)));
        var arrangement = harness.Project.Arrangements[0];
        var bare = PowerChord(0, 8, null);
        arrangement.ChordTemplates.Add(bare);

        var plan = ChordShapeEdits.PlanSuggestFingers(arrangement, bare, harness.Editor);

        Assert.False(plan.IsRefused);
        Assert.True(harness.History.Do(plan.Action!));
        Assert.Equal(4, bare.Fingers[1]);
    }

    [Fact]
    public void SuggestFingersKeepsTheArrangementsOwnMajority()
    {
        var harness = new ChartEditHarness();
        harness.Project.Arrangements.Add(Part("Rhythm", PowerChord(0, 3, 4), PowerChord(1, 5, 4)));
        var arrangement = harness.Project.Arrangements[0];
        arrangement.ChordTemplates.Add(PowerChord(0, 1, 3));
        arrangement.ChordTemplates.Add(PowerChord(1, 6, 3));
        var bare = PowerChord(2, 8, null);
        arrangement.ChordTemplates.Add(bare);

        var plan = ChordShapeEdits.PlanSuggestFingers(arrangement, bare, harness.Editor);

        Assert.True(harness.History.Do(plan.Action!));
        Assert.Equal(3, bare.Fingers[1]);
    }

    private static Chord UnshapedPowerChord(int templateId)
    {
        var chord = new Chord { TemplateId = templateId };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 8, Sustain = new FractionalPosition(1) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 10, Sustain = new FractionalPosition(1) });
        return chord;
    }

    [Fact]
    public void HandShapeGenerationMintsThePowerChordTheSongFingers()
    {
        var (project, rhythm) = Song();
        rhythm.StringCount = 6;
        rhythm.Levels[0].Chords.Add(UnshapedPowerChord(-1));

        var plan = ArrangementHandShapeGenerator.Plan(rhythm, ArrangementFingeringStyle.OfSong(project, rhythm));

        Assert.Equal(4, Assert.Single(plan.MintedTemplates).Fingers[1]);
    }

    [Fact]
    public void AutoFixMintsThePowerChordTheSongFingers()
    {
        var (project, rhythm) = Song();
        rhythm.StringCount = 6;
        var chord = UnshapedPowerChord(99);
        rhythm.Levels[0].Chords.Add(chord);

        new ArrangementAutoFixService().Fix(rhythm, new ArrangementFixResult(), project);

        Assert.Equal(4, rhythm.FindChordTemplate(chord.TemplateId)!.Fingers[1]);
    }
}
