using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Tools.RigDataset.Beds;

/// <summary>One note in a bed layer, on the bed's own beat grid.</summary>
/// <param name="StartBeats">Onset, in beats from the start of the bed.</param>
/// <param name="LengthBeats">Sounding length, in beats.</param>
/// <param name="Midi">MIDI note number. For a drum layer this is a General-MIDI-ish
/// drum key — 36 kick, 38 snare, 42 closed hat, 46 open hat — and not a pitch.</param>
/// <param name="Velocity">0…1.</param>
public sealed record BedNote(double StartBeats, double LengthBeats, int Midi, float Velocity);

/// <summary>A module in a bed layer's rig, as the sidecar records it.</summary>
/// <param name="ModuleTypeId">The engine catalogue type id.</param>
/// <param name="Params">Param id → value, already clamped to the catalogue range.</param>
public sealed record BedRigModule(string ModuleTypeId, IReadOnlyDictionary<string, float> Params);

/// <summary>
///     One synthesised layer of a bed: what to play, what to play it with, and how
///     loud it sits in the sum.
/// </summary>
/// <param name="Name">A human label — <c>bass</c>, <c>drums</c>, <c>keys</c>.</param>
/// <param name="Voice">Which synthesis voice renders it: see
/// <see cref="BedVoices" />.</param>
/// <param name="Notes">The note list, already laid out over the whole bed.</param>
/// <param name="Rig">The engine chain the layer is rendered through.</param>
/// <param name="LevelDb">The layer's level in the sum, relative to the bed's own
/// reference, before the bed is normalised as a whole.</param>
public sealed record BedLayerPlan(
    string Name,
    string Voice,
    IReadOnlyList<BedNote> Notes,
    IReadOnlyList<BedRigModule> Rig,
    float LevelDb);

/// <summary>The three synthesis voices a bed layer can use.</summary>
public static class BedVoices
{
    /// <summary>Plucked monophonic bass: saw + sub sine through a decaying one-pole.</summary>
    public const string Bass = "bass";

    /// <summary>Synthesised kit: pitch-swept sine kick, band-passed noise snare, hats.</summary>
    public const string Drums = "drums";

    /// <summary>Slow-attack detuned-saw pad, three to four voices per chord.</summary>
    public const string Keys = "keys";
}

/// <summary>
///     A whole backing bed, fully determined by its seed. <b>The plan is the
///     product</b> — the audio is a pure function of it plus the engine, so the
///     sidecar is enough to re-render the bed, to say what is in it, and to prove
///     nobody else's audio went into it.
/// </summary>
/// <param name="Index">The bed's index in the run; also part of its seed.</param>
/// <param name="Seed">The derived seed every draw in this plan came from.</param>
/// <param name="Seconds">Length.</param>
/// <param name="SampleRate">48 000.</param>
/// <param name="TempoBpm">Tempo.</param>
/// <param name="KeyRootMidi">The tonic, as a MIDI note in octave 3 (48…59).</param>
/// <param name="KeyName">The tonic's name, e.g. <c>D</c>.</param>
/// <param name="Scale"><c>major</c> or <c>minor</c>.</param>
/// <param name="Progression">The chord degrees, one per bar, cycled over the bed.</param>
/// <param name="BeatsPerBar">4, always, today.</param>
/// <param name="Layers">The layers, in sum order.</param>
public sealed record BedPlan(
    int Index,
    int Seed,
    double Seconds,
    int SampleRate,
    double TempoBpm,
    int KeyRootMidi,
    string KeyName,
    string Scale,
    IReadOnlyList<int> Progression,
    int BeatsPerBar,
    IReadOnlyList<BedLayerPlan> Layers)
{
    /// <summary>The bed's WAV name, relative to the beds directory.</summary>
    public string WavName => $"bed_{Index:D3}.wav";

    /// <summary>The sidecar's name, relative to the beds directory.</summary>
    public string JsonName => $"bed_{Index:D3}.json";
}

/// <summary>
///     The <c>.json</c> written beside every bed WAV.
///     <para>
///         <b>The licence line is not decoration.</b> A bed folder is the one place
///         in this corpus where somebody else's recording could plausibly end up —
///         MUSDB18-HQ and MoisesDB are both non-commercial and both are one download
///         away — so every bed we generate states, in the file itself, that it was
///         synthesised from the app's own DSP and carries no third-party audio. A
///         bed with no sidecar is a bed nobody can vouch for; treat it as unusable.
///     </para>
/// </summary>
/// <param name="Schema">Sidecar schema version.</param>
/// <param name="Licence">The licence line. Always <see cref="BedSidecar.OursLicence" />
/// for a generated bed.</param>
/// <param name="Generator">Tool and subcommand that wrote it.</param>
/// <param name="GeneratedUtc">When.</param>
/// <param name="Plan">The full plan — enough to re-render bit-identically.</param>
/// <param name="RmsDbfs">The written file's measured RMS.</param>
/// <param name="PeakDbfs">The written file's measured peak.</param>
public sealed record BedSidecar(
    int Schema,
    string Licence,
    string Generator,
    DateTimeOffset GeneratedUtc,
    BedPlan Plan,
    float RmsDbfs,
    float PeakDbfs)
{
    /// <summary>The current sidecar schema.</summary>
    public const int CurrentSchema = 1;

    /// <summary>The one licence a generated bed may carry.</summary>
    public const string OursLicence =
        "ours — synthesised by DotCharter.Tools.RigDataset 'beds' from the app's own DSP modules. "
        + "No third-party audio, no sample library, no separated commercial recording. "
        + "Free to train on and to redistribute as part of a shipped product.";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>Serialises the sidecar. Invariant by construction —
    /// <c>System.Text.Json</c> writes numbers invariantly, which is why the tags in
    /// <see cref="Augmentation" /> have to be built by hand and this does not.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a sidecar back.</summary>
    /// <param name="json">The file's contents.</param>
    public static BedSidecar? FromJson(string json) => JsonSerializer.Deserialize<BedSidecar>(json, Options);

    /// <summary>Builds a sidecar for a rendered bed.</summary>
    /// <param name="plan">The plan the audio came from.</param>
    /// <param name="audio">The rendered, normalised bed.</param>
    /// <param name="nowUtc">Injected so a test is not timing-dependent.</param>
    public static BedSidecar For(BedPlan plan, ReadOnlySpan<float> audio, DateTimeOffset nowUtc)
        => new(CurrentSchema, OursLicence, "DotCharter.Tools.RigDataset beds", nowUtc, plan,
            Augmentation.Db(Augmentation.Rms(audio)), Augmentation.Db(Augmentation.Peak(audio)));

    /// <summary>The one-line summary the operator sees per bed.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"bed {Plan.Index:D3}: {Plan.Seconds:F0}s {Plan.TempoBpm:F0}bpm {Plan.KeyName} {Plan.Scale}, "
        + $"{Plan.Layers.Count} layers, {RmsDbfs:F1} dBFS rms / {PeakDbfs:F1} peak");
}
