using System.Xml.Linq;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     The FHP generator's minted fingerings survive a save and a reopen (#508):
///     <see cref="ChordTemplate.FollowedFingering" /> is written to and read from the
///     <c>.rscp</c>, a hand edit clears it, and a project saved before the field
///     existed loads with every fingering authored, as it always did.
/// </summary>
public sealed class FollowedFingeringPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-followed-" + Guid.NewGuid().ToString("N"));

    public FollowedFingeringPersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static ChartProject ImportLikeProject()
    {
        var project = new ChartProject();
        foreach (var beat in ChordFingeringFollowerTests.Beats()) project.Beats.Add(beat);
        var arrangement = ChordFingeringFollowerTests.ImportLikeArrangement();
        arrangement.Name = "Lead";
        project.Arrangements.Add(arrangement);
        return project;
    }

    private ChartProject SaveAndReopen(ChartProject project, Action<XDocument>? edit = null)
    {
        var path = Path.Combine(_dir, $"p{Guid.NewGuid():N}.rscp");
        new RscpChartSerializer().Save(path, project);
        if (edit != null)
        {
            var doc = XDocument.Load(path);
            edit(doc);
            doc.Save(path);
        }

        return new RscpChartSerializer().Load(path);
    }

    private static ChordTemplate TemplateOf(Arrangement arrangement, Chord chord) =>
        arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);

    private static void RemoveTheLineThatHeldTheHand(ChartLevel level)
    {
        foreach (var note in level.Notes.Where(n => n.Position < new FractionalPosition(2)).ToList())
            level.Notes.Remove(note);
    }

    // ─── .rscp ──────────────────────────────────────────────────────────────

    [Fact]
    public void Rscp_RoundTripsTheFlag_AndWritesNothingWhenItIsFalse()
    {
        var project = ImportLikeProject();
        var templates = project.Arrangements[0].ChordTemplates;
        var minted = templates[0].Clone();
        minted.Id = 1;
        minted.Fingers[3] = 2;
        minted.Fingers[4] = 3;
        minted.FollowedFingering = true;
        templates.Add(minted);

        string? writtenFor0 = null, writtenFor1 = null;
        var reloaded = SaveAndReopen(project, doc =>
        {
            var written = doc.Descendants("chordTemplate").ToList();
            writtenFor0 = written[0].Attribute("followed")?.Value;
            writtenFor1 = written[1].Attribute("followed")?.Value;
        }).Arrangements[0].ChordTemplates;

        Assert.Null(writtenFor0);
        Assert.Equal("T", writtenFor1);
        Assert.False(reloaded.Single(t => t.Id == 0).FollowedFingering);
        Assert.True(reloaded.Single(t => t.Id == 1).FollowedFingering);
    }

    // ─── Generate FHP across a reopen ───────────────────────────────────────

    /// <summary>
    ///     The issue's scenario: Generate FHP holds the hand at 8 and mints a
    ///     shifted variant; save; reopen; delete the line that held the hand;
    ///     Generate FHP again — the chord goes back to its base grip, index on 9.
    ///     Before #508 the reopened variant read as a charter's fingering, stayed,
    ///     and implied FHP 8.
    /// </summary>
    [Fact]
    public void GenerateFhp_Save_Reopen_RemoveTheLine_GenerateFhp_ReturnsToTheBaseGrip()
    {
        var project = ImportLikeProject();
        var baseFingers = new Dictionary<int, int>(project.Arrangements[0].ChordTemplates[0].Fingers);
        new ChordFingeringFollowerTests.Harness(project).Maintenance.GenerateFhp();
        var variant = TemplateOf(project.Arrangements[0], project.Arrangements[0].Levels[0].Chords[0]);
        Assert.True(variant.FollowedFingering);
        Assert.NotEqual(baseFingers, variant.Fingers);

        var reopened = SaveAndReopen(project);
        var arrangement = reopened.Arrangements[0];
        var level = arrangement.Levels[0];
        Assert.True(TemplateOf(arrangement, level.Chords[0]).FollowedFingering);

        RemoveTheLineThatHeldTheHand(level);
        new ChordFingeringFollowerTests.Harness(reopened).Maintenance.GenerateFhp();

        Assert.Equal("2:9 4:8", string.Join(" ", level.Anchors.OrderBy(a => a.Position)
            .Select(a => $"{a.Position.BeatId}:{a.Fret}")));
        Assert.Equal(baseFingers, TemplateOf(arrangement, level.Chords[0]).Fingers);
        Assert.Equal(level.Chords[0].TemplateId, level.HandShapes[0].TemplateId);
    }

    /// <summary>
    ///     The safe default, and the vacuity keeper for the test above: the SAME
    ///     file without the attribute — a project saved before #508 — reads the
    ///     variant as authored, so Generate FHP leaves its fingering alone and the
    ///     hand stays where that fingering puts it. A missing flag never lets the
    ///     generator move a fingering.
    /// </summary>
    [Fact]
    public void AProjectWithoutTheAttribute_KeepsTheShiftedFingering()
    {
        var project = ImportLikeProject();
        new ChordFingeringFollowerTests.Harness(project).Maintenance.GenerateFhp();
        var variantFingers = new Dictionary<int, int>(
            TemplateOf(project.Arrangements[0], project.Arrangements[0].Levels[0].Chords[0]).Fingers);

        var reopened = SaveAndReopen(project,
            doc => doc.Descendants("chordTemplate").Attributes("followed").Remove());
        var arrangement = reopened.Arrangements[0];
        var level = arrangement.Levels[0];
        Assert.All(arrangement.ChordTemplates, t => Assert.False(t.FollowedFingering));

        RemoveTheLineThatHeldTheHand(level);
        new ChordFingeringFollowerTests.Harness(reopened).Maintenance.GenerateFhp();

        Assert.Equal(variantFingers, TemplateOf(arrangement, level.Chords[0]).Fingers);
        Assert.Equal(8, level.Anchors.OrderBy(a => a.Position).First().Fret);
    }

    // ─── Hand edits outside the panel ───────────────────────────────────────

    /// <summary>The power-chord switch (#499 / #526) is a hand edit: it marks a
    /// minted power chord authored, and its undo puts the mark back.</summary>
    [Fact]
    public void ThePowerChordSwitch_MarksAMintedTemplateAuthored_AndUndoRestoresIt()
    {
        var project = new ChartProject();
        foreach (var beat in ChordFingeringFollowerTests.Beats()) project.Beats.Add(beat);
        var arrangement = new Arrangement { Levels = { new ChartLevel() } };
        project.Arrangements.Add(arrangement);
        var a5 = new ChordTemplate
        {
            Id = 0, Name = "A5", FollowedFingering = true,
            Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 },
            Fingers = new Dictionary<int, int> { [0] = 1, [1] = 3 }
        };
        arrangement.ChordTemplates.Add(a5);
        arrangement.Levels[0].Chords.Add(new Chord
        {
            TemplateId = 0,
            ChordNotes = { new ChordNote { StringIndex = 0, Fret = 5 }, new ChordNote { StringIndex = 1, Fret = 7 } }
        });
        var harness = new ChordFingeringFollowerTests.Harness(project);

        harness.Maintenance.TogglePowerChordFingering();

        Assert.Equal(4, a5.Fingers[1]);
        Assert.False(a5.FollowedFingering);

        harness.History.Undo();
        Assert.Equal(3, a5.Fingers[1]);
        Assert.True(a5.FollowedFingering);
    }

    /// <summary>A copy of the arrangement — the AI chart pass plans on one —
    /// judges a minted variant free exactly as the live chart does, with no
    /// carry-over step (the old session mark needed <c>CarryMintedMarks</c>).</summary>
    [Fact]
    public void AClonedArrangement_StillReadsTheVariantAsFree()
    {
        var project = ImportLikeProject();
        new ChordFingeringFollowerTests.Harness(project).Maintenance.GenerateFhp();
        var live = project.Arrangements[0];
        var copy = ChartProjectCloner.CloneArrangement(live);

        var liveVariant = TemplateOf(live, live.Levels[0].Chords[0]);
        var copiedVariant = TemplateOf(copy, copy.Levels[0].Chords[0]);

        Assert.True(ChordFingeringFollower.IsFreeFingering(liveVariant, 6));
        Assert.True(ChordFingeringFollower.IsFreeFingering(copiedVariant, 6));
    }
}
