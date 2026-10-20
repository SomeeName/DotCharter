namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     Appending measures to the END of the beat grid.
///     <para>
///         RS-XML's last phrase is the END marker and may not contain notes, so
///         a chart whose final measure still plays has nowhere to put it. Every
///         tempo-map verb before this one added time at the START or in the
///         MIDDLE, so the only fixes by hand were dragging the last beat or
///         inserting at the playhead repeatedly — both of which move content
///         that was already correct.
///     </para>
///     <para>
///         The defining property is the one the other verbs cannot have:
///         appending shifts NOTHING. Nothing follows the last beat, so no note,
///         mute, anchor, phrase or voice may move.
///     </para>
/// </summary>
public sealed class AddMeasuresAtEndTests
{
    /// <summary>
    ///     ONE measure asked for is ONE new bar line on the ruler — the count the
    ///     charter can actually see. It read as TWO on first release: the run
    ///     ended on the bar line AFTER the new measure, so adding one measure to
    ///     a chart ending in M251 drew both M252 and M253.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void NMeasures_AddExactlyNBarLines(int measures)
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var before = project.Beats.Count;

        h.Maintenance.AddMeasuresAtEnd(measures);

        Assert.Equal(measures, project.Beats.Skip(before).Count(b => b.IsMeasure));
    }

    /// <summary>
    ///     The other half of that: the run STOPS on the last beat of the new
    ///     measure. The trailing downbeat is exactly what drew the second bar.
    /// </summary>
    [Fact]
    public void TheGridDoesNotEndOnATrailingBarLine()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;

        h.Maintenance.AddMeasuresAtEnd(1);

        Assert.False(project.Beats[^1].IsMeasure,
            "the run ended on the NEXT bar line, so one measure reads as two");
    }

    /// <summary>
    ///     …and the new measure is COMPLETE and empty: a bar line plus the rest
    ///     of its beats, not a lone marker with nothing behind it.
    /// </summary>
    [Fact]
    public void TheNewMeasureIsWholeAndEmpty()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var before = project.Beats.Count;

        h.Maintenance.AddMeasuresAtEnd(1);

        var barLine = -1;
        for (var i = before; i < project.Beats.Count; i++)
        {
            if (project.Beats[i].IsMeasure)
            {
                barLine = i;
                break;
            }
        }

        Assert.True(barLine >= 0, "no bar line was added");
        Assert.Equal(4, project.Beats.Count - barLine);

        var level = h.State.ActiveArrangement!.Levels[0];
        Assert.DoesNotContain(level.Notes, n => n.Position >= new FractionalPosition(barLine));
    }

    [Fact]
    public void ItAppendsAtTheGridsOwnSpacing()
    {
        var h = EndingOnABarLine();
        var project = h.State.ActiveProject!;
        var before = project.Beats.Count;
        var lastTime = project.Beats[^1].Time;

        h.Maintenance.AddMeasuresAtEnd(1);

        // The harness grid is 500 ms a beat, 4/4.
        for (var i = 1; i <= project.Beats.Count - before; i++)
            Assert.Equal(lastTime + 500 * i, project.Beats[before + i - 1].Time, 3);
    }

    [Fact]
    public void ItShiftsNothingThatWasAlreadyCharted()
    {
        // The whole reason this verb exists rather than "insert at playhead N times".
        var h = new ChartPowerToolsTests.Harness();
        var level = h.State.ActiveArrangement!.Levels[0];
        var positionsBefore = level.Notes.Select(n => n.Position).ToList();
        var firstBeatTime = h.State.ActiveProject!.Beats[0].Time;

        h.Maintenance.AddMeasuresAtEnd(2);

        Assert.Equal(positionsBefore, level.Notes.Select(n => n.Position).ToList());
        Assert.Equal(firstBeatTime, h.State.ActiveProject!.Beats[0].Time, 3);
    }

    [Fact]
    public void TheAppendedBeatsCarryTheTimeSignatureAndTempoOfTheLastBeat()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var before = project.Beats.Count;
        var last = project.Beats[^1];

        h.Maintenance.AddMeasuresAtEnd(1);

        foreach (var beat in project.Beats.Skip(before))
        {
            Assert.Equal(last.Bpm, beat.Bpm);
            Assert.Equal(last.BeatsInMeasure, beat.BeatsInMeasure);
            Assert.False(beat.IsAnchor);
        }
    }

    /// <summary>
    ///     A grid ending MID-measure is finished off first, and those filler beats
    ///     are FREE — they are not one of the measures asked for. Otherwise "add
    ///     one measure" leaves the END phrase sharing a bar with the last note,
    ///     which is the whole thing being fixed.
    ///     <para>
    ///         A grid whose last beat IS a bar line is the same case, not the
    ///         exception: that bar is open and EMPTY, so the next bar line is a
    ///         whole measure away.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    public void AGridEndingMidMeasure_IsFilledToTheBarLineFirst(int beatsIntoTheBar, int toNextBarLine)
    {
        var h = EndingOnABarLine();
        var project = h.State.ActiveProject!;
        for (var i = 0; i < beatsIntoTheBar; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = project.Beats[^1].Time + 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = false
            });
        }

        var before = project.Beats.Count;

        h.Maintenance.AddMeasuresAtEnd(1);

        // Reach the next bar line, then that measure's remaining three beats.
        Assert.Equal(before + toNextBarLine + 3, project.Beats.Count);
        Assert.Single(project.Beats.Skip(before), b => b.IsMeasure);
        Assert.True(project.Beats[before + toNextBarLine - 1].IsMeasure,
            "the filler did not land the grid on a bar line");
    }

    [Fact]
    public void OneUndoTakesTheWholeAppendBack()
    {
        var h = new ChartPowerToolsTests.Harness();
        var before = h.State.ActiveProject!.Beats.Count;

        h.Maintenance.AddMeasuresAtEnd(3);
        Assert.True(h.State.ActiveProject!.Beats.Count > before);

        h.History.Undo();

        Assert.Equal(before, h.State.ActiveProject!.Beats.Count);
    }

    [Fact]
    public void ALockedBeatMapRefuses()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.ActiveProject!.IsBeatMapLocked = true;
        var before = h.State.ActiveProject!.Beats.Count;

        h.Maintenance.AddMeasuresAtEnd(1);

        Assert.Equal(before, h.State.ActiveProject!.Beats.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ANonPositiveCount_DoesNothing(int measures)
    {
        var h = new ChartPowerToolsTests.Harness();
        var before = h.State.ActiveProject!.Beats.Count;

        h.Maintenance.AddMeasuresAtEnd(measures);

        Assert.Equal(before, h.State.ActiveProject!.Beats.Count);
    }

    /// <summary>
    ///     The spacing comes from the grid's own last gap, not from the printed
    ///     BPM: a hand-dragged tempo map disagrees with its BPM field, and a tail
    ///     grown from BPM would jump at the join.
    /// </summary>
    [Fact]
    public void TheSpacingFollowsTheGridsLastGap_NotTheBpmField()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        // Drag the last beat out to a 750 ms gap while Bpm still says 120.
        project.Beats[^1].Time = project.Beats[^2].Time + 750;
        var before = project.Beats.Count;
        var lastTime = project.Beats[^1].Time;

        h.Maintenance.AddMeasuresAtEnd(1);

        Assert.Equal(lastTime + 750, project.Beats[before].Time, 3);
    }

    /// <summary>
    ///     The shared harness grid is 32 beats of 4/4 — index 31 is the FOURTH
    ///     beat of its bar, so it ends three past a bar line. Trimming leaves the
    ///     grid finishing exactly ON one, which is the clean starting point for
    ///     the alignment theory above.
    /// </summary>
    private static ChartPowerToolsTests.Harness EndingOnABarLine()
    {
        var h = new ChartPowerToolsTests.Harness();
        var beats = h.State.ActiveProject!.Beats;
        while (!beats[^1].IsMeasure) beats.RemoveAt(beats.Count - 1);
        Assert.True(beats[^1].IsMeasure);
        return h;
    }
}
