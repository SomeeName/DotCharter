using DotCharter.Services.Ai.Generate;
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tests.Ai.Generate.RsToneLoopFakes;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>The knob search's space: the catalogue's own ranges, the gear and the knob set fixed, every decode valid.</summary>
public sealed class RsToneKnobSpaceTests
{
    [Fact]
    public void Every_continuous_knob_the_tone_sets_is_an_axis_in_key_order()
    {
        var tone = MakeTone(AmpA, 40);
        var space = RsToneKnobSpace.Of(tone, RsGearCatalog.Shared);

        Assert.Equal(tone.InSignalOrder().SelectMany(g => g.KnobValues.Keys.Order(StringComparer.Ordinal)), space.Keys);
        Assert.Equal((40 - 1) / 99.0, space.Encode(tone)[0], 6);   // Amp_AT120_Bass: 1..100
    }

    [Fact]
    public void The_ends_of_the_unit_box_are_the_catalogues_ends_and_a_step_is_snapped()
    {
        var tone = MakeTone(AmpA, 40);
        var space = RsToneKnobSpace.Of(tone, RsGearCatalog.Shared);
        Assert.True(RsGearCatalog.Shared.TryGetKnobByFullKey(space.Keys[0], out var bass));

        var low = space.Decode(tone, Enumerable.Repeat(0.0, space.Count).ToArray());
        var high = space.Decode(tone, Enumerable.Repeat(1.0, space.Count).ToArray());
        var between = space.Decode(tone, [0.5004, .. Enumerable.Repeat(0.5, space.Count - 1)]);

        Assert.Equal(bass.MinValue, low.Amp.KnobValues[space.Keys[0]]);
        Assert.Equal(bass.MaxValue, high.Amp.KnobValues[space.Keys[0]]);
        Assert.Equal(51f, between.Amp.KnobValues[space.Keys[0]]);   // 1 + 0.5004 · 99 = 50.54 → the step of 1
        Assert.Equal(tone.Amp.GearKey, high.Amp.GearKey);
        Assert.Equal(tone.Cabinet.GearKey, high.Cabinet.GearKey);
    }

    [Fact]
    public void Two_instances_of_one_pedal_are_two_axes_each_decoded_onto_its_own_slot()
    {
        var catalog = RsGearCatalog.Shared;
        var pedal = RsToneAuthoring.GearOptions(catalog, RsGearType.Pedal).First(g => g.Knobs?.Any(Continuous) == true);
        var knob = pedal.Knobs!.First(Continuous);
        var lo = Math.Min(knob.MinValue, knob.MaxValue);
        var hi = Math.Max(knob.MinValue, knob.MaxValue);
        RsPedal Slot(int slot, float value) => new(RsGearRole.PrePedal, slot, pedal.Key, RsGearType.Pedal, pedal.Category,
            pedal.Knobs!.ToDictionary(k => k.Key, k => k.Key == knob.Key ? value : k.DefaultValue, StringComparer.Ordinal));
        var tone = MakeTone(AmpA, 40) with { PrePedals = [Slot(1, lo), Slot(2, hi)] };
        var space = RsToneKnobSpace.Of(tone, catalog);

        var axes = space.Keys.Select((key, i) => (key, i)).Where(x => x.key == knob.Key).Select(x => x.i).ToArray();
        Assert.Equal(2, axes.Length);
        var encoded = space.Encode(tone);
        Assert.Equal(0.0, encoded[axes[0]], 6);
        Assert.Equal(1.0, encoded[axes[1]], 6);

        var point = encoded.ToArray();
        point[axes[0]] = 1.0;
        point[axes[1]] = 0.0;
        var decoded = space.Decode(tone, point);

        Assert.Equal(lo, decoded.PrePedals[1].KnobValues[knob.Key]);
        Assert.True(decoded.PrePedals[0].KnobValues[knob.Key] > lo);
    }

    private static bool Continuous(RsGearKnob knob) => knob.UnitType != RsKnobUnit.Enum && knob.MaxValue != knob.MinValue;

    [Fact]
    public void A_point_of_the_wrong_length_is_refused()
    {
        var tone = MakeTone(AmpA, 40);
        var space = RsToneKnobSpace.Of(tone, RsGearCatalog.Shared);

        Assert.Throws<ArgumentException>(() => space.Decode(tone, [0.5]));
    }
}
