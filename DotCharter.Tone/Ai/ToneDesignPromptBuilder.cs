using System.Globalization;
using System.Text;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Ai;

/// <summary>
///     Builds the <see cref="ToneDesignPrompt" />: the system prompt that grounds a
///     model in THIS build's module catalogue, plus the conversation.
///
///     <para>
///         <b>The system prompt is ordered most-stable-first</b> — rules, then the
///         catalogue, then the few-shot examples — and is built ONCE into
///         <see cref="SystemPrompt" />. That ordering is the whole reason it is
///         assembled separately from the conversation: it is byte-identical across
///         every request this build makes, so it is exactly the prefix a provider's
///         prompt cache can hit. Interpolating the user's request into it (the
///         obvious shortcut) would make every request a cache MISS and multiply
///         the cost of the catalogue by the number of requests.
///     </para>
///     <para>
///         <b>The catalogue block is generated, never written by hand.</b> Ranges
///         and defaults come from <c>DcRigTargetCatalog</c> — the same table
///         <see cref="ToneDraftValidator" /> clamps against — so the model cannot be
///         told a range the validator will then disagree with. Only the prose
///         (display name, category, one-line note) comes from
///         <see cref="AiGearNotes" />.
///     </para>
/// </summary>
public static class ToneDesignPromptBuilder
{
    /// <summary>Ids of the factory presets rendered as few-shot examples: a clean,
    /// a crunch, a high-gain and a bass, chosen because all four are algorithmic
    /// (no <c>namamp</c> capture file, which a draft cannot carry) and because a
    /// bass example is what stops a model reaching for <c>amp</c> on a bass
    /// request.</summary>
    public static IReadOnlyList<string> FewShotPresetIds { get; } =
        ["clean-glass", "crunch-plexi", "metal-5150-rhythm", "gear-bass-di"];

    /// <summary>The few-shot that steps aside for a retrieved ANCHOR.
    /// <c>crunch-plexi</c>, and not one of the other three: <c>clean-glass</c> and
    /// <c>metal-5150-rhythm</c> bracket the gain range, and <c>gear-bass-di</c> (bassod → bassamp) is
    /// the one that stops a model reaching for <c>amp</c> on a bass request. A
    /// mid-gain crunch is the example a real, song-specific reference most nearly
    /// replaces.</summary>
    public const string AnchorReplacesFewShotId = "crunch-plexi";

    /// <summary>The factory preset rendered as the CAPTURE few-shot: the one example
    /// that shows the <c>capture</c> field in use. It is added only when the player
    /// HAS captures, and it is a real capture-backed preset rather than a written
    /// one, so it round-trips through the validator like the other four.</summary>
    public const string CaptureFewShotPresetId = "nam-metal-5150-rhythm";

    private static readonly Lazy<string> CatalogueBlockCache =
        new(() => BuildCatalogueBlock(hasCaptures: false), isThreadSafe: true);

    private static readonly Lazy<string> CaptureCatalogueBlockCache =
        new(() => BuildCatalogueBlock(hasCaptures: true), isThreadSafe: true);

    private static readonly Lazy<string> FewShotBlockCache =
        new(() => BuildFewShotBlock(FewShotPresetIds, null), isThreadSafe: true);

    private static readonly Lazy<string> AnchoredFewShotBlockCache =
        new(() => BuildFewShotBlock(
            FewShotPresetIds.Where(id => id != AnchorReplacesFewShotId).ToList(), null), isThreadSafe: true);

    /// <summary>One entry per (anchored, capture-catalogue) pair. There is one
    /// system prompt per distinct capture library, and it must be the SAME string
    /// every time that library is seen — a rebuilt prefix is a prompt-cache miss and
    /// the catalogue charged at full price again. Keyed by the catalogue's
    /// fingerprint, so a library that gained a capture gets a new prefix and an
    /// unchanged one keeps hitting the old.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        SystemPromptCache = new(StringComparer.Ordinal);

