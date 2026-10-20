namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     Covers both halves of <see cref="ToneEvalCorpus" />: the committed fixtures
///     (default gate, always) and the local corpus (skipped when it is absent, so
///     the darkness shows instead of passing vacuously).
/// </summary>
public sealed class ToneEvalCorpusTests
{
    // ------------------------------------------------------------------ fixtures

    [Fact]
    public void TheFixtureCorpus_HasFiveConvertedEntries()
    {
        var fixtures = ToneEvalCorpus.Fixtures;

        Assert.Equal(5, fixtures.Count);
        Assert.All(fixtures, e =>
        {
            Assert.NotEmpty(e.SongTitle);
            Assert.NotEmpty(e.Artist);
            Assert.NotEmpty(e.ToneName);
            Assert.NotEmpty(e.ToneJson);
            Assert.NotEmpty(e.Truth.Modules);
            Assert.Equal("fixture", e.Source);
        });
    }

    [Fact]
    public void TheFixtureCorpus_ReachesEveryScoredFamilyTheScorerGrades()
    {
        // A fixture corpus that only ever contains amp + cab + drive cannot catch a
        // scorer bug in the modulation, time or dynamics arms — which is exactly
        // why two of the five fixtures are hand-written rather than RS defaults.
        var families = ToneEvalCorpus.Fixtures
            .SelectMany(e => ToneDesignScorer.FamilySequence(
                ToneDesignScorer.Canonicalize(e.Truth).Modules.Select(m => m.ModuleTypeId)))
            .ToHashSet();

        Assert.Contains(ToneFamily.Amp, families);
        Assert.Contains(ToneFamily.Cab, families);
        Assert.Contains(ToneFamily.Drive, families);
        Assert.Contains(ToneFamily.Modulation, families);
        Assert.Contains(ToneFamily.Delay, families);
        Assert.Contains(ToneFamily.Reverb, families);
        Assert.Contains(ToneFamily.Eq, families);
        Assert.True(families.Count >= 6,
            "the fixture corpus collapsed to " + families.Count + " families: " +
            string.Join(", ", families));
    }

    [Fact]
    public void TheFixtureCorpus_IsStableAcrossCalls()
    {
        // The eval diffs runs against each other; a corpus whose row set or order
        // moves makes every diff meaningless.
        Assert.Equal(
            ToneEvalCorpus.Fixtures.Select(e => e.Id),
            ToneEvalCorpus.Fixtures.Select(e => e.Id));
    }

    [Theory]
    [InlineData("Bass", "bass")]
    [InlineData("Clean2", "clean")]
    [InlineData("CleanLead", "clean")]
    [InlineData("Lead", "lead")]
    [InlineData("Octave_Solo", "lead")]
    [InlineData("Overdrive", "rhythm")]
    [InlineData("Distortion", "rhythm")]
    [InlineData("Rhyhtm", "rhythm")] // the corpus really does carry this typo
    [InlineData("Phaser", "lead")]
    public void TheRole_FoldsOutOfTheToneName(string toneName, string expected)
    {
        var entry = ToneEvalCorpus.Fixtures[0] with { ToneName = toneName };
        Assert.Equal(expected, entry.Role);
    }

    [Fact]
    public void BothPromptShapes_NameTheSong()
    {
        var entry = ToneEvalCorpus.Fixtures[0];

        Assert.Contains(entry.SongTitle, entry.Prompt, StringComparison.Ordinal);
        Assert.Contains(entry.Artist, entry.Prompt, StringComparison.Ordinal);
        Assert.Contains(entry.ToneName, entry.Prompt, StringComparison.Ordinal);

        Assert.Contains(entry.SongTitle, entry.ShortPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(entry.Artist, entry.ShortPrompt, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------- local

    [Fact]
    public void TheLocalCorpus_YieldsAtLeastTenDeduplicatedSongs()
    {
        Assert.SkipWhen(ToneEvalCorpus.ResolveCorpusRoot() is null, ToneEvalCorpus.MissingCorpus);

        var entries = ToneEvalCorpus.LoadLocal();
        Assert.SkipWhen(entries.Count == 0, ToneEvalCorpus.MissingCorpus);

        Assert.True(entries.Count >= 10,
            $"the local corpus yielded only {entries.Count} songs from " +
            $"{ToneEvalCorpus.ResolveCorpusRoot()} — the eval needs at least 10 to mean anything.");
        Assert.True(entries.Count <= ToneEvalCorpus.DefaultLimit);

        // De-duplicated by SONG: one tone each, never two rows for one title.
        Assert.Equal(entries.Count, entries.Select(e => e.SongId).Distinct().Count());

        // Every row converted to something real — TryBuild drops the rest.
        Assert.All(entries, e =>
        {
            Assert.NotEmpty(e.SongTitle);
            Assert.NotEmpty(e.Artist);
            Assert.NotEmpty(ToneDesignScorer.Canonicalize(e.Truth).Modules);
            Assert.True(e.Source is "rs2dlc" or "psarc", $"unexpected source '{e.Source}'");
        });
    }

    [Fact]
    public void TheLocalCorpus_LoadsInAStableOrderAndHonoursItsLimit()
    {
        Assert.SkipWhen(ToneEvalCorpus.ResolveCorpusRoot() is null, ToneEvalCorpus.MissingCorpus);

        var first = ToneEvalCorpus.LoadLocal();
        Assert.SkipWhen(first.Count == 0, ToneEvalCorpus.MissingCorpus);

        var second = ToneEvalCorpus.LoadLocal();
        Assert.Equal(first.Select(e => e.Id), second.Select(e => e.Id));

        var capped = ToneEvalCorpus.LoadLocal(3);
        Assert.Equal(Math.Min(3, first.Count), capped.Count);
        Assert.Equal(first.Take(capped.Count).Select(e => e.Id), capped.Select(e => e.Id));
    }

    [Fact]
    public void TheLocalCorpus_ScoresItselfAtOneHundred()
    {
        // The same anchor the fixtures carry, run over the REAL rows — it is the
        // only thing that proves the corpus's truth and the scorer's canonical form
        // agree on chains nobody hand-wrote.
        Assert.SkipWhen(ToneEvalCorpus.ResolveCorpusRoot() is null, ToneEvalCorpus.MissingCorpus);

        var entries = ToneEvalCorpus.LoadLocal();
        Assert.SkipWhen(entries.Count == 0, ToneEvalCorpus.MissingCorpus);

        foreach (var entry in entries)
            Assert.Equal(100.0, ToneDesignScorer.Score(entry.Truth, entry.Truth).Total, 6);
    }

    [Fact]
    public void AnAbsentCorpusRoot_IsAnEmptyLoadAndNotAThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(),
            "dotcharter-ai-eval-missing-" + Guid.NewGuid().ToString("N"));

        var previous = Environment.GetEnvironmentVariable(ToneEvalCorpus.CorpusRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(ToneEvalCorpus.CorpusRootVariable, missing);
            Assert.Null(ToneEvalCorpus.ResolveCorpusRoot());
            Assert.False(ToneEvalCorpus.TryLoadLocal(out var entries));
            Assert.Empty(entries);
        }
        finally
        {
            // Restored on the FAILURE path too: this variable is process-wide.
            Environment.SetEnvironmentVariable(ToneEvalCorpus.CorpusRootVariable, previous);
        }
    }
}
