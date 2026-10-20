using DotCharter.Services.Editing.Fixing;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     Auto-Fix declares the tones the chart's own tone changes already name.
///     <para>
///         D01 ("tone change to 'X' is not declared on this arrangement") was
///         raised by validation and actionable by nothing: Auto-Fix had ten
///         fixers and none of them touched tones. Placing a tone change is the
///         natural way to author tones, so a charter ends up with changes to
///         "Clean" and "Overdrive", four empty slots, and six identical errors
///         stating a fact rather than a remedy — the owner read it as the app
///         inventing a concept ("tf are tone slots a to d").
///     </para>
///     <para>
///         The slots are real Rocksmith 2014: a base tone plus four switchable
///         ones, and the game cannot switch to a tone the arrangement does not
///         declare.
///     </para>
/// </summary>
public sealed class ToneSlotAutoFixTests
{
    private static Arrangement WithChanges(params string[] toneNames)
    {
        var arr = new Arrangement { Name = "Lead" };
        arr.Levels.Add(new ChartLevel());
        for (var i = 0; i < toneNames.Length; i++)
        {
            arr.ToneChanges.Add(new ToneChange
            {
                Position = new FractionalPosition(i + 1), ToneName = toneNames[i]
            });
        }

        return arr;
    }

    private static ArrangementFixResult Fix(Arrangement arr)
    {
        var result = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, result);
        return result;
    }

    /// <summary>The reported case: two named changes, four empty slots.</summary>
    [Fact]
    public void TheTonesAChangeNamesAreDeclaredInFreeSlots()
    {
        var arr = WithChanges("Clean", "Overdrive");

        Fix(arr);

        Assert.Equal("Clean", arr.ToneA);
        Assert.Equal("Overdrive", arr.ToneB);
    }

    /// <summary>Slot A is the tone the song reaches FIRST, not the first one the
    /// collection happens to hold.</summary>
    [Fact]
    public void SlotsFollowFirstUseOrderNotCollectionOrder()
    {
        var arr = new Arrangement { Name = "Lead" };
        arr.Levels.Add(new ChartLevel());
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(9), ToneName = "Late" });
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(2), ToneName = "Early" });

        Fix(arr);

        Assert.Equal("Early", arr.ToneA);
        Assert.Equal("Late", arr.ToneB);
    }

    [Fact]
    public void ARepeatedToneTakesOneSlot()
    {
        var arr = WithChanges("Clean", "Overdrive", "Clean", "Overdrive");

        Assert.Equal(2, Fix(arr).Changes["tonesDeclared"]);
        Assert.Equal("", arr.ToneC);
    }

    /// <summary>
    ///     A declared tone is an authoring decision and may own a body in the
    ///     Tone Designer, so overwriting one to silence an error would trade a
    ///     visible problem for an inaudible one.
    /// </summary>
    [Fact]
    public void AnOccupiedSlotIsNeverOverwritten()
    {
        var arr = WithChanges("Clean");
        arr.ToneA = "Acoustic";

        Fix(arr);

        Assert.Equal("Acoustic", arr.ToneA);
        Assert.Equal("Clean", arr.ToneB);
    }

    [Fact]
    public void AToneAlreadyDeclaredAnywhereIsLeftAlone()
    {
        var arr = WithChanges("Clean");
        arr.ToneBase = "Clean";

        Assert.Equal(0, Fix(arr).Changes.GetValueOrDefault("tonesDeclared")); // Add() drops zeroes
        Assert.Equal("", arr.ToneA);
    }

    /// <summary>Four slots is the format's limit, not a bug — the fifth distinct
    /// tone keeps its issue, because only the charter can decide what to merge.</summary>
    [Fact]
    public void PastFourDistinctTonesTheRestAreLeftFlagged()
    {
        var arr = WithChanges("One", "Two", "Three", "Four", "Five");

        Assert.Equal(4, Fix(arr).Changes["tonesDeclared"]);
        Assert.Equal("Four", arr.ToneD);

        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats());
        var remaining = issues.Where(i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone).ToList();
        Assert.Single(remaining);
        Assert.Contains("Five", Assert.Single(remaining).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnnamedChangeIsNotInvented()
    {
        var arr = WithChanges("");

        Assert.Equal(0, Fix(arr).Changes.GetValueOrDefault("tonesDeclared")); // Add() drops zeroes
        Assert.Equal("", arr.ToneA);
    }

    /// <summary>Auto-Fix clears the errors it was offered for — the whole point.</summary>
    [Fact]
    public void AfterTheFixTheChartNoLongerReportsUndeclaredTones()
    {
        var arr = WithChanges("Clean", "Overdrive", "Clean");
        var before = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats());
        Assert.Equal(3, before.Count(i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone));

        Fix(arr);

        var after = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats());
        Assert.DoesNotContain(after, i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);
    }

    // ---- the message -----------------------------------------------------

    /// <summary>
    ///     The message has to name a REMEDY. "has no matching tone declared on
    ///     the arrangement (starting tone, base or A–D slots)" stated the fault
    ///     twice and the cure never.
    /// </summary>
    [Fact]
    public void TheMessageSaysWhatToDoWhileSlotsAreFree()
    {
        var arr = WithChanges("Clean");

        var issue = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats())
            .First(i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);

        Assert.Contains("Tone designer", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Auto-fix", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>With every slot taken the remedy is different, and so is the
    /// sentence: Auto-Fix cannot help, and the charter must reuse or free one.</summary>
    [Fact]
    public void TheMessageChangesWhenNoSlotIsFree()
    {
        var arr = WithChanges("Fifth");
        arr.ToneA = "One";
        arr.ToneB = "Two";
        arr.ToneC = "Three";
        arr.ToneD = "Four";

        var issue = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats())
            .First(i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);

        Assert.DoesNotContain("Auto-fix", issue.Message, StringComparison.Ordinal);
        Assert.Contains("reuse", issue.Message, StringComparison.Ordinal);
        Assert.Contains("One", issue.Message, StringComparison.Ordinal);
    }

    private static List<BeatMarker> Beats()
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 32; i++) beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        return beats;
    }
}