    /// <summary>The generated catalogue listing — one line per module plus its
    /// params as <c>id (min..max, default, unit)</c>. Cached: it is a few thousand
    /// tokens and does not change within a build.
    /// <para>This is the NO-CAPTURE rendering. With captures installed the
    /// <c>namamp</c> row carries its real note instead of the "avoid it" one —
    /// <see cref="AiGearNotes.NamAmpNoteWithoutCaptures" />.</para></summary>
    public static string CatalogueBlock => CatalogueBlockCache.Value;

    /// <summary>The catalogue listing for a player who HAS captures.</summary>
    public static string CaptureCatalogueBlock => CaptureCatalogueBlockCache.Value;

    /// <summary>The four rendered few-shot examples.</summary>
    public static string FewShotBlock => FewShotBlockCache.Value;

    /// <summary>The same examples minus <see cref="AnchorReplacesFewShotId" />, for
    /// a request that carries a retrieved anchor.</summary>
    public static string AnchoredFewShotBlock => AnchoredFewShotBlockCache.Value;

    /// <summary>The whole system prompt (rules + catalogue + examples) for a build
    /// with NO neural captures installed. Stable for the life of the process.</summary>
    public static string SystemPrompt => SystemPromptFor(null);

    /// <summary>
    ///     The system prompt grounded in <paramref name="captures" />: the same
    ///     rules, idiom and catalogue, plus the CAPTURE LIST between the catalogue
    ///     and the examples, plus one more few-shot that uses a capture.
    ///     <para>
    ///         An empty or null catalogue gives exactly <see cref="SystemPrompt" />
    ///         — no capture block, and the "do not use namamp" rule back. A block
    ///         listing nothing is worse than no block: it tells the model there is a
    ///         list and then shows it none.
    ///     </para>
    ///     <para>
    ///         <paramref name="captureSearch" /> is true while TONE3000 can be
    ///         searched (signed in, and a capture source wired): <c>namamp</c> is then
    ///         usable even with an empty library, because the reply may name a
    ///         search instead of a listed capture (<see cref="ToneDraft.CaptureSearch" />).
    ///         It is part of the cache key, so the two prompts never share a prefix.
    ///     </para>
    /// </summary>
    public static string SystemPromptFor(NamCaptureCatalog? captures, bool captureSearch = false)
        => CachedSystemPrompt(captures, anchored: false, captureSearch);

    /// <summary>The anchored system prompt grounded in <paramref name="captures" />.</summary>
    public static string AnchoredSystemPromptFor(NamCaptureCatalog? captures, bool captureSearch = false)
        => CachedSystemPrompt(captures, anchored: true, captureSearch);

    private static string CachedSystemPrompt(NamCaptureCatalog? captures, bool anchored, bool captureSearch)
    {
        var set = captures ?? NamCaptureCatalog.Empty;
        var key = (anchored ? "a:" : "u:") + (captureSearch ? "s:" : "") + set.Fingerprint;
        return SystemPromptCache.GetOrAdd(key, _ =>
        {
            var ids = anchored
                ? FewShotPresetIds.Where(id => id != AnchorReplacesFewShotId).ToList()
                : FewShotPresetIds;
            var fewShotIds = set.IsEmpty ? ids : ids.Append(CaptureFewShotPresetId).ToList();
            return BuildSystemPrompt(
                BuildFewShotBlock(fewShotIds, set.IsEmpty ? null : set),
                fewShotIds.Count,
                set,
                captureSearch);
        });
    }

    /// <summary>
    ///     The system prompt for a request carrying an ANCHOR: one few-shot fewer,
    ///     because the anchor is a better example than a generic one and the 12k
    ///     budget is not free.
    ///
    ///     <para>
    ///         <b>There are two cached prefixes now, and that is the trade.</b> Each
    ///         is still build-constant and therefore still exactly the prefix a
    ///         provider's prompt cache can hit, but a player who alternates anchored
    ///         and unanchored requests pays a cache MISS on the switch. The
    ///         alternative — one prefix, all four generic examples, and the anchor on
    ///         the message side — costs the anchor's ~400 tokens of UNCACHED input on
    ///         every anchored request instead, which is the worse of the two.
    ///     </para>
    /// </summary>
    public static string AnchoredSystemPrompt => AnchoredSystemPromptFor(null);

