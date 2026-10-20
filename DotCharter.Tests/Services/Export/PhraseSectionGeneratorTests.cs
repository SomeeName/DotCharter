using DotCharter.Services.Export;
using DotCharter.Services.Export.Sng;

namespace DotCharter.Tests.Services.Export;

public sealed partial class PhraseSectionGeneratorTests
{
    // ---- tests -------------------------------------------------------------

    [Fact]
    public void Structureless_chart_gets_count_p0_end_with_riff_sections_and_trailing_noguitar()
    {
        var beats = CreateBeats();
        var arrangement = NewArrangement();
        // Continuous content from 2000 ms to 22000 ms (long enough for one body cut).
        for (var ms = 2000.0; ms <= 22000.0; ms += 500.0) AddNote(arrangement, beats, ms);

        var generated = PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 32000);

        Assert.True(generated);

        // Phrases: COUNT, p0, at least one body p#, END — in that framing.
        Assert.Equal("COUNT", arrangement.Phrases[0]);
        Assert.Equal("END", arrangement.Phrases[^1]);
        Assert.Contains("p0", arrangement.Phrases);
        Assert.Contains("p1", arrangement.Phrases);

        // Every iteration references a valid phrase; END is the last one in time.
        var iterations = IterationsByTime(arrangement, beats);
        Assert.Equal("COUNT", iterations[0].Phrase);
        Assert.Equal("END", iterations[^1].Phrase);

        // Sections use only the fixed riff/noguitar whitelist, and the trailing
        // section is the noguitar at the END time.
        var sections = Sections(arrangement, beats);
        Assert.All(sections, s => Assert.Contains(s.Section, new[] { "riff", "noguitar" }));
        Assert.Equal("riff", sections[0].Section);
        Assert.Equal("noguitar", sections[^1].Section);
        Assert.Equal(iterations[^1].TimeMs, sections[^1].TimeMs, Tolerance);

        // Content phrases carry a riff section at their iteration start.
        foreach (var (phrase, timeMs) in iterations.Where(i => i.Phrase is "p0" or "p1"))
            Assert.Contains(sections, s => s.Section == "riff" && Math.Abs(s.TimeMs - timeMs) <= Tolerance);

