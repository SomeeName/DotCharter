using DotCharter.Services.Ai.Match;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>A span of the song's audio the creator measures, when the charter asks it to.</summary>
/// <param name="SongAudioPath">The song's audio; its cached stems are read, never separated.</param>
/// <param name="FromMs">Where the span starts.</param>
/// <param name="ToMs">Where it ends.</param>
/// <param name="ToneName">The placed tone change whose window this is, when it was picked for that tone; else null.</param>
public sealed record ToneCreatorWindow(string SongAudioPath, double FromMs, double ToMs, string? ToneName = null);

/// <summary>One question to the Tone designer's AI creator.</summary>
/// <param name="Description">The charter's words for the tone ("Master of Puppets rhythm, tight, scooped").</param>
/// <param name="Instrument">One of <see cref="ToneStems" />: it picks the catalogue block and the gear the reply may name.</param>
/// <param name="ToneKey">The key the reply carries.</param>
/// <param name="ToneName">The display name.</param>
/// <param name="Context">The song's metadata (<see cref="AiToneGenerationRun.ContextFor" />), or null.</param>
/// <param name="SongTitle">For the model's own knowledge of the song.</param>
/// <param name="Artist">Likewise.</param>
/// <param name="ArrangementName">So a tone named after the arrangement is not read as a description.</param>
/// <param name="Window">The span to measure, or null for the charter's words alone.</param>
/// <param name="Previous">On Refine, the proposal being refined; null on a first ask.</param>
/// <param name="Refinement">On Refine, what to change ("more gain, less reverb").</param>
public sealed record ToneCreatorAsk(
    string Description,
    string Instrument,
    string ToneKey,
    string ToneName,
    string? Context,
    string? SongTitle,
    string? Artist,
    string? ArrangementName,
    ToneCreatorWindow? Window = null,
    RsTone? Previous = null,
    string? Refinement = null);

/// <summary>What the creator proposes: a catalogue-valid tone, the model's one-line reason, and whether audio was measured.</summary>
public sealed record ToneCreatorProposal(RsTone Tone, string Rationale, string ModelId, bool Measured);

/// <summary>The Tone designer's AI creator: the charter's words (and optionally a measured span) to ONE Rocksmith tone.</summary>
public interface IToneCreator
{
    /// <summary>Asks the model once. Throws <see cref="RsToneCatalogException" /> when the reply names gear the
    /// game does not have, a knob outside its range, or gear of the other instrument — rejected, never repaired.</summary>
    Task<ToneCreatorProposal> ProposeAsync(ToneCreatorAsk ask, CancellationToken ct);
}

/// <summary>
///     The shipping <see cref="IToneCreator" />: the W6 lane's own pieces, one tone at a time — the cache-only stem
///     isolation and the measured features when a window is given, then <see cref="IRsToneMapper" /> (the
///     Rocksmith catalogue prompt and its reject-never-repair reader), then the instrument check the catalogue gate
///     does not make: a guitar tone naming a <c>Bass_</c> amp or cabinet, or the other way round.
///     <para>
///         <b>Stems are never separated here.</b> The isolator the composition root hands over is
///         <see cref="StemIsolator.CacheOnly" />; a miss is "no measurement", and the tone is mapped from the
///         words alone, which <see cref="ToneCreatorProposal.Measured" /> says.
///     </para>
/// </summary>
public sealed class ToneCreator(IRsToneMapper mapper, IStemIsolator isolator, IToneEvidenceSource evidence) : IToneCreator
{
    /// <summary>The fault code for gear of the other instrument.</summary>
    public const string WrongInstrumentCode = "wrong-instrument";

    /// <summary>The app's creator: the stem isolator narrowed to its cache (<see cref="StemIsolator.CacheOnly" />), the measured features.</summary>
    public static ToneCreator CacheOnly(IRsToneMapper mapper, IStemIsolator isolator) =>
        new(mapper, isolator is StemIsolator stems ? stems.CacheOnly() : isolator, new MeasuredToneEvidenceSource());

    /// <inheritdoc />
    public async Task<ToneCreatorProposal> ProposeAsync(ToneCreatorAsk ask, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);
        var instruction = InstructionFor(ask);
        var features = await MeasureAsync(ask, ct).ConfigureAwait(false);
        if (instruction is null && features is null)
            throw new ArgumentException("There is nothing to create a tone from: no description and no measured audio.", nameof(ask));

        var mapping = await mapper.MapAsync(
            new RsToneMapRequest(features, ask.Context, ask.ToneKey, ask.ToneName, ask.Instrument, ask.SongTitle, ask.Artist,
                instruction, ask.ArrangementName, ask.Previous is null ? null : RsToneJsonWriter.Write(ask.Previous)),
            ct).ConfigureAwait(false);

