using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     <c>Ignore</c> has two authors, and the enforcer runs on every save.
///     <list type="bullet">
///         <item>
///             It must RETRACT its own flag when the reason goes — a note
///             dragged off fret 24 kept an invisible <c>Ignore</c> and stayed
///             unscored forever.
///         </item>
///         <item>
///             It must NOT retract the user's — <c>Ignore</c> is authorable
///             from the inspector chip and the highway toggle, and an
///             unconditional re-derivation discarded that on the next save.
///         </item>
///     </list>
///     <c>IgnoreDerived</c> is the discriminator, and it has to survive a save
///     or the first reload turns a derived flag into an apparently-authored one
///     and the first bug comes back.
/// </summary>
public sealed class IgnoreProvenanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_ignore_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static (Arrangement Arrangement, ChartLevel Level) NewChart()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        return (arrangement, level);
    }

    private static AppConfig Config() => new()
    {
        MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction,
        MinNoteDistanceDivisor = 64
    };

    private static List<BeatMarker> Beats()
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 16; i++) beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        return beats;
    }

    [Fact]
    public void AnUnplayableFretIsIgnored_AndTheFlagIsMarkedDerived()
    {
        var (arrangement, level) = NewChart();
        level.Notes.Add(new GuitarNote { Fret = 24, StringIndex = 0 });

        ChartSpacingEnforcer.EnforceArrangement(arrangement, Beats(), Config());

        var note = level.Notes.Single();
        Assert.True(note.Ignore);
        Assert.True(note.IgnoreDerived);
    }

    [Fact]
    public void MovingTheNoteBackIntoRangeRetractsTheDerivedFlag()
    {
        var (arrangement, level) = NewChart();
        var note = new GuitarNote { Fret = 24, StringIndex = 0 };
        level.Notes.Add(note);
        var beats = Beats();

        ChartSpacingEnforcer.EnforceArrangement(arrangement, beats, Config());
        Assert.True(note.Ignore);

        note.Fret = 5;
        ChartSpacingEnforcer.EnforceArrangement(arrangement, beats, Config());

        Assert.False(note.Ignore);
        Assert.False(note.IgnoreDerived);
    }

    [Fact]
    public void AUserAuthoredIgnoreSurvivesEveryEnforcePass()
    {
        // The regression this discriminator exists to prevent: the two-way
        // derivation would otherwise clear a flag the user set by hand.
        var (arrangement, level) = NewChart();
        var note = new GuitarNote { Fret = 5, StringIndex = 0, Ignore = true };
        level.Notes.Add(note);
        var beats = Beats();

        for (var i = 0; i < 3; i++)
            ChartSpacingEnforcer.EnforceArrangement(arrangement, beats, Config());

        Assert.True(note.Ignore);
        Assert.False(note.IgnoreDerived);
    }

    [Fact]
    public void AUserAuthoredChordIgnoreSurvivesToo()
    {
        var (arrangement, level) = NewChart();
        var chord = new Chord { TemplateId = -1, Ignore = true };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);

        ChartSpacingEnforcer.EnforceArrangement(arrangement, Beats(), Config());

        Assert.True(chord.Ignore);
        Assert.False(chord.IgnoreDerived);
    }

    [Fact]
    public void ProvenanceSurvivesAnRscpRoundTrip()
    {
        // Without this the first reload makes a DERIVED flag look authored, and
        // the stuck-Ignore bug returns on the very next edit.
        var project = new ChartProject();
        var (arrangement, level) = NewChart();
        level.Notes.Add(new GuitarNote { Fret = 24, StringIndex = 0, Ignore = true, IgnoreDerived = true });
        level.Notes.Add(new GuitarNote { Fret = 5, StringIndex = 1, Ignore = true });
        project.Arrangements.Add(arrangement);

        var path = Path.Combine(_dir, "project.rscp");
        new RscpChartSerializer().Save(path, project);
        var loaded = new RscpChartSerializer().Load(path).Arrangements[0].Levels[0];

        var derived = loaded.Notes.Single(n => n.Fret == 24);
        var authored = loaded.Notes.Single(n => n.Fret == 5);

        Assert.True(derived.Ignore);
        Assert.True(derived.IgnoreDerived);
        Assert.True(authored.Ignore);
        Assert.False(authored.IgnoreDerived);
    }

    [Fact]
    public void ChartsWithoutDerivedFlagsWriteNoNewAttribute()
    {
        // Byte-identical guarantee: an ordinary chart must not grow markup.
        var project = new ChartProject();
        var (arrangement, level) = NewChart();
        level.Notes.Add(new GuitarNote { Fret = 5, StringIndex = 0 });
        project.Arrangements.Add(arrangement);

        var path = Path.Combine(_dir, "plain.rscp");
        new RscpChartSerializer().Save(path, project);

        Assert.DoesNotContain("ignDer", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
