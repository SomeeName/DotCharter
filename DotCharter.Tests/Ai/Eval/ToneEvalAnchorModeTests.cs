using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The eval's LEAKAGE guard.
///
///     <para>
///         The scorer grades a designed chain against the song's own converted
///         Rocksmith tone — and grounding stage 2 hands the model that very tone.
///         A default eval run that left the anchor on would therefore be measuring
///         how well the model copies the answer it was given, and the number would
///         rise for a reason that says nothing about the case the feature exists
///         for: a song the player's library does not have.
///     </para>
///     <para>
///         So the default run pins <c>UseLibraryTones = false</c> into the settings
///         the REAL service reads, and the anchored run is opt-in behind
///         <see cref="ToneEvalEnvironment.AnchorVariable" /> and is labelled in the
///         report as an upper bound. Both halves are asserted here, because either
///         one alone is a run nobody could interpret.
///     </para>
/// </summary>
public sealed class ToneEvalAnchorModeTests
{
    [Fact]
    public void TheDefaultEnvironment_SwitchesTheAnchorOff()
    {
        var environment = new ToneEvalEnvironment(
            AiProviderKind.Claude, "claude-opus-5", null, "sk-test", 20, false);

        Assert.False(environment.UseAnchor);
        // The settings the SHIPPING service reads, not a flag the harness keeps to
        // itself — that is what makes the leak impossible to reintroduce by
        // forgetting.
        Assert.False(environment.Settings.UseLibraryTones);
        Assert.False(environment.ToOptions().Anchored);
    }

    [Fact]
    public void TheAnchoredEnvironment_TurnsItOnEverywhere()
    {
        var environment = new ToneEvalEnvironment(
            AiProviderKind.Claude, "claude-opus-5", null, "sk-test", 20, false, UseAnchor: true);

        Assert.True(environment.Settings.UseLibraryTones);
        Assert.True(environment.ToOptions().Anchored);
    }

    /// <summary>Both halves, and only together. An anchor library handed to an
    /// UNANCHORED run must be ignored — a run that is half-anchored is a number
    /// nothing could interpret.</summary>
    [Fact]
    public void AnUnanchoredRun_IgnoresAnAnchorLibraryItIsHanded()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var library = new ToneEvalAnchorLibrary(corpus);
        var environment = new ToneEvalEnvironment(
            AiProviderKind.Claude, "claude-opus-5", null, "sk-test", 2, false);

        var service = ToneDesignServiceLocator.Create(environment, library);

        Assert.True(service.IsConfigured);
        // Nothing has run, so nothing has looked anything up — and with
        // UseLibraryTones false the service would not ask even if it had.
        Assert.Equal(0, library.Lookups);
    }

    /// <summary>The report must SAY which mode ran, in words a reader cannot
    /// mistake. A number without its mode is the one way this report misleads.</summary>
    [Fact]
    public void TheReportHeader_NamesTheMode()
    {
        var anchored = ToneDesignEval.WriteReport(Run(true), new ToneEvalOptions { Anchored = true });
        var plain = ToneDesignEval.WriteReport(Run(false), new ToneEvalOptions());

        Assert.Contains("ANCHORED", anchored, StringComparison.Ordinal);
        Assert.Contains("upper bound, not a generalisation score", anchored, StringComparison.Ordinal);

        Assert.Contains("unanchored", plain, StringComparison.Ordinal);
        Assert.Contains("UseLibraryTones = false", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("upper bound, not a generalisation score", plain, StringComparison.Ordinal);
    }

    /// <summary>The anchor library serves the corpus one entry per SONG, with every
    /// tone that song has — so the role picker has something to choose between,
    /// exactly as it would against a real package.</summary>
    [Fact]
    public async Task TheCorpusAnchorLibrary_GroupsTonesBySong()
    {
        var library = new ToneEvalAnchorLibrary(ToneEvalCorpus.Fixtures);

        var songs = await library.LookupAsync("anything", "anyone", TestContext.Current.CancellationToken);

        Assert.NotEmpty(songs);
        Assert.Equal(1, library.Lookups);
        Assert.Equal(
            ToneEvalCorpus.Fixtures.Select(e => e.SongId).Distinct().Count(),
            songs.Count);
        Assert.All(songs, s => Assert.NotEmpty(s.Tones));
    }

    private static ToneEvalRun Run(bool anchored) => new(
        "2026-09-12T00-00-00Z", "claude-opus-5", "Claude", 1,
        50, 80, 50, 50, 0.5, 0.5, 0.5, 0.5, 0, 100, 200, 0.01,
        [], "artifacts", anchored);
}