        if (InstrumentFault(mapping.Tone, ask.Instrument) is { } fault)
            throw new RsToneCatalogException(fault, $"the proposed tone '{ask.ToneName}'");
        return new ToneCreatorProposal(mapping.Tone, mapping.Rationale, mapping.ModelId, features is not null);
    }

    /// <summary>
    ///     Gear of the other instrument, or null. Amps and cabinets split on <see cref="RsToneGeneratePrompt.BassPrefix" />
    ///     both ways; a bass board may carry any pedal or rack (the game offers them), a guitar board no <c>Bass_</c> one.
    /// </summary>
    public static RsToneFault? InstrumentFault(RsTone tone, string instrument)
    {
        ArgumentNullException.ThrowIfNull(tone);
        var bass = string.Equals(instrument, ToneStems.Bass, StringComparison.OrdinalIgnoreCase);
        foreach (var gear in new RsGear[] { tone.Amp, tone.Cabinet })
        {
            if (IsBassGear(gear.GearKey) != bass) return Fault(gear.GearKey, bass);
        }

        if (bass) return null;
        var bassPedal = tone.PrePedals.Concat(tone.Racks).Concat(tone.PostPedals).FirstOrDefault(p => IsBassGear(p.GearKey));
        return bassPedal is null ? null : Fault(bassPedal.GearKey, bass);
    }

    /// <summary>The instruction section's body: the description, then the refinement on its own line. Null for neither.</summary>
    internal static string? InstructionFor(ToneCreatorAsk ask)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(ask.Description)) parts.Add(ask.Description.Trim());
        if (!string.IsNullOrWhiteSpace(ask.Refinement)) parts.Add("Refine it: " + ask.Refinement.Trim());
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private async Task<ToneWindowFeatures?> MeasureAsync(ToneCreatorAsk ask, CancellationToken ct)
    {
        if (ask.Window is not { } window) return null;
        MonoAudio audio;
        try
        {
            audio = await isolator.IsolateAsync(window.SongAudioPath, ask.Instrument, TimeSpan.FromMilliseconds(window.FromMs),
                TimeSpan.FromMilliseconds(window.ToMs), null, ct).ConfigureAwait(false);
        }
        catch (StemsNotCachedException)
        {
            return null;
        }

        var described = await evidence.DescribeAsync(audio, ct).ConfigureAwait(false);
        return described?.Features;
    }

    private static bool IsBassGear(string key) => key.StartsWith(RsToneGeneratePrompt.BassPrefix, StringComparison.Ordinal);

    private static RsToneFault Fault(string key, bool bass) => new(WrongInstrumentCode, key, null,
        bass
            ? $"'{key}' is guitar gear; a bass tone takes a bass amp and cabinet"
            : $"'{key}' is bass gear; a guitar tone cannot use it");
}

/// <summary>What the creator knows about the open chart: the instrument, the song's metadata and the span to measure.</summary>
public static class ToneCreatorEvidence
{
    /// <summary>The span measured when the playhead is before the first placed tone change.</summary>
    public const double FallbackSpanMs = 10_000;

    /// <summary>The stem <paramref name="arrangement" />'s tones are measured from (<see cref="ToneStems" />).</summary>
    public static string InstrumentOf(ChartProject project, Arrangement arrangement) =>
        AiToneGenerationRun.StemFor(arrangement, project);

    /// <summary>The song's metadata as the generator writes it, or null.</summary>
    public static string? ContextOf(ChartProject project, Arrangement arrangement) =>
        AiToneGenerationRun.ContextFor(project, arrangement);

    /// <summary>
    ///     The span to measure: with <paramref name="toneKey" />, a window where that tone is placed (the one holding the
    ///     playhead, else its first); otherwise the placed tone change's window (<see cref="ToneWindowPlanner" />, one
    ///     measure of lead) that holds the playhead, else the next <see cref="FallbackSpanMs" />. Null without audio or a
    ///     beat map.
    /// </summary>
    public static ToneCreatorWindow? AtPlayhead(ProjectState state, Arrangement arrangement, string? toneKey = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(arrangement);
        if (state.ActiveProject is not { } project || project.Beats.Count == 0) return null;
        if (ProjectAudioLocator.TryResolve(state.CurrentProjectPath, state.CurrentAudioFileName) is not { } audio) return null;
        return Window(audio, project, arrangement, state.CurrentTimeMs, state.TotalAudioLengthMs, toneKey);
    }

    /// <summary>The pure half of <see cref="AtPlayhead" />.</summary>
    public static ToneCreatorWindow Window(string audio, ChartProject project, Arrangement arrangement, double playheadMs, double endMs,
        string? toneKey = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        var end = endMs > 0 ? endMs : playheadMs + FallbackSpanMs;
        if (project.Beats.Count > 0 && arrangement.ToneChanges.Count > 0)
        {
            var windows = ToneWindowPlanner.Plan(arrangement.ToneChanges, project.Beats, end);
            var ofTone = toneKey is null
                ? []
                : windows.Where(w => string.Equals(w.ToneName, toneKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var tone = ofTone.FirstOrDefault(w => Holds(w, playheadMs)) ?? ofTone.FirstOrDefault();
            if (tone is not null) return new ToneCreatorWindow(audio, tone.FromMs, tone.ToMs, tone.ToneName);
            var held = windows.FirstOrDefault(w => Holds(w, playheadMs));
            if (held is not null) return new ToneCreatorWindow(audio, held.FromMs, held.ToMs);
        }

        var from = Math.Max(0, playheadMs);
        return new ToneCreatorWindow(audio, from, Math.Max(from + 1, Math.Min(end, from + FallbackSpanMs)));
    }

    private static bool Holds(ToneWindow window, double ms) => ms >= window.FromMs && ms < window.ToMs;
}
