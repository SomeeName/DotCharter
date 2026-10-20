using System.Globalization;

namespace DotCharter.Tone.Ai;

/// <summary>
///     Turns a <see cref="ToneDesignRequest" /> plus an
///     <see cref="IToneAnchorLibrary" /> into a <see cref="ToneAnchorSet" />.
///
///     <para>
///         <b>All the ranking is here, none of it is in the library.</b> The real
///         library walks psarc archives and cannot be exercised without a corpus;
///         every decision worth testing — which song is the song, which of its
///         tones answers the request, what to fall back to — is therefore in this
///         pure class, which a list of records is enough to drive.
///     </para>
///     <para>
///         <b>Two outcomes, never mixed.</b> Either the song IS in the library and
///         the set carries one anchor, or it is not and the set carries up to two
///         analogies. Handing the model both would let it quote a neighbour's rig
///         as the song's own; and an analogy presented as an anchor is simply
///         false, which a model repeats back in its rationale.
///     </para>
/// </summary>
public static class ToneAnchorRetriever
{
    /// <summary>How many nearby songs stand in for a song the library does not
    /// have. Two: one is an anecdote and three starts crowding the budget that the
    /// dropped few-shot only just paid for.</summary>
    public const int MaxAnalogies = 2;

    /// <summary>How close two release years must be to count as the same era. A
    /// Rocksmith package carries NO genre — <c>IChartGenreIndex</c> exists for the
    /// user's OWN charts and says so explicitly — so year is the only era signal
    /// available, and eight years is about one guitar-sound generation.</summary>
    public const int SameEraYears = 8;

