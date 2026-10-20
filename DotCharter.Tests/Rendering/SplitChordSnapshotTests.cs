using DotCharter.Rendering.Data.Render;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A chord split into notes (<c>Chord.SplitIntoNotes</c>) ships as single notes,
///     so the highway must know it: the snapshot carries
///     <see cref="ChordFlags.SplitIntoNotes" /> and the chord renderer then draws
///     single-note heads and tails with no box (#599, a Guitar Pro pick scrape).
///     The pixels are <c>HeadlessPickScrapeRenderTests</c>' (GPU-gated); this is the
///     data half, in the default gate.
/// </summary>
public sealed class SplitChordSnapshotTests
{
    [Fact]
    public void ASplitChord_IsFlaggedInTheSnapshot_AnOrdinaryOneIsNot()
    {
        var arrangement = PickScrapeScenes.Import(PickScrapeScenes.Gpif(PickScrapeScenes.Shinbatsu()));
        var plain = new Chord { Position = new FractionalPosition(10) };
        plain.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
        plain.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 9 });
        arrangement.Levels[0].Chords.Add(plain);
        Assert.Equal(4, arrangement.Levels[0].Chords.Count(GpPickScrape.IsScrapeChord));

        var snapshot = PartialChordSlideFixture.Snapshot(arrangement);

        var flags = snapshot.Chords.AsSpan(0, snapshot.ChordCount).ToArray()
            .Select(c => (c.Flags & ChordFlags.SplitIntoNotes) != 0).ToArray();
        Assert.Equal(new[] { true, true, true, true, false }, flags);
    }
}
