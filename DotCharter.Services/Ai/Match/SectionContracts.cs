using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match;

// "Match by section" — the contract between three parallel lanes:
//   sections : IToneSectionProposer + IToneSetMatcher + the stem-kind isolation
//              (services; segmentation by timbre change over the isolated stem)
//   prior    : IToneMatchPriorSource + IToneNarrator (the language model's guess
//              feeds the matcher as its starting point; the model then explains
//              what the matcher found, in words)
//   ui       : the section list in the tone-design panel (span + stem pickers,
//              proposals, one rig per section, "apply as tone set")
// Interfaces and records only; every lane codes against these and fakes the rest
// until the merge. Additive changes only — never rename or remove a member. Same
// rules as MatchContracts.cs, which this file extends rather than replaces.

/// <summary>The stems a song separates into and a section can match against.
/// Strings rather than an enum so the separator's own stem names, the manifest and
/// this vocabulary are one thing, and a new stem stays an additive change. The
/// names are the six-stem separator's target names verbatim.</summary>
public static class ToneStems
{
    public const string Guitar = "guitar";
    public const string Bass = "bass";
    public const string Piano = "piano";
    public const string Other = "other";

    /// <summary>Every stem a section may name, in picker order.</summary>
    public static IReadOnlyList<string> All { get; } = [Guitar, Bass, Piano, Other];
}

/// <summary>One part of a song the user wants a rig for: "0:10 to 0:30, guitar,
/// clean". <paramref name="Label"/> is the user's or the proposer's word for it and
/// becomes the tone's name; <paramref name="Prior"/> is what the language model (or
/// the user) thinks the rig is, or null for a pure audio match.</summary>
public sealed record ToneSection(
    string Id,
    string Label,
    string Stem,
    TimeSpan From,
    TimeSpan To,
    ToneMatchPrior? Prior = null)
{
    public TimeSpan Duration => To - From;

    /// <summary>A section shorter than this cannot be matched: the feature extractor
    /// needs a few seconds of playing to average over, and the separator's edge
    /// artefacts dominate anything shorter.</summary>
    public static TimeSpan MinDuration { get; } = TimeSpan.FromSeconds(4);

    public bool IsUseful => Duration >= MinDuration && ToneStems.All.Contains(Stem);
}

/// <summary>What the proposer thinks the song's parts are: a section plus the
/// evidence for the boundary (0..1, how strongly the timbre changed there) and where
/// it came from — the chart's tone changes, the audio, or both agreeing.</summary>
public sealed record ToneSectionProposal(ToneSection Section, double Confidence, string Origin);

/// <summary>Where a proposal came from. The UI shows chart-derived sections as
/// authoritative and audio-derived ones as suggestions.</summary>
public static class ToneSectionOrigins
{
    /// <summary>The chart's own tone changes (a song in the library).</summary>
    public const string Chart = "chart";
    /// <summary>A timbre change detected in the isolated stem.</summary>
    public const string Audio = "audio";
    /// <summary>A chart boundary the audio agrees with.</summary>
    public const string Both = "chart+audio";
}

/// <summary>Proposes sections for a song: from the chart's tone changes when there
/// is one, and from timbre changes in the isolated stem always — so a song that is
/// not in the library still gets "0:10–0:30 clean, 0:30–0:50 driven" suggestions.
/// Never called on the audio thread; may separate stems (cache-first).</summary>
public interface IToneSectionProposer
{
    /// <param name="songAudioPath">The song's audio file.</param>
    /// <param name="stem">One of <see cref="ToneStems"/>.</param>
    /// <param name="chartSpans">Spans from the chart's tone changes (from/to/label),
    /// or empty when the song has no chart.</param>
    /// <param name="status">Progress lines ("Separating…", "Scanning 2:10…"); may be null.</param>
    /// <param name="ct">Cancels the separation and the scan.</param>
    Task<IReadOnlyList<ToneSectionProposal>> ProposeAsync(
        string songAudioPath,
        string stem,
        IReadOnlyList<ToneSection> chartSpans,
        IProgress<string>? status,
        CancellationToken ct);
}

/// <summary>A whole song's worth of sections to match in one go.</summary>
/// <param name="SongAudioPath">The song's audio file (the isolator separates it, cache-first).</param>
/// <param name="ReferenceDi">The player's DI the candidates are rendered through.</param>
/// <param name="Sections">The sections, in song order.</param>
/// <param name="CandidatesPerSection">How many candidates each section keeps.</param>
/// <param name="BudgetPerSection">Wall budget per section, or null for the matcher's default.</param>
/// <param name="SongTitle">For the prior and the narrator; null when unknown.</param>
/// <param name="Artist">For the prior and the narrator; null when unknown.</param>
public sealed record ToneSetMatchRequest(
    string SongAudioPath,
    MonoAudio ReferenceDi,
    IReadOnlyList<ToneSection> Sections,
    int CandidatesPerSection = 5,
    TimeSpan? BudgetPerSection = null,
    string? SongTitle = null,
    string? Artist = null);

/// <summary>One section's outcome. <paramref name="Match"/> is null when the section
/// failed and <paramref name="Error"/> says why — a set never fails as a whole
/// because one part could not be isolated.</summary>
public sealed record ToneSectionMatch(ToneSection Section, ToneMatchResult? Match, string? Error);

public sealed record ToneSetMatchResult(
    IReadOnlyList<ToneSectionMatch> Sections,
    TimeSpan Elapsed,
    IReadOnlyList<string> Notes);

/// <summary>Matches every section of a song, sequentially (the renderer is one
/// device-less engine), isolating each section's stem, asking the prior source for a
/// starting point when the section has none, and running <see cref="IToneMatcher"/>.
/// Progress is reported per section ("Section 2 of 4: isolating…").</summary>
public interface IToneSetMatcher
{
    Task<ToneSetMatchResult> MatchAsync(ToneSetMatchRequest request, IProgress<string>? status, CancellationToken ct);
}

/// <summary>A language model's guess at a section's rig, used as the matcher's
/// starting point (retrieval seed + CMA-ES initial mean) — knowledge before
/// measurement. Implemented over <see cref="IToneDesignService"/>; returns null,
/// never throws, when no provider is configured, so a pure audio match still runs.</summary>
public interface IToneMatchPriorSource
{
    bool IsAvailable { get; }
    Task<ToneMatchPrior?> GetPriorAsync(ToneSection section, string? songTitle, string? artist, IProgress<string>? status, CancellationToken ct);
}

/// <summary>What the narrator says about a matched rig: a name for the tone and a
/// short plain-language rationale ("a mid-gain crunch with a slapback; the amp is
/// doing the work"), in the app's language.</summary>
public sealed record ToneNarration(string Name, string Rationale, AiUsage Usage);

/// <summary>Turns a matched rig into words. Implemented over the configured
/// language model with its own small prompt and schema; returns null, never throws,
/// when no provider is configured. Never called for a rig the user did not ask
/// about — one call per "Explain", not per candidate.</summary>
public interface IToneNarrator
{
    bool IsAvailable { get; }
    Task<ToneNarration?> NarrateAsync(DcRigPreset rig, ToneSection section, string? songTitle, string? artist, CancellationToken ct);
}

/// <summary>One matched section as the UI applies it: the rig became a tone
/// preset named after the section, to be placed at the section's start as a tone
/// change. The set is what "apply as tone set" hands to the project.</summary>
public sealed record ToneSetEntry(ToneSection Section, DspTonePreset Tone);

public sealed record ToneSetDraft(IReadOnlyList<ToneSetEntry> Entries, string? SongTitle, string? Artist);