    /// <summary>
    ///     Retrieves for <paramref name="request" />. Answers
    ///     <see cref="ToneAnchorSet.Empty" /> — never throws — when the request
    ///     names no song, when there is no library, or when the library fails: a
    ///     grounding stage that cannot run leaves the request ungrounded, it does
    ///     not fail it.
    /// </summary>
    public static async Task<ToneAnchorSet> RetrieveAsync(
        IToneAnchorLibrary? library,
        ToneDesignRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (library is null) return ToneAnchorSet.Empty;

        // Title AND artist. A title alone matches a dozen covers, and an anchor
        // from the wrong recording is worse than none — the model would defend it.
        if (string.IsNullOrWhiteSpace(request.SongTitle) || string.IsNullOrWhiteSpace(request.Artist))
            return ToneAnchorSet.Empty;

        IReadOnlyList<LibrarySongTones> songs;
        try
        {
            songs = await library.LookupAsync(request.SongTitle, request.Artist, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Every library fault — missing path, unreadable archive, a psarc this
            // build cannot open — is "no anchor".
            return ToneAnchorSet.Empty;
        }

        return Select(songs, request.SongTitle, request.Artist, request.Description);
    }

    /// <summary>The pure half: rank what the library returned. Public so the
    /// ranking can be driven directly.</summary>
    public static ToneAnchorSet Select(
        IReadOnlyList<LibrarySongTones>? songs, string? title, string? artist, string? description)
    {
        if (songs is null || songs.Count == 0) return ToneAnchorSet.Empty;

        var role = RoleOf(description);
        var usable = songs.Where(s => s?.Tones is { Count: > 0 }).ToList();
        if (usable.Count == 0) return ToneAnchorSet.Empty;

        var exact = usable.FirstOrDefault(s => Same(s.Title, title) && Same(s.Artist, artist));
        if (exact is not null)
        {
            var tone = PickTone(exact, role);
            return tone is null
                ? ToneAnchorSet.Empty
                : new ToneAnchorSet(ToAnchor(exact, tone), []);
        }

        // No exact match. The same ARTIST first — a band's rig is a band's rig —
        // then the same ERA, which for a Rocksmith package means the release year
        // and nothing else: the format carries NO genre, and `IChartGenreIndex`
        // exists for the user's own charts and says so.
        //
        // A song that is neither is NOT an analogy, it is a random song, and a
        // random song labelled "nearby" teaches the model something false about
        // what the player asked for. Those are dropped rather than ranked last.
        var sameArtist = usable.Where(s => Same(s.Artist, artist)).ToList();
        var reference = sameArtist.Select(s => s.Year).FirstOrDefault(y => y > 0);
        IEnumerable<LibrarySongTones> sameEra = reference > 0
            ? usable
                .Where(s => !Same(s.Artist, artist) && s.Year > 0 &&
                            Math.Abs(s.Year - reference) <= SameEraYears)
                .OrderBy(s => Math.Abs(s.Year - reference))
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            : [];

        var ranked = sameArtist
            .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .Concat(sameEra)
            .ToList();

        var analogies = new List<ToneAnchor>(MaxAnalogies);
        foreach (var song in ranked)
        {
            if (analogies.Count == MaxAnalogies) break;
            var tone = PickTone(song, role);
            if (tone is not null) analogies.Add(ToAnchor(song, tone));
        }

        return analogies.Count == 0 ? ToneAnchorSet.Empty : new ToneAnchorSet(null, analogies);
    }

    /// <summary>
    ///     Which tone of <paramref name="song" /> answers a request for
    ///     <paramref name="role" />. A tone whose NAME names the role wins — every
    ///     Rocksmith package names them that way, because the game shows the name to
    ///     the player. With no role in the request, or no tone carrying it, the
    ///     RICHEST chain wins: the longest chain carries the most of what made the
    ///     song sound like itself, and a two-module DI is a poor thing to adapt.
    /// </summary>
    public static LibraryToneCandidate? PickTone(LibrarySongTones? song, ToneAnchorRole role)
    {
        if (song?.Tones is not { Count: > 0 } tones) return null;

        if (role != ToneAnchorRole.Unknown)
        {
            var named = tones
                .Where(t => t is not null && NamesRole(t.ToneName, role))
                .OrderByDescending(t => t.Rig?.Modules.Count ?? 0)
                .FirstOrDefault();
            if (named is not null) return named;
        }

        return tones
            .Where(t => t?.Rig is { Modules.Count: > 0 })
            .OrderByDescending(t => t.Rig.Modules.Count)
            .ThenBy(t => t.ToneName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    ///     The role a description is asking for, or
    ///     <see cref="ToneAnchorRole.Unknown" />. BASS is tested first and wins
    ///     outright: "clean bass" is a bass request, and answering it with the
    ///     guitar's clean tone is the one mistake that produces a chain built on
    ///     the wrong amp — the prompt's own rule 6 exists for it.
    /// </summary>
    public static ToneAnchorRole RoleOf(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return ToneAnchorRole.Unknown;
        var text = " " + description.ToLowerInvariant() + " ";

        if (Mentions(text, "bass")) return ToneAnchorRole.Bass;
        if (Mentions(text, "lead") || Mentions(text, "solo")) return ToneAnchorRole.Lead;
        if (Mentions(text, "rhythm")) return ToneAnchorRole.Rhythm;
        if (Mentions(text, "clean")) return ToneAnchorRole.Clean;
        return ToneAnchorRole.Unknown;
    }

    /// <summary>Whole-word only. "Bassy" is a tone DESCRIPTION, not a bass request,
    /// and "cleanup" is neither.</summary>
    private static bool Mentions(string paddedLowerText, string word)
    {
        var index = paddedLowerText.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = paddedLowerText[index - 1];
            var after = paddedLowerText[index + word.Length];
            if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after)) return true;
            index = paddedLowerText.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool NamesRole(string? toneName, ToneAnchorRole role)
    {
        if (string.IsNullOrWhiteSpace(toneName)) return false;
        var text = " " + toneName.ToLowerInvariant() + " ";
        return role switch
        {
            ToneAnchorRole.Bass => Mentions(text, "bass"),
            ToneAnchorRole.Lead => Mentions(text, "lead") || Mentions(text, "solo"),
            ToneAnchorRole.Rhythm => Mentions(text, "rhythm"),
            ToneAnchorRole.Clean => Mentions(text, "clean"),
            _ => false
        };
    }

    private static ToneAnchor ToAnchor(LibrarySongTones song, LibraryToneCandidate tone)
        => new(song.Title, song.Artist, tone.ToneName, ToDraft(song, tone));

    /// <summary>
    ///     The converted rig as a <see cref="ToneDraft" /> — the same shape the
    ///     model must answer in, so the reference teaches the FORMAT as well as the
    ///     sound. Confidence is 1.0 because this is a shipped tone, not a guess.
    /// </summary>
    private static ToneDraft ToDraft(LibrarySongTones song, LibraryToneCandidate tone)
    {
        var modules = (tone.Rig?.Modules ?? [])
            .Select(m => new ToneDraftModule(
                m.ModuleTypeId,
                new Dictionary<string, float>(m.Params, StringComparer.Ordinal),
                m.Bypassed))
            .ToList();

        var year = song.Year > 0 ? $", {song.Year.ToString(CultureInfo.InvariantCulture)}" : "";
        return new ToneDraft(
            tone.ToneName,
            $"Rocksmith's own \"{tone.ToneName}\" tone for {song.Artist} — {song.Title}{year}.",
            1.0,
            modules);
    }

    /// <summary>Loose equality for a title or an artist. Case, accents-as-typed and
    /// the punctuation a library and a chart disagree about ("Don't" vs "Dont",
    /// "AC/DC" vs "AC DC") are all ignored; nothing fuzzier, because a NEAR match
    /// presented as the song itself is the failure this stage must not have.</summary>
    public static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
           string.Equals(Fold(a), Fold(b), StringComparison.Ordinal);

    private static string Fold(string value)
    {
        var buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }
}