    /// <summary>A rough token count for <paramref name="text" /> — four characters
    /// to a token, the usual English estimate. Used for the size log line only;
    /// nothing decides anything on it.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>Assembles the prompt for <paramref name="request" />, with no
    /// retrieved reference.</summary>
    public static ToneDesignPrompt Build(ToneDesignRequest request)
        => Build(request, null, null);

    /// <summary>
    ///     Assembles the prompt for <paramref name="request" />, grounded in
    ///     <paramref name="anchors" /> when retrieval found anything.
    ///
    ///     <para>
    ///         A true ANCHOR — the song's own Rocksmith tone — swaps the system
    ///         prompt for <see cref="AnchoredSystemPrompt" />, which carries one
    ///         generic example fewer. ANALOGIES do not: two neighbouring songs are a
    ///         weaker signal than a generic example is a teacher of the FORMAT, and
    ///         trading a few-shot away for them would be a bad swap.
    ///     </para>
    /// </summary>
    public static ToneDesignPrompt Build(ToneDesignRequest request, ToneAnchorSet? anchors)
        => Build(request, anchors, null);

    /// <summary>
    ///     Assembles the prompt for <paramref name="request" />, grounded in
    ///     <paramref name="anchors" /> and in the player's own neural-capture
    ///     library <paramref name="captures" />.
    /// </summary>
    /// <param name="request">What the user asked for.</param>
    /// <param name="anchors">Grounding stage 2, or null.</param>
    /// <param name="captures">The closed capture list a <c>namamp</c> may choose
    /// from, or null / empty for a machine with no captures.</param>
    /// <param name="captureSearch">True while the reply may ask TONE3000 for a
    /// capture that is not on the list — see <see cref="SystemPromptFor" />.</param>
    public static ToneDesignPrompt Build(
        ToneDesignRequest request, ToneAnchorSet? anchors, NamCaptureCatalog? captures,
        bool captureSearch = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        var set = anchors ?? ToneAnchorSet.Empty;
        return new ToneDesignPrompt(
            set.Anchor is not null
                ? AnchoredSystemPromptFor(captures, captureSearch)
                : SystemPromptFor(captures, captureSearch),
            BuildMessages(request, set),
            ToneDraftJson.Schema,
            ToneDraftJson.SchemaName);
    }

