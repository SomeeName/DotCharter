using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     Grounding stage 2: retrieval, selection, and what actually reaches the
///     prompt.
///
///     <para>
///         Everything here runs against a FAKE library. The real one walks psarc
///         archives and needs a corpus; every decision worth pinning — which song
///         is the song, which of its tones answers the request, what an absent song
///         falls back to, and the flag that switches the lot off — lives in
///         <see cref="ToneAnchorRetriever" /> and the prompt builder, which a list
///         of records is enough to drive.
///     </para>
/// </summary>
public sealed class ToneAnchorRetrievalTests
{
    // ---- fakes and helpers ----------------------------------------------------

    private sealed class FakeLibrary(params LibrarySongTones[] songs) : IToneAnchorLibrary
    {
        public readonly List<(string? Title, string? Artist)> Lookups = [];
        public Exception? Throw;

        public Task<IReadOnlyList<LibrarySongTones>> LookupAsync(
            string? title, string? artist, CancellationToken ct)
        {
            Lookups.Add((title, artist));
            if (Throw != null) throw Throw;
            return Task.FromResult<IReadOnlyList<LibrarySongTones>>(songs);
        }
    }

    private static DcRigPreset Rig(params string[] modules)
        => new(modules.Select(m => new DcRigModule(
            m, new Dictionary<string, float> { ["gainDb"] = 3f })).ToList());

    private static LibrarySongTones Song(
        string title, string artist, int year, params (string Name, int Modules)[] tones)
        => new(title, artist, year,
            tones.Select(t => new LibraryToneCandidate(
                t.Name, Rig(Enumerable.Range(0, t.Modules).Select(i => "m" + i).ToArray()))).ToList());

    private static string Description(string presetId)
        => DspTonePresetLibrary.All.First(p => p.Id == presetId).Description;

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static ToneDesignRequest Request(
        string description, string? title = "Thunderstruck", string? artist = "AC/DC")
        => new(description, [], null, title, artist);

    // ---- the anchor -----------------------------------------------------------

    [Fact]
    public async Task TheSongsOwnTone_BecomesTheAnchor()
    {
        var library = new FakeLibrary(
            Song("Thunderstruck", "AC/DC", 1990, ("Rhythm", 5), ("Lead", 7)));

        var set = await ToneAnchorRetriever.RetrieveAsync(
            library, Request("something biting for the solo"), CancellationToken.None);

        Assert.NotNull(set.Anchor);
        Assert.Equal("Thunderstruck", set.Anchor.Title);
        Assert.Equal("AC/DC", set.Anchor.Artist);
        Assert.Equal("Lead", set.Anchor.ToneName);
        Assert.Empty(set.Analogies);
    }

    /// <summary>The prompt must NAME the song, not just carry its chain — a model
    /// handed an unattributed rig has no reason to treat it as this song's.</summary>
    [Fact]
    public async Task TheAnchorReachesThePrompt_AndNamesTheSong()
    {
        var library = new FakeLibrary(Song("Thunderstruck", "AC/DC", 1990, ("Lead", 6)));
        var request = Request("biting lead");

        var set = await ToneAnchorRetriever.RetrieveAsync(library, request, CancellationToken.None);
        var prompt = ToneDesignPromptBuilder.Build(request, set);
        var text = string.Join("\n", prompt.Messages.Select(m => m.Text));

        Assert.Contains("Thunderstruck", text, StringComparison.Ordinal);
        Assert.Contains("AC/DC", text, StringComparison.Ordinal);
        Assert.Contains("how Rocksmith rigged", text, StringComparison.Ordinal);
        Assert.Contains("Adapt it to the request rather than starting from nothing",
            text, StringComparison.Ordinal);
        // Never presented as an analogy when it is the song's own tone.
        Assert.DoesNotContain("Analogy —", text, StringComparison.Ordinal);
    }