        // Structural phrases pinned to difficulty 0; body phrases carry no entry.
        Assert.Equal(0, arrangement.PhraseInfos["COUNT"].MaxDifficulty);
        Assert.Equal(0, arrangement.PhraseInfos["END"].MaxDifficulty);
        Assert.False(arrangement.PhraseInfos.ContainsKey("p0"));
    }

    [Fact]
    public void Long_chart_gets_body_phrases_with_at_least_minimum_separation()
    {
        var beats = CreateBeats(200); // 100 s of grid
        var arrangement = NewArrangement();
        // ~40 measures of continuous notes (0..80000 ms).
        for (var ms = 0.0; ms <= 80000.0; ms += 500.0) AddNote(arrangement, beats, ms);

        Assert.True(PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 100000));

        // Multiple content phrases were cut.
        var contentPhrases = arrangement.Phrases.Count(p => p.StartsWith('p'));
        Assert.True(contentPhrases >= 4, $"expected several body phrases, got {contentPhrases}");
        Assert.True(arrangement.Phrases.Count > 3);

        // Consecutive content-phrase starts respect the 2000 ms minimum.
        var contentStarts = IterationsByTime(arrangement, beats)
            .Where(i => i.Phrase.StartsWith('p'))
            .Select(i => i.TimeMs)
            .ToList();
        for (var i = 1; i < contentStarts.Count; i++)
        {
            Assert.True(contentStarts[i] - contentStarts[i - 1] >= 2000 - Tolerance,
                $"phrases {i - 1}->{i} only {contentStarts[i] - contentStarts[i - 1]} ms apart");
        }
    }

    [Fact]
    public void Content_gap_produces_a_noguitar_section_and_a_riff_resume()
    {
        var beats = CreateBeats();
        var arrangement = NewArrangement();
        // Content A (2000..8000), a 6 s silence, then content B (14000..18000).
        for (var ms = 2000.0; ms <= 8000.0; ms += 500.0) AddNote(arrangement, beats, ms);
        for (var ms = 14000.0; ms <= 18000.0; ms += 500.0) AddNote(arrangement, beats, ms);

        Assert.True(PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 32000));

        var sections = Sections(arrangement, beats);

        // The silence is cut off with a noguitar at the gap start (8000 ms) and
        // the guitar resumes with a riff at the next content (14000 ms).
        var noGuitar = sections.FirstOrDefault(s => s.Section == "noguitar" && Math.Abs(s.TimeMs - 8000) <= Tolerance);
        Assert.Equal("noguitar", noGuitar.Section);

        var resume = sections.FirstOrDefault(s => s.Section == "riff" && Math.Abs(s.TimeMs - 14000) <= Tolerance);
        Assert.Equal("riff", resume.Section);

        // The noguitar immediately precedes the riff resume in time.
        var ordered = sections.Select(s => s.TimeMs).ToList();
        var ngIndex = ordered.FindIndex(t => Math.Abs(t - 8000) <= Tolerance);
        var riffIndex = ordered.FindIndex(t => Math.Abs(t - 14000) <= Tolerance);
        Assert.Equal(ngIndex + 1, riffIndex);
    }

    [Fact]
    public void Phrase_cut_never_lands_inside_a_sustain()
    {
        var beats = CreateBeats();
        var arrangement = NewArrangement();
        // Continuous staccato content from 500 ms so the 9th measure boundary
        // lands at 18000 ms, plus one long sustain (17500..19500) crossing it.
        for (var ms = 500.0; ms <= 20500.0; ms += 1000.0) AddNote(arrangement, beats, ms);
        AddNote(arrangement, beats, 17500, stringIndex: 1, fret: 5, sustainMs: 2000);

        Assert.True(PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 32000));

        var contentStarts = IterationsByTime(arrangement, beats)
            .Where(i => i.Phrase.StartsWith('p'))
            .Select(i => i.TimeMs)
            .ToList();

        // No phrase cut sits strictly inside the sustain span.
        Assert.DoesNotContain(contentStarts, t => t is > (17500 + Tolerance) and < (19500 - Tolerance));

        // The 9-measure boundary at 18000 ms was nudged forward to the sustain
        // end (19500 ms) rather than cutting through it.
        var body = contentStarts.Where(t => t > 500 + Tolerance).ToList();
        Assert.NotEmpty(body);
        Assert.Equal(19500, body[0], Tolerance);
    }

    [Fact]
    public void Authored_structure_is_left_untouched()
    {
        var beats = CreateBeats();
        var arrangement = NewArrangement();
        arrangement.Phrases.AddRange(["COUNT", "verse", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 2000), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 28000), 2));
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 2000), Section = "verse" });
        for (var ms = 2000.0; ms <= 6000.0; ms += 500.0) AddNote(arrangement, beats, ms);

        var generated = PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 32000);

        Assert.False(generated);
        Assert.Equal(["COUNT", "verse", "END"], arrangement.Phrases);
        Assert.Equal(3, arrangement.PhraseIterations.Count);
        var section = Assert.Single(arrangement.EventPoints, ep => !string.IsNullOrEmpty(ep.Section));
        Assert.Equal("verse", section.Section);
    }

    [Fact]
    public void Empty_arrangement_does_nothing()
    {
        var beats = CreateBeats();
        var arrangement = NewArrangement(); // a level, but no notes/chords

        var generated = PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 32000);

        Assert.False(generated);
        Assert.Empty(arrangement.Phrases);
        Assert.Empty(arrangement.PhraseIterations);
        Assert.Empty(arrangement.EventPoints);
    }

    [Fact]
    public void Event_only_event_points_are_preserved_when_generating()
    {
        var beats = CreateBeats();
        var arrangement = NewArrangement();
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 0), Events = "TS:4/4" });
        for (var ms = 2000.0; ms <= 6000.0; ms += 500.0) AddNote(arrangement, beats, ms);

        Assert.True(PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 32000));

        // The TS marker survives; new section markers were added around it.
        Assert.Contains(arrangement.EventPoints, ep => ep.Events == "TS:4/4" && string.IsNullOrEmpty(ep.Section));
        Assert.Contains(arrangement.EventPoints, ep => ep.Section == "riff");
    }

    [Fact]
    public void Generated_structure_flows_through_the_sng_mapper()
    {
        var beats = CreateBeats(160);
        var arrangement = NewArrangement();
        for (var ms = 500.0; ms <= 60000.0; ms += 500.0) AddNote(arrangement, beats, ms);

        Assert.True(PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 70000));

        var build = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 70000);
        var sng = build.Sng;

        Assert.True(sng.Phrases.Count > 3, $"expected >3 phrases, got {sng.Phrases.Count}");
        Assert.Equal("COUNT", sng.Phrases[0].Name);
        Assert.Equal("END", sng.Phrases[^1].Name);
        Assert.Equal(arrangement.PhraseIterations.Count, sng.PhraseIterations.Count);
        Assert.Contains(sng.Sections, s => s.Name == "riff");
        Assert.Contains(sng.Sections, s => s.Name == "noguitar");
    }
}
