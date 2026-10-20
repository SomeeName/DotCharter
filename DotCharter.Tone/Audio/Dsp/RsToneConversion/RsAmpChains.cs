// RsAmpChains: the managed index of the native `rsamp` table — Rocksmith 2014's own amp chains, read from the player's install at run time (RsGameTables).
namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>Which plug-in stages of an amp's chain a knob's curves move.</summary>
[Flags]
internal enum RsAmpStage
{
    None = 0,

    /// <summary>A Guitar Distortion EQ band (gain, frequency, Q, type, on): the
    /// game's numbers through a documented filter.</summary>
    Eq = 1,

    /// <summary>A Guitar Distortion block's drive stage (type, drive, tone,
    /// rectification, output, wet): the documented transfer functions, but an
    /// unpublished drive law.</summary>
    GuitarDistortion = 2,

    /// <summary>The FutzBox filters (frequency, resonance, on).</summary>
    FutzFilter = 4,

    /// <summary>The FutzBox distortion, whose modes are unpublished.</summary>
    FutzDistortion = 8,

    /// <summary>The FutzBox EQ band.</summary>
    FutzEq = 16,

    /// <summary>The FutzBox input / output gain or wet balance.</summary>
    FutzGain = 32,

    /// <summary>The Wwise Compressor.</summary>
    Compressor = 64
}

/// <summary>One knob of a Rocksmith amp, in the manifest's order (the order of
/// <c>rsamp</c>'s <c>k1</c>..<c>k10</c>).</summary>
/// <param name="Key">The knob's key, e.g. <c>Amp_GB100_Gain</c>.</param>
/// <param name="Min">The knob's minimum in the game.</param>
/// <param name="Max">The knob's maximum.</param>
/// <param name="Default">The manifest's default.</param>
/// <param name="Discrete">A switch or a selector: the native core rounds it.</param>
/// <param name="Stages">The chain stages its curves move.</param>
/// <param name="UnpublishedShape">One of its curves is an S-curve, whose formula
/// Audiokinetic does not publish (it runs linear in the stored domain).</param>
internal sealed record RsAmpKnob(string Key, float Min, float Max, float Default, bool Discrete, RsAmpStage Stages, bool UnpublishedShape);

/// <summary>One Rocksmith amp: a row of the native <c>rsamp</c> table.</summary>
/// <param name="Key">The gear key, e.g. <c>Amp_GB100</c>.</param>
/// <param name="Name">The game's display name (the native <c>model</c> label).</param>
/// <param name="Bank">The soundbank it was read from.</param>
/// <param name="Slots">How many plug-ins its chain has.</param>
/// <param name="MacSwaps">How many of them are the game's Mac stand-in for Trash.</param>
/// <param name="Knobs">Its knobs, in <c>k1</c>.. order.</param>
internal sealed record RsAmpModel(string Key, string Name, string Bank, int Slots, int MacSwaps, IReadOnlyList<RsAmpKnob> Knobs);

/// <summary>
///     Rocksmith 2014's amps as the GAME defines them: each is a chain of
///     Wwise plug-ins in its soundbank, which the native <c>rsamp</c> module runs
///     (JuceSharp <c>dcdsp_rsamp.h</c>). This is its managed index: the model
///     order, each amp's knob order and ranges, and which stages each knob moves,
///     read from the PLAYER'S OWN install (<see cref="RsGameTables" />) in the
///     same order as the native table. Empty without an install: every amp then
///     takes the generic amp mapping.
/// </summary>
internal static class RsAmpChains
{
    /// <summary>The rows in use (<see cref="RsGameTables.Current" />); empty without an install.</summary>
    internal static IReadOnlyList<RsAmpModel> Models => RsGameTables.Current.AmpModels;

    /// <summary>Whether the game's amp chains were read from an install.</summary>
    internal static bool IsAvailable => Models.Count > 0;

    /// <summary>The <c>rsamp.model</c> index of an RS amp key, or false when the
    /// game ships no such amp (the synthetic amp, a custom key) or no install was read.</summary>
    public static bool TryGetModel(string gearKey, out int model, out RsAmpModel? row)
    {
        var tables = RsGameTables.Current;
        if (tables.TryGetAmpIndex(gearKey, out model))
        {
            row = tables.AmpModels[model];
            return true;
        }

        row = null;
        return false;
    }

    /// <summary>The 0..1 position of <paramref name="value" /> on a knob, as
    /// <c>rsamp.kN</c> takes it (clamped to the knob's travel).</summary>
    public static float Position(RsAmpKnob knob, float value)
        => knob.Max > knob.Min ? Math.Clamp((value - knob.Min) / (knob.Max - knob.Min), 0f, 1f) : 0f;

    /// <summary>
    ///     What <c>rsamp</c>'s <c>k1</c>..<c>k10</c> are on model
    ///     <paramref name="model" />: the game's own knob names (the catalogue's,
    ///     e.g. "Gain", "Mid-Frequency", "30hz"), one per knob the amp has, so the
    ///     rig can label a generic knob the way the game's faceplate does. Empty
    ///     for a model the table does not have (and always without an install).
    /// </summary>
    internal static IReadOnlyList<string> KnobNames(int model)
    {
        var models = Models;
        if (model < 0 || model >= models.Count) return [];
        var row = models[model];
        var names = new string[row.Knobs.Count];
        for (var i = 0; i < names.Length; i++)
        {
            var key = row.Knobs[i].Key;
            names[i] = Tones.RsGearCatalog.Shared.TryGetKnobByFullKey(key, out var knob) ? knob.Name : key[(row.Key.Length + 1)..];
        }

        return names;
    }
}

/// <summary>
///     The rig UI's view of Rocksmith's amp chains: <c>rsamp</c>'s ten knobs are
///     positions of whichever amp its <c>model</c> names, so the faceplate labels
///     them per model (<c>PedalNodeViewModel</c>).
/// </summary>
public static class RsAmpKnobs
{
    /// <summary>The module type id.</summary>
    public const string ModuleTypeId = DcRigTargetCatalog.RsAmpChain;

    /// <summary>The knob param ids, <c>k1</c>..<c>k10</c>, in knob order.</summary>
    public static IReadOnlyList<string> ParamIds => RsToneConverter.RsAmpKnobParams;

    /// <summary>The game's names for model <paramref name="model" />'s knobs, in
    /// <see cref="ParamIds" /> order; an amp with fewer knobs leaves the rest unnamed.</summary>
    public static IReadOnlyList<string> NamesFor(int model) => RsAmpChains.KnobNames(model);

    /// <summary>Whether the amp chains were read from a Rocksmith 2014 install.
    /// Without one the module still runs, but nothing can say what its knobs are.</summary>
    public static bool Available => RsAmpChains.IsAvailable;
}