    /// <summary>
    ///     The retrieved reference, as it goes into the conversation.
    ///
    ///     <para>
    ///         The anchor is stated for what it IS — one professional's take, in
    ///         this app's own module ids — and the model is told to ADAPT it rather
    ///         than reproduce it. "This is the answer", on a reference that is one
    ///         lossy conversion of one interpretation, produces a copy, which is not
    ///         what the player asked for.
    ///     </para>
    ///     <para>
    ///         An ANALOGY presented as the song's own tone is simply FALSE, and a
    ///         model repeats it back in its rationale — so the two are labelled
    ///         differently and never appear together.
    ///     </para>
    /// </summary>
    public static string RenderAnchors(ToneAnchorSet? anchors)
    {
        var set = anchors ?? ToneAnchorSet.Empty;
        if (!set.HasAny) return string.Empty;

        var sb = new StringBuilder(4 * 1024);
        if (set.Anchor is { } anchor)
        {
            sb.Append("This is how Rocksmith rigged \"").Append(anchor.Title)
                .Append("\" by ").Append(anchor.Artist)
                .Append(" — its \"").Append(anchor.ToneName)
                .AppendLine("\" tone, converted into DotCharter's own modules:");
            sb.AppendLine(ToneDraftJson.Serialize(anchor.Draft));
            sb.AppendLine();
            sb.AppendLine("Adapt it to the request rather than starting from nothing. It is one");
            sb.AppendLine("interpretation and the conversion is lossy, so change what the request");
            sb.AppendLine("asks for — and anything it plainly gets wrong for this request.");
            sb.AppendLine();
            return sb.ToString();
        }

        sb.AppendLine("The requested song is NOT in the player's library, so there is no tone for");
        sb.AppendLine("it here. These are Rocksmith tones for NEARBY songs, as analogies only —");
        sb.AppendLine("never describe them as the requested song's own tone:");
        sb.AppendLine();
        foreach (var analogy in set.Analogies)
        {
            sb.Append("Analogy — \"").Append(analogy.Title).Append("\" by ").Append(analogy.Artist)
                .Append(", its \"").Append(analogy.ToneName).AppendLine("\" tone:");
            sb.AppendLine(ToneDraftJson.Serialize(analogy.Draft));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     The conversation. A FRESH request is one user turn carrying the
    ///     description and any song/artist context. A FOLLOW-UP (a
    ///     <see cref="ToneDesignRequest.CurrentTone" /> plus history) replays the
    ///     history and then hands the model the current chain as ToneDraft JSON,
    ///     with the instruction to return the FULL edited chain — a model asked for
    ///     "the changes" returns a diff nobody can apply, and the validator would
    ///     see a one-module rig.
    /// </summary>
    private static List<ToneDesignTurn> BuildMessages(ToneDesignRequest request, ToneAnchorSet anchors)
    {
        var messages = new List<ToneDesignTurn>();

        foreach (var turn in request.History ?? [])
        {
            if (!string.IsNullOrWhiteSpace(turn?.Text))
                messages.Add(turn);
        }

        var sb = new StringBuilder();

        // The reference goes FIRST, and in the SAME trailing turn as the request —
        // so a FOLLOW-UP keeps its anchor and "less gain" is still measured against
        // the song's own rig rather than against whatever the last answer was. A
        // separate turn would put two user turns back to back, which every
        // strictly-alternating chat API rejects.
        sb.Append(RenderAnchors(anchors));

        if (request.CurrentTone is { } current)
        {
            sb.AppendLine("This is the tone currently on the rig:");
            sb.AppendLine(ToneDraftJson.Serialize(ToDraft(current)));
            sb.AppendLine();
            sb.AppendLine("Edit it as asked below and return the FULL chain, every module and every");
            sb.AppendLine("param you want set — not just what changed.");
            sb.AppendLine();
        }

        sb.Append("Request: ").AppendLine(request.Description?.Trim());
        if (!string.IsNullOrWhiteSpace(request.SongTitle))
            sb.Append("Song: ").AppendLine(request.SongTitle.Trim());
        if (!string.IsNullOrWhiteSpace(request.Artist))
            sb.Append("Artist: ").AppendLine(request.Artist.Trim());
        if (!string.IsNullOrWhiteSpace(request.Tuning))
            sb.Append("The player's guitar is tuned to: ").AppendLine(request.Tuning.Trim());

        messages.Add(new ToneDesignTurn(ToneDesignRole.User, sb.ToString().TrimEnd()));

        // A provider that alternates roles strictly (every chat API does) needs the
        // last turn to be the user's. History that ends on a user turn would put two
        // user turns back to back, which the messages above already merge into one
        // trailing turn — but a history ending on an ASSISTANT turn is fine and is
        // the normal follow-up shape.
        return messages;
    }

    /// <summary>A preset as the model would have written it: every module's float
    /// params, in order. String params (a cab IR, Miku lyrics) and branch tags are
    /// dropped — a v1 draft cannot express them, and an example carrying a field the
    /// schema forbids teaches the model to emit it.</summary>
    public static ToneDraft ToDraft(DspTonePreset preset) => ToDraft(preset, null);

    /// <summary>
    ///     The same, with a <c>namamp</c> module's <c>modelPath</c> translated back
    ///     into a CAPTURE ID from <paramref name="captures" /> — the one string param
    ///     a draft CAN express since W8.
    ///     <para>
    ///         A capture the catalogue does not have (the preset names a bundled file
    ///         the player deleted) falls back to the catalogue's FIRST row rather
    ///         than to nothing: an example whose capture id is not in the list beside
    ///         it teaches the model that inventing one is fine, which is the exact
    ///         mistake the validator then has to drop a module for.
    ///     </para>
    /// </summary>
    public static ToneDraft ToDraft(DspTonePreset preset, NamCaptureCatalog? captures)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var modules = preset.Rig.Modules
            .Select(m => new ToneDraftModule(
                m.ModuleTypeId,
                new Dictionary<string, float>(m.Params, StringComparer.Ordinal),
                m.Bypassed,
                CaptureIdFor(m, captures)))
            .ToList();

        return new ToneDraft(preset.Name, preset.Description, 0.9, modules);
    }

    private static string CaptureIdFor(DcRigModule module, NamCaptureCatalog? captures)
    {
        if (captures is null || captures.IsEmpty) return "";
        if (!string.Equals(module.ModuleTypeId, AiGearNotes.NamAmpId, StringComparison.Ordinal)) return "";

        var fileName = module.StringParams is { } strings && strings.TryGetValue("modelPath", out var path)
            ? Path.GetFileNameWithoutExtension(path ?? "")
            : "";

        return captures.Find(fileName)?.Id ?? captures.Options[0].Id;
    }

    /// <param name="fewShotBlock">The rendered examples to carry.</param>
    /// <param name="fewShotCount">How many they are — the prose says so, and a
    /// prompt that promises four and shows three is the kind of small
    /// inconsistency a model notices and reasons about.</param>
    /// <param name="captures">The capture catalogue this prompt is built for. Its
    /// block goes AFTER the module catalogue and BEFORE the examples: the catalogue
    /// is build-constant and identical on every machine, so everything above the
    /// capture block stays a shared prefix, and the examples that follow it are the
    /// only part that has to be re-rendered when a library changes.</param>
    /// <param name="captureSearch">Whether the reply may name a TONE3000 search.</param>
    private static string BuildSystemPrompt(
        string fewShotBlock, int fewShotCount, NamCaptureCatalog captures, bool captureSearch)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine(RulesFor(!captures.IsEmpty, captureSearch));
        sb.AppendLine();
        sb.Append(RsConverterIdiom.Block);
        sb.AppendLine();
        sb.AppendLine("# Module catalogue");
        sb.AppendLine();
        sb.AppendLine("Every module you may use, with its params as `id (min..max, default, unit)`.");
        sb.AppendLine("Values are in the param's own unit. A param you omit takes its default.");
        sb.AppendLine();
        sb.Append(captures.IsEmpty && !captureSearch ? CatalogueBlock : CaptureCatalogueBlock);
        sb.AppendLine();
        if (!captures.IsEmpty)
        {
            sb.Append(captures.Block);
            sb.AppendLine();
        }

        sb.AppendLine("# Examples");
        sb.AppendLine();
        sb.Append(Count(fewShotCount))
            .AppendLine(" tones from the factory library, in exactly the format you must answer in.");
        sb.AppendLine();
        sb.Append(fewShotBlock);
        return sb.ToString();
    }

