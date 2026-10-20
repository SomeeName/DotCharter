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
    public void A_point_of_the_wrong_length_is_refused()
    {
        var tone = MakeTone(AmpA, 40);
        var space = RsToneKnobSpace.Of(tone, RsGearCatalog.Shared);

        Assert.Throws<ArgumentException>(() => space.Decode(tone, [0.5]));
    }
}
