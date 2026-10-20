using DotCharter.Rendering.Data;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tests.Services.Parsing.GuitarPro;
using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The owner's partial chord slide (#474, #476) as the renderers receive it:
///     5 on the A string + 8 on the D string, the 5 slides to 6 while the 8 is
///     held, so 5+8 becomes 6+8, then a low-E 3. Imported through the REAL
///     <c>GpImporter</c> (<see cref="GpPartialChordSlideTests" />'s gpif fixture)
///     and projected by the REAL <see cref="FrameSnapshot.BuildSnapshot" />, the
///     way the app's two snapshot builders build it.
/// </summary>
internal static class PartialChordSlideFixture
{
    public const int AString = 1;
    public const int DString = 2;

    /// <summary>Beat 0 sits here, one beat per <see cref="BeatMs" />.</summary>
    public const double FirstBeatMs = 1000;

    /// <summary>60 bpm, so the one-beat slide is long enough to see.</summary>
    public const double BeatMs = 1000;

    /// <summary>The sliding chord (5+8).</summary>
    public const double OriginMs = FirstBeatMs;

    /// <summary>The landing shape (6+8), one beat later.</summary>
    public const double LandingMs = FirstBeatMs + BeatMs;

    public static Arrangement Import() => GpPartialChordSlideTests.ImportTiedHold("gpif");

    public static List<BeatMarker> Beats()
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 16; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = FirstBeatMs + i * BeatMs, Bpm = 60, IsMeasure = i % 4 == 0, BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        return beats;
    }

    /// <summary>The whole chart in one window.</summary>
    public static FrameSnapshot Snapshot(Arrangement? arrangement = null)
    {
        arrangement ??= Import();
        var level = arrangement.Levels[0];
        var beats = Beats();
        var ctx = new SnapshotUpdateContext
        {
            Arrangement = arrangement,
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveHandShapes = level.HandShapes,
            ActiveAnchors = level.Anchors,
            ActiveBeats = beats,
            ActiveTemplates = arrangement.ChordTemplates,
            RepeatChords = new RepeatChordLatch().Get(arrangement.ChordTemplates, level.Notes, level.Chords,
                level.HandShapes, beats, contentVersion: 0, nowMs: 0)
        };

        var snapshot = new FrameSnapshot { StringCount = 6 };
        FrameSnapshot.BuildSnapshot(0, beats[^1].Time, 0, snapshot, ctx, []);
        return snapshot;
    }
}
