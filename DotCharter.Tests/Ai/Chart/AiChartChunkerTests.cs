using DotCharter.Services.Ai.Chart;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     The AI chart pass is chunked by PHRASE with context either side (#497): every sound
///     is offered for change in exactly one chunk, and context is shown but never part of
///     a core.
/// </summary>
public sealed class AiChartChunkerTests
{
    private readonly AiChartFixture _f = new();

    [Fact]
    public void Each_phrase_is_a_chunk_that_starts_at_its_marker_and_ends_where_the_next_starts()
    {
        var chunks = _f.Chunks();

        Assert.Equal(2, chunks.Count);
        Assert.Equal(AiChartFixture.At(0), chunks[0].Start);
        Assert.Equal(AiChartFixture.At(16), chunks[0].End);
        Assert.Equal(AiChartFixture.At(16), chunks[1].Start);
        Assert.Null(chunks[1].End);
        Assert.Equal("intro", chunks[0].Phrases);
        Assert.Equal("verse", chunks[1].Phrases);
        Assert.All(chunks[0].Core, s => Assert.True(s.Position < AiChartFixture.At(16)));
        Assert.All(chunks[1].Core, s => Assert.True(s.Position >= AiChartFixture.At(16)));
    }

    [Fact]
    public void Every_sound_is_in_exactly_one_core_and_context_is_never_core()
    {
        foreach (var options in new[]
                 {
                     AiChartFixture.PerPhrase, AiChartChunkOptions.Default,
                     new AiChartChunkOptions(MinSounds: 1, MaxSounds: 3, ContextBefore: 2, ContextAfter: 4)
                 })
        {
            var chunks = _f.Chunks(options);
            var all = AiChartChunker.Sounds(_f.Level, _f.Beats).Select(s => s.Id).ToList();
            var cores = chunks.SelectMany(c => c.Core).Select(s => s.Id).ToList();
            Assert.Equal(all, cores);

            foreach (var chunk in chunks)
            {
                var core = chunk.Core.Select(s => s.Id).ToHashSet();
                Assert.DoesNotContain(chunk.Before, s => core.Contains(s.Id));
                Assert.DoesNotContain(chunk.After, s => core.Contains(s.Id));
                Assert.True(chunk.Before.Count <= options.ContextBefore);
                Assert.True(chunk.After.Count <= options.ContextAfter);
                Assert.All(chunk.Core, s => Assert.True(chunk.Contains(s.Position)));
                Assert.All(chunk.Before, s => Assert.False(chunk.Contains(s.Position)));
                Assert.All(chunk.After, s => Assert.False(chunk.Contains(s.Position)));
            }
        }
    }

    [Fact]
    public void Short_phrases_merge_and_a_long_phrase_is_cut_into_pieces_only_the_first_of_which_opens_it()
    {
        Assert.Single(_f.Chunks(AiChartChunkOptions.Default));

        var cut = _f.Chunks(new AiChartChunkOptions(MinSounds: 1, MaxSounds: 4, ContextBefore: 0, ContextAfter: 0));
        Assert.True(cut.Count >= 5);
        Assert.All(cut, c => Assert.True(c.Core.Count <= 4));
        // The intro's second piece starts at its own first sound, not at the phrase marker.
        Assert.Equal(AiChartFixture.At(0), cut[0].Start);
        Assert.Equal(cut[1].Core[0].Position, cut[1].Start);
        Assert.Contains(cut, c => c.Start == AiChartFixture.At(16));
    }

    [Fact]
    public void A_selection_limits_the_cores_and_stops_before_whatever_follows_it()
    {
        // Beats 10..14: the three power chords after the first, all inside the intro.
        var chunks = _f.Chunks(from: AiChartFixture.At(10), to: AiChartFixture.At(14));

        var chunk = Assert.Single(chunks);
        Assert.Equal(["s9", "s10", "s11"], chunk.Core.Select(s => s.Id));
        // The selection starts after the phrase marker: the chunk starts at its first sound.
        Assert.Equal(AiChartFixture.At(10), chunk.Start);
        // …and ends at the next phrase marker (beat 16), not at the end of the song.
        Assert.Equal(AiChartFixture.At(16), chunk.End);
        Assert.Equal(["s6", "s7", "s8"], chunk.Before.Select(s => s.Id));
        Assert.Equal(["s12", "s13", "s14"], chunk.After.Select(s => s.Id));
    }

    [Fact]
    public void An_empty_selection_has_no_chunks() =>
        Assert.Empty(_f.Chunks(from: AiChartFixture.At(30), to: AiChartFixture.At(31)));
}
