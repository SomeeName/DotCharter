using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     Grounding stage 2 for the ANCHORED eval mode, served out of the corpus that
///     is already loaded.
///
///     <para>
///         <b>This deliberately hands the model the ground truth.</b> Each entry's
///         <see cref="ToneEvalEntry.Truth" /> is the converted Rocksmith tone the
///         scorer grades against, so an anchored run is measuring how well the
///         pipeline COPIES a reference it was given. That is the point of the mode
///         and it is why <see cref="ToneEvalEnvironment.AnchorVariable" /> is opt-in
///         and the report calls the number an upper bound.
///     </para>
///     <para>
///         It is served from the corpus rather than by pointing
///         the app's library tone anchors at the library for a reason worth stating:
///         going through the real library would make the anchored number depend on
///         whether the library walk happened to FIND each scored song, so a retrieval
///         miss would read as a model failure. An upper bound has to assume perfect
///         retrieval, or it is not a bound.
///     </para>
/// </summary>
public sealed class ToneEvalAnchorLibrary : IToneAnchorLibrary
{
    private readonly List<LibrarySongTones> _songs;

    /// <summary>Builds the library over <paramref name="corpus" />. One
    /// <see cref="LibrarySongTones" /> per SONG, carrying every tone the corpus has
    /// for it — so the role picker has something to choose between, exactly as it
    /// would against a real package.</summary>
    public ToneEvalAnchorLibrary(IReadOnlyList<ToneEvalEntry> corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        _songs = corpus
            .GroupBy(e => e.SongId, StringComparer.Ordinal)
            .Select(g => new LibrarySongTones(
                g.First().SongTitle,
                g.First().Artist,
                // The corpus does not carry a year, and 0 means "unknown" — which
                // switches the era fallback off rather than inventing a decade.
                0,
                g.Select(e => new LibraryToneCandidate(e.ToneName, e.Truth)).ToList()))
            .ToList();
    }

    /// <summary>How many lookups have been served. The offline guard asserts on
    /// this: a mode that claims to be anchored and never reads the library is the
    /// failure that would make an anchored number silently an unanchored one.</summary>
    public int Lookups { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<LibrarySongTones>> LookupAsync(
        string? title, string? artist, CancellationToken ct)
    {
        Lookups++;
        // Everything, and let ToneAnchorRetriever rank it — the same code path the
        // product takes, so the anchored run exercises the real selection rather
        // than a shortcut that would always find the perfect tone.
        return Task.FromResult<IReadOnlyList<LibrarySongTones>>(_songs);
    }
}