    /// <summary>The anchor pays for itself: one generic few-shot steps aside, so the
    /// system prompt is the ANCHORED one and is SHORTER, not longer.</summary>
    [Fact]
    public async Task AnAnchoredRequest_DropsOneGenericFewShot()
    {
        var library = new FakeLibrary(Song("Thunderstruck", "AC/DC", 1990, ("Lead", 6)));
        var request = Request("biting lead");

        var set = await ToneAnchorRetriever.RetrieveAsync(library, request, CancellationToken.None);
        var prompt = ToneDesignPromptBuilder.Build(request, set);

        Assert.Equal(ToneDesignPromptBuilder.AnchoredSystemPrompt, prompt.System);
        Assert.True(prompt.System.Length < ToneDesignPromptBuilder.SystemPrompt.Length);
        // Exactly one example fewer. Counted off the rendered block, because the
        // preset ID never appears in it — the examples are rendered as a request
        // line plus draft JSON.
        Assert.Equal(
            Occurrences(ToneDesignPromptBuilder.FewShotBlock, "Request: ") - 1,
            Occurrences(ToneDesignPromptBuilder.AnchoredFewShotBlock, "Request: "));
        Assert.Equal(4, Occurrences(ToneDesignPromptBuilder.FewShotBlock, "Request: "));
        // …and it is the RIGHT one. The bass example must survive — it is the one
        // that keeps a bass request off the guitar amp — and crunch-plexi is the
        // one that goes. Matched on the rendered DESCRIPTION, which is what the
        // block actually carries.
        Assert.Contains(Description("gear-bass-di"),
            ToneDesignPromptBuilder.AnchoredFewShotBlock, StringComparison.Ordinal);
        Assert.Contains(Description(ToneDesignPromptBuilder.AnchorReplacesFewShotId),
            ToneDesignPromptBuilder.FewShotBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(Description(ToneDesignPromptBuilder.AnchorReplacesFewShotId),
            ToneDesignPromptBuilder.AnchoredFewShotBlock, StringComparison.Ordinal);
    }

    /// <summary>The whole point of the budget: an anchored prompt must still fit.
    /// The system prompt is the cacheable prefix and the anchor rides on the message
    /// side, so both halves are counted.</summary>
    [Fact]
    public async Task AnAnchoredPrompt_FitsTheTokenBudget()
    {
        var library = new FakeLibrary(Song("Thunderstruck", "AC/DC", 1990, ("Lead", 12)));
        var request = Request("biting lead");

        var set = await ToneAnchorRetriever.RetrieveAsync(library, request, CancellationToken.None);
        var prompt = ToneDesignPromptBuilder.Build(request, set);
        var tokens = ToneDesignPromptBuilder.EstimateTokens(prompt.System) +
                     prompt.Messages.Sum(m => ToneDesignPromptBuilder.EstimateTokens(m.Text));

        Assert.True(tokens < 12_000, $"anchored prompt estimated at ~{tokens} tokens.");
    }

    // ---- role selection -------------------------------------------------------

    [Theory]
    [InlineData("a warm clean for the verses", "Clean")]
    [InlineData("biting lead for the solo", "Lead")]
    [InlineData("chunky rhythm", "Rhythm")]
    [InlineData("growly bass", "Bass")]
    public async Task TheRequestedRole_PicksTheMatchingTone(string description, string expected)
    {
        var library = new FakeLibrary(Song("Thunderstruck", "AC/DC", 1990,
            ("Clean", 3), ("Lead", 4), ("Rhythm", 5), ("Bass", 2)));

        var set = await ToneAnchorRetriever.RetrieveAsync(
            library, Request(description), CancellationToken.None);

        Assert.Equal(expected, set.Anchor?.ToneName);
    }

    /// <summary>"clean bass" is a BASS request. Answering it with the guitar's clean
    /// tone is the one mistake that anchors the chain on the wrong amp — the
    /// prompt's own rule 6 exists for it.</summary>
    [Fact]
    public void Bass_WinsOverEveryOtherRoleWordInTheSameSentence()
        => Assert.Equal(ToneAnchorRole.Bass, ToneAnchorRetriever.RoleOf("a clean bass for the lead break"));

    /// <summary>"Bassy" is a description of a guitar tone, not a bass request.</summary>
    [Fact]
    public void ARoleWordInsideAnotherWord_IsNotARole()
    {
        Assert.Equal(ToneAnchorRole.Unknown, ToneAnchorRetriever.RoleOf("warm and bassy"));
        Assert.Equal(ToneAnchorRole.Unknown, ToneAnchorRetriever.RoleOf("good cleanup when I roll back"));
    }

    /// <summary>No role named, so the RICHEST chain wins — the longest one carries
    /// the most of what made the song sound like itself.</summary>
    [Fact]
    public async Task WithNoRoleInTheRequest_TheRichestToneIsTaken()
    {
        var library = new FakeLibrary(Song("Thunderstruck", "AC/DC", 1990,
            ("Tone A", 2), ("Tone B", 9), ("Tone C", 4)));

        var set = await ToneAnchorRetriever.RetrieveAsync(
            library, Request("something that sounds like the record"), CancellationToken.None);

        Assert.Equal("Tone B", set.Anchor?.ToneName);
    }

    // ---- analogies ------------------------------------------------------------

    [Fact]
    public async Task ASongTheLibraryLacks_FallsBackToTwoAnalogies()
    {
        var library = new FakeLibrary(
            Song("Back in Black", "AC/DC", 1980, ("Rhythm", 5)),
            Song("Highway to Hell", "AC/DC", 1979, ("Rhythm", 4)),
            Song("Hells Bells", "AC/DC", 1980, ("Rhythm", 6)));

        var request = Request("crunchy rhythm", title: "Thunderstruck");
        var set = await ToneAnchorRetriever.RetrieveAsync(library, request, CancellationToken.None);

        Assert.Null(set.Anchor);
        Assert.Equal(2, set.Analogies.Count);

        var text = string.Join("\n", ToneDesignPromptBuilder.Build(request, set)
            .Messages.Select(m => m.Text));
        Assert.Contains("Analogy —", text, StringComparison.Ordinal);
        Assert.Contains("NOT in the player's library", text, StringComparison.Ordinal);
        Assert.Contains("never describe them as the requested song's own tone",
            text, StringComparison.Ordinal);
        // Analogies do NOT buy a few-shot: they are a weaker signal than a generic
        // example is a teacher of the format.
        Assert.Equal(ToneDesignPromptBuilder.SystemPrompt,
            ToneDesignPromptBuilder.Build(request, set).System);
    }

    /// <summary>A song that is neither the same artist nor the same era is not an
    /// analogy, it is a random song — and one labelled "nearby" teaches the model
    /// something false about what was asked for.</summary>
    [Fact]
    public async Task AUnrelatedSong_IsNotOfferedAsAnAnalogy()
    {
        var library = new FakeLibrary(
            Song("Back in Black", "AC/DC", 1980, ("Rhythm", 5)),
            Song("Reptilia", "The Strokes", 2003, ("Rhythm", 5)));

        var set = await ToneAnchorRetriever.RetrieveAsync(
            library, Request("crunchy rhythm", title: "Thunderstruck"), CancellationToken.None);

        var analogy = Assert.Single(set.Analogies);
        Assert.Equal("AC/DC", analogy.Artist);
    }

    [Fact]
    public async Task AnEmptyLibrary_GroundsNothing()
    {
        var set = await ToneAnchorRetriever.RetrieveAsync(
            new FakeLibrary(), Request("anything"), CancellationToken.None);

        Assert.False(set.HasAny);
        Assert.Equal(string.Empty, ToneDesignPromptBuilder.RenderAnchors(set));
    }

    /// <summary>Title AND artist. A title alone matches a dozen covers, and an
    /// anchor from the wrong recording is worse than none — the model defends it in
    /// its rationale.</summary>
    [Fact]
    public async Task ARequestWithNoArtist_NeverAsksTheLibrary()
    {
        var library = new FakeLibrary(Song("Thunderstruck", "AC/DC", 1990, ("Lead", 6)));

        var set = await ToneAnchorRetriever.RetrieveAsync(
            library, Request("biting lead", artist: null), CancellationToken.None);

        Assert.False(set.HasAny);
        Assert.Empty(library.Lookups);
    }

    /// <summary>Punctuation and case differ between a chart and a library all the
    /// time ("AC/DC" vs "AC DC", "Don't" vs "Dont"); a match that failed on those
    /// would make the anchor a coin flip.</summary>
    [Fact]
    public async Task MatchingIgnoresCaseAndPunctuation()
    {
        var library = new FakeLibrary(Song("T.N.T.", "AC/DC", 1975, ("Rhythm", 5)));

        var set = await ToneAnchorRetriever.RetrieveAsync(
            library, Request("crunchy rhythm", "tnt", "ac dc"), CancellationToken.None);

        Assert.NotNull(set.Anchor);
        Assert.Equal("T.N.T.", set.Anchor.Title);
    }

    /// <summary>A grounding stage that cannot run leaves the request UNGROUNDED; it
    /// does not fail it. A library throwing must not cost the user their design.</summary>
    [Fact]
    public async Task AThrowingLibrary_IsJustNoAnchor()
    {
        var library = new FakeLibrary(Song("Thunderstruck", "AC/DC", 1990, ("Lead", 6)))
        {
            Throw = new IOException("the psarc is locked")
        };

        var set = await ToneAnchorRetriever.RetrieveAsync(
            library, Request("biting lead"), CancellationToken.None);

        Assert.False(set.HasAny);
    }
}
