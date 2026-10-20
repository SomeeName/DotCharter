// RsToneConverter's Rocksmith amps: every RS amp key becomes `rsamp`, the game's own chain for that amp, its knobs passed as positions; the per-amp level trims are RsAmpLevelTrims.
using System.Globalization;
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigTargetCatalog;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

public sealed partial class RsToneConverter
{
    /// <summary>The <c>rsamp</c> knob param ids, <c>k1</c>..<c>k10</c>.</summary>
    internal static readonly string[] RsAmpKnobParams = ["k1", "k2", "k3", "k4", "k5", "k6", "k7", "k8", "k9", "k10"];

    /// <summary>
    ///     An RS amp through the GAME'S OWN chain: <c>rsamp</c> at the amp's row,
    ///     each knob at its position between the game's own min and max. Nothing is
    ///     mapped by reading a label, so nothing folds; what the clone cannot do
    ///     exactly is reported PER KNOB (<see cref="RsAmpKnobCaveats" />).
    /// </summary>
    private static DcRigModule BuildRsAmp(RsAmp amp, int model, RsAmpModel row, RsGearCatalog catalog, ConversionReport report)
    {
        var p = new Dictionary<string, float>(StringComparer.Ordinal) { ["model"] = model };
        for (var i = 0; i < row.Knobs.Count; i++)
        {
            var knob = row.Knobs[i];
            var value = amp.KnobValues.TryGetValue(knob.Key, out var v) && float.IsFinite(v) ? v : knob.Default;
            p[RsAmpKnobParams[i]] = RsAmpChains.Position(knob, value);
        }

        if (row.MacSwaps > 0)
        {
            report.Add(ConversionNoteKind.Approximated, amp.GearKey,
                "drive stage: the game's own Mac stand-in (a Wwise Guitar Distortion block in the same soundbank) for the Windows build's iZotope Trash Multiband Distortion, whose algorithm is unpublished");
        }

        foreach (var knob in row.Knobs)
        {
            var caveats = RsAmpKnobCaveats(knob);
            if (caveats.Count == 0) continue;
            var name = catalog.TryGetKnobByFullKey(knob.Key, out var k) ? k.Name : knob.Key;
            report.Add(ConversionNoteKind.Approximated, amp.GearKey,
                string.Create(CultureInfo.InvariantCulture, $"knob '{name}': {string.Join("; ", caveats)}"));
        }

        return new DcRigModule(RsAmpChain, p);
    }

    /// <summary>
    ///     What stops one knob of the game's chain from being exact, or nothing.
    ///     A knob that only moves Guitar Distortion EQ bands (and FutzBox levels) is
    ///     the bank's numbers through a documented filter; every other stage carries
    ///     an inference (JuceSharp <c>dcdsp_rsamp.h</c> says which).
    /// </summary>
    internal static IReadOnlyList<string> RsAmpKnobCaveats(RsAmpKnob knob)
    {
        var c = new List<string>(4);
        if (knob.Stages.HasFlag(RsAmpStage.GuitarDistortion))
            c.Add("moves a Guitar Distortion drive stage (transfer curves fitted to Audiokinetic's published figures; the drive law is inferred)");
        if (knob.Stages.HasFlag(RsAmpStage.FutzDistortion))
            c.Add("moves the FutzBox distortion, whose modes are unpublished (approximated)");
        if (knob.Stages.HasFlag(RsAmpStage.FutzFilter))
            c.Add("moves a FutzBox filter (its resonance law is inferred)");
        if (knob.Stages.HasFlag(RsAmpStage.FutzEq))
            c.Add("moves the FutzBox EQ band (its Q unit is inferred)");
        if (knob.Stages.HasFlag(RsAmpStage.Compressor))
            c.Add("moves the Wwise Compressor (its detector is inferred)");
        if (knob.UnpublishedShape)
            c.Add("one of its curves is an S-curve, whose formula is unpublished (run linear)");
        return c;
    }
}