    /// <summary>The small counts this prompt uses, as words. Prose, not a number,
    /// because everything around it is prose.</summary>
    private static string Count(int value) => value switch
    {
        3 => "Three",
        4 => "Four",
        5 => "Five",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>
    ///     The rules block. Rule 12 is the ONE line that depends on the player's
    ///     machine: with captures installed <c>namamp</c> is a module like any other
    ///     provided it names one, and with none it is unusable and says so. Both
    ///     variants are constants, so each is still byte-identical across every
    ///     request that uses it. With a TONE3000 search available there are two more
    ///     variants, which teach the <c>captureSearch</c> field.
    /// </summary>
    internal static string RulesFor(bool hasCaptures, bool captureSearch = false) => (hasCaptures, captureSearch) switch
    {
        (true, true) => RulesWithCapturesAndSearch,
        (false, true) => RulesWithSearchOnly,
        (true, false) => RulesWithCaptures,
        _ => Rules
    };

    /// <summary>Rule 12 as it reads with no captures installed. Spelled out
    /// separately so the capture variant is a SUBSTITUTION into the one rules block
    /// rather than a second copy of fourteen rules that can drift from it.</summary>
    private const string RuleTwelveWithoutCaptures = """
        12. Do not use namamp, convreverb, miku or mikustomp unless the request names
            them: they need a model, impulse or lyric file that this reply cannot carry.
        """;

    private const string RuleTwelveWithCaptures = """
        12. Do not use convreverb, miku or mikustomp unless the request names them:
            they need an impulse or lyric file that this reply cannot carry. namamp is
            different — it is a real choice here, but ONLY with a "capture" naming one
            of the captures listed below the module catalogue. A namamp with no
            capture, or with an id that is not on that list, is dropped.
        """;

    private const string RuleTwelveWithCapturesAndSearch = """
        12. Do not use convreverb, miku or mikustomp unless the request names them:
            they need an impulse or lyric file that this reply cannot carry. namamp is
            different — it plays a neural capture of a real amp, and when the request
            names or implies a specific real amp it is the better choice over the
            modelled amps. Name one of the captures listed below the module catalogue
            in its "capture", or, when none of them is the amp the request calls for,
            leave "capture" empty and set "captureSearch" to a short TONE3000 search
            for that amp: make, model and character, e.g. "Fender Deluxe Reverb clean"
            or "Peavey 5150 high gain". The app downloads the best match. Put a cab
            after it like any amp. Leave "captureSearch" empty when no namamp needs it.
        """;

    private const string RuleTwelveWithSearchOnly = """
        12. Do not use convreverb, miku or mikustomp unless the request names them:
            they need an impulse or lyric file that this reply cannot carry. namamp is
            different — it plays a neural capture of a real amp, and when the request
            names or implies a specific real amp it is the better choice over the
            modelled amps. Leave its "capture" empty and set "captureSearch" to a short
            TONE3000 search for that amp: make, model and character, e.g. "Fender
            Deluxe Reverb clean" or "Peavey 5150 high gain". The app downloads the best
            match. Put a cab after it like any amp. Leave "captureSearch" empty when
            you do not use namamp.
        """;

    private static readonly string RulesWithCaptures = SwapRuleTwelve(RuleTwelveWithCaptures);
    private static readonly string RulesWithCapturesAndSearch = SwapRuleTwelve(RuleTwelveWithCapturesAndSearch);
    private static readonly string RulesWithSearchOnly = SwapRuleTwelve(RuleTwelveWithSearchOnly);

    private static string SwapRuleTwelve(string replacement)
    {
        var swapped = Rules.Replace(RuleTwelveWithoutCaptures, replacement, StringComparison.Ordinal);
        if (ReferenceEquals(swapped, Rules) || swapped.Length == Rules.Length)
        {
            throw new InvalidOperationException(
                "ToneDesignPromptBuilder.RuleTwelveWithoutCaptures no longer appears in Rules verbatim — " +
                "the capture variant would have silently kept the \"do not use namamp\" line.");
        }

        return swapped;
    }

    private const string Rules = """
        You are DotCharter's tone designer. You build a guitar or bass signal chain out
        of DotCharter's own DSP modules and answer with JSON describing it. You never
        hear audio; a tone here is pure data.

        # Rules

        1. The chain is a SERIES chain, input first. There are no parallel lanes.
        2. Between 1 and 12 modules. Most good tones are 4 to 7.
        3. Order the chain the way this app wires a real rig, unless the request
           explicitly asks otherwise: everything a player puts on the FLOOR comes
           before the amp, everything that lives in a RACK comes after the cab.
           pedals (gate, stompbox compressor, boost, overdrive, distortion, fuzz,
           wah, octave) -> amp -> cab -> mic/room -> rack eq / graphic eq ->
           modulation -> delay -> reverb -> utility.
           The house idiom below tags every module with the slot its real gear
           occupies; follow those tags.
        4. Put a cab AFTER an amp, ALWAYS — guitar amp, bass head and tube DI preamp
           alike. A bass rig is bassamp -> cab, not a bare DI. The only chain that
           ends without a cab is an acpre acoustic DI.
        5. Never two amps in one chain. Pick one.
        6. A BASS request uses bassamp (and bassod for grit), never the guitar amp,
           and it still gets its cab.
        7. An ACOUSTIC request played on an electric guitar is acousticsim in FRONT
           of a normal amp (or tubepre) and its cab — that is the rig this app
           builds. Reach for acpre only when the request says piezo, DI or plugged
           straight into the desk.
        8. Prefer the SPECIFIC module over the generic one — see the house idiom
           below. A named real pedal has a module of its own here; the generic
           compressor / eq / reverb / delay are fallbacks for gear that has none.
        9. Use only module type ids from the catalogue below, spelled exactly. A
           module you invent is dropped and the tone comes out wrong.
        10. Use only param ids listed for that module, and keep every value inside the
            printed range, in the printed unit. A value outside the range is clamped,
            which is rarely what you meant.
        11. Set the params that MATTER for the request and leave the rest out — an
            omitted param takes its catalogue default, which is always sensible.
        12. Do not use namamp, convreverb, miku or mikustomp unless the request names
            them: they need a model, impulse or lyric file that this reply cannot carry.
        13. "bypassed" is for a module the player should switch ON themselves (a
            boost for solos). Normally false.
        14. Answer with the JSON object ONLY. No prose, no markdown fences, no
            commentary before or after it.
        """;

    /// <param name="hasCaptures">Whether the player has any neural captures. It
    /// swaps ONE line — <c>namamp</c>'s note — because the table's own note points at
    /// a capture list, and pointing at an empty list is how a design comes back
    /// naming a capture nobody has.</param>
    private static string BuildCatalogueBlock(bool hasCaptures)
    {
        var specs = DcRigTargetCatalog.AllModules.ToDictionary(m => m.ModuleTypeId, m => m.Params, StringComparer.Ordinal);
        var sb = new StringBuilder(48 * 1024);

        string? category = null;
        foreach (var note in AiGearNotes.All)
        {
            if (AiGearNotes.SeriesExcluded.Contains(note.Id)) continue;
            if (!specs.TryGetValue(note.Id, out var moduleParams)) continue;

            if (!string.Equals(category, note.Category, StringComparison.Ordinal))
            {
                category = note.Category;
                sb.AppendLine();
                sb.Append("## ").AppendLine(category);
            }

            var prose = !hasCaptures && string.Equals(note.Id, AiGearNotes.NamAmpId, StringComparison.Ordinal)
                ? AiGearNotes.NamAmpNoteWithoutCaptures
                : note.Note;
            sb.Append(note.Id).Append(" — ").Append(note.Name).Append(" — ").AppendLine(prose);
            sb.Append("    ");
            for (var i = 0; i < moduleParams.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                var p = moduleParams[i];
                sb.Append(p.Id)
                    .Append(" (")
                    .Append(ToneDesignNumber.Format(p.Min)).Append("..").Append(ToneDesignNumber.Format(p.Max))
                    .Append(", def ").Append(ToneDesignNumber.Format(p.Default));
                var unit = AiGearNotes.UnitOf(p.Id);
                if (unit is not null) sb.Append(' ').Append(unit);
                sb.Append(')');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildFewShotBlock(IReadOnlyList<string> ids, NamCaptureCatalog? captures)
    {
        var byId = DspTonePresetLibrary.All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var sb = new StringBuilder(8 * 1024);

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var preset)) continue;
            sb.Append("Request: ").AppendLine(preset.Description);
            sb.AppendLine(ToneDraftJson.Serialize(ToDraft(preset, captures)));
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
