using DotCharter.Services.Editing.ChordShapes;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     A bar without an authored ghost mask whose only sound is a fret-hand-muted chord (a pick
///     slide on a power-chord grip) showed every grip string ticked Ghost in the Shape page, while the
///     Rocksmith reader (<see cref="GhostGems.MarkUnplayedHandShapeFrets" />) infers no ghosts there.
/// </summary>
public sealed class GripInspectorMutedOpeningTests
{
    [Theory]
    [InlineData(ChordApplyRung.ThisChord)]
    [InlineData(ChordApplyRung.Selected)]
    [InlineData(ChordApplyRung.EveryUse)]
    public void AMutedOpening_ShowsNoGhosts_OnAnUnauthoredBar(ChordApplyRung rung)
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [0] = 12, [1] = 12 });
        var shape = rig.Shape(grip, 0, 2);
        var chord = Sound(rig, grip, MuteType.Full);

        rig.Ladder.ApplyRung = rung;
        rig.Select(SoundSelectionKind.Chord, [shape], [chord]);

        Assert.False(rig.Row(0).IsGhost);
        Assert.False(rig.Row(1).IsGhost);
        Assert.Equal(0, GhostGems.MarkUnplayedHandShapeFrets(rig.Arrangement));
        Assert.Null(shape.GhostMask);
    }

    [Fact]
    public void ACopyWithAnExplicitEmptyMask_AndAnUnauthoredCopy_ReadTheSame()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [0] = 12, [1] = 12 });
        var authored = rig.Shape(grip, 0, 2, ghost: 0);
        var chord = Sound(rig, grip, MuteType.Full);
        rig.Shape(grip, 4, 6);
        var later = Sound(rig, grip, MuteType.Full, at: 4);

        rig.Ladder.ApplyRung = ChordApplyRung.EveryUse;
        rig.Select(SoundSelectionKind.Chord, [authored], [chord, later]);

        Assert.False(rig.Row(0).IsGhost);
        Assert.False(rig.Row(1).IsGhost);
    }

    [Fact]
    public void APitchedOpening_StillInfersTheStringItDoesNotStrike()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [0] = 12, [1] = 12 });
        var shape = rig.Shape(grip, 0, 2);
        var chord = new Chord { Position = new(0), TemplateId = grip.Id };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 12, Sustain = new(1) });
        rig.Level.Chords.Add(chord);

        rig.Ladder.ApplyRung = ChordApplyRung.ThisChord;
        rig.Select(SoundSelectionKind.Chord, [shape], [chord]);

        Assert.True(rig.Row(0).IsGhost);
        Assert.False(rig.Row(1).IsGhost);
    }

    private static Chord Sound(GripInspectorRig rig, ChordTemplate grip, MuteType mute, int at = 0)
    {
        var chord = new Chord { Position = new(at), TemplateId = grip.Id };
        foreach (var (s, fret) in grip.Frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret, Mute = mute, Sustain = new(1) });
        rig.Level.Chords.Add(chord);
        return chord;
    }
}
