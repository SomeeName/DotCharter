using DotCharter.Services.Ai.Generate;
using DotCharter.Tests.Audio.Dsp.RsToneConversion.Coverage;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     The rig → Rocksmith inverse (<see cref="RigToRsToneConverter" />): every gear the coverage
///     audit calls modelled comes back as itself (or a gear the rig cannot tell from it) with its
///     knobs, every loss is named, a rail over four slots is refused, and every tone it writes
///     passes the catalogue gate.
/// </summary>
public sealed class RigToRsToneConverterTests
{
    private static readonly Lazy<IReadOnlyList<GearCoverage>> Modelled = new(() =>
    {
        using var none = SyntheticRsGame.UseNone();
        return [.. RsGearCoverageAudit.Run(RsGearCatalog.Shared, RsGearOverrides.Shared, RsGearLibraryUsage.Empty)
            .Where(r => r.Status == CoverageStatus.Modelled)];
    });

    [Fact]
    public void EveryModelledGear_RoundTrips_ToTheSameGearAndKnobs()
    {
        using var none = SyntheticRsGame.UseNone();
        var failures = new List<string>();
        var aliases = 0;
        Assert.True(Modelled.Value.Count > 400, $"only {Modelled.Value.Count} modelled rows — the audit probe is broken");

        foreach (var row in Modelled.Value)
        {
            var gear = row.Gear;
            var knobs = OffDefaultKnobs(gear);
            var rig = Forward(gear, knobs);
            // A rack sits after the amp in Rocksmith; before it, a pedal of the same module wins.
            var amp = gear.Type == "Racks" ? Forward(Gear("Amp_TW22"), RsToneAuthoring.DefaultKnobValues(Gear("Amp_TW22"))) : [];
            var result = RigToRsToneConverter.Convert(new DcRigPreset([.. amp, .. rig]), "RoundTrip", "Round trip");

            if (!RsToneCatalogValidation.IsValid(result.Tone, RsGearCatalog.Shared))
            {
                failures.Add($"{gear.Key}: {RsToneCatalogValidation.FirstFault(result.Tone, RsGearCatalog.Shared)?.Message}");
                continue;
            }

            var back = GearAt(result, amp.Count);
            var backData = RsGearCatalog.Shared.TryGetGear(back.GearKey, out var d) ? d : null;
            var again = backData is null ? [] : Forward(backData, back.KnobValues);
            var distance = Distance(rig, again);
            if (distance > 1e-9)
            {
                failures.Add($"{gear.Key} → {back.GearKey}: forward distance {distance:E2}");
                continue;
            }

            if (back.GearKey != gear.Key)
            {
                aliases++;
                continue;
            }

            foreach (var knob in gear.Knobs ?? [])
            {
                if (!row.Knobs.First(k => k.Knob.Key == knob.Key).Lands) continue;
                var want = knobs[knob.Key];
                var got = back.KnobValues[knob.Key];
                var resolution = Math.Max(knob.ValueStep, Math.Abs(knob.MaxValue - knob.MinValue) / 100f) + 1e-4f;
                if (Math.Abs(want - got) <= resolution) continue;
                // A knob the conversion is flat around (an enum's equal options, a stepped range)
                // cannot be told back; one that changes the sound must come back.
                var swapped = new Dictionary<string, float>(back.KnobValues, StringComparer.Ordinal) { [knob.Key] = want };
                if (Distance(again, Forward(gear, swapped)) > 1e-9) failures.Add($"{knob.Key}: wanted {want}, got {got}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} round trip(s) failed ({aliases} aliases):\n  " + string.Join("\n  ", failures.Take(40)));
    }

    [Fact]
    public void AWholeTone_RoundTrips_SlotForSlot_AndPassesTheGate()
    {
        using var none = SyntheticRsGame.UseNone();
        var tone = Tone(
            pre: ["Pedal_NoiseGate", "Pedal_Chorus"],
            amp: "Amp_TW22",
            cab: "Cab_Marshall1960TV_Ribbon_Cone",
            racks: ["Rack_StudioCompressor"],
            post: ["Pedal_DigitalVerb"]);
        var rig = new RsToneConverter().Convert(tone, out _);

        var result = RigToRsToneConverter.Convert(rig, tone.Key, tone.Name);

        Assert.False(result.IsRefused);
        Assert.True(RsToneCatalogValidation.IsValid(result.Tone, RsGearCatalog.Shared),
            RsToneCatalogValidation.FirstFault(result.Tone, RsGearCatalog.Shared)?.Message);
        var back = result.Tone!;
        Assert.Equal(tone.PrePedals.Count, back.PrePedals.Count);
        Assert.Equal(tone.Racks.Count, back.Racks.Count);
        Assert.Equal(tone.PostPedals.Count, back.PostPedals.Count);
        foreach (var (want, got) in tone.InSignalOrder().Zip(back.InSignalOrder())) AssertSameSound(want, got);
        Assert.Equal([1, 2], back.PrePedals.Select(p => p.SlotIndex));
        Assert.True(result.Closeness > 0.99f, $"closeness {result.Closeness}");
        Assert.DoesNotContain(result.Losses, l => l.Kind is RigToneLossKind.NoCounterpart or RigToneLossKind.NoAmp or RigToneLossKind.NoCabinet);
    }

    [Fact]
    public void WhatHasNoCounterpart_IsNamed_NeverSilent()
    {
        using var none = SyntheticRsGame.UseNone();
        var cab = Forward(Gear("Cab_Marshall1960TV_Ribbon_Cone"), new Dictionary<string, float>());
        var rig = new DcRigPreset(
        [
            new DcRigModule(DcRigTargetCatalog.NamAmp, new Dictionary<string, float>())
            {
                StringParams = new Dictionary<string, string> { ["modelPath"] = @"C:\captures\My Plexi.nam" }
            },
            cab[0] with { StringParams = new Dictionary<string, string> { ["irPath"] = @"D:\irs\Greenback.wav" } },
            .. cab.Skip(1),
            new DcRigModule(DcRigTargetCatalog.Splitter, new Dictionary<string, float>()),
            new DcRigModule(DcRigTargetCatalog.Looper, new Dictionary<string, float>()),
            new DcRigModule(DcRigTargetCatalog.Merger, new Dictionary<string, float>()),
            .. Forward(Gear("Pedal_Chorus"), new Dictionary<string, float>()).Select(m => m with { Bypassed = true })
        ]);

        var result = RigToRsToneConverter.Convert(rig, "Losses", "Losses");

        Assert.Contains(result.Losses, l => l is { Kind: RigToneLossKind.NamCapture, Detail: "My Plexi" });
        Assert.Contains(result.Losses, l => l is { Kind: RigToneLossKind.ExternalIr, Detail: "Greenback.wav" });
        Assert.Contains(result.Losses, l => l is { Kind: RigToneLossKind.ParallelFlattened });
        Assert.Contains(result.Losses, l => l is { Kind: RigToneLossKind.NoCounterpart, Module: DcRigTargetCatalog.Looper });
        Assert.Contains(result.Losses, l => l is { Kind: RigToneLossKind.Bypassed, Module: DcRigTargetCatalog.Chorus });
        Assert.True(RsToneCatalogValidation.IsValid(result.Tone, RsGearCatalog.Shared));
        Assert.Contains(result.Slots, s => s is { Role: RsGearRole.Amp, IsStandIn: true });
        Assert.True(result.Closeness < 0.9f, $"a rig that lost its amp scored {result.Closeness}");
    }

    [Fact]
    public void AnEmptyRig_StillWritesALoadableTone_AndSaysWhatStoodIn()
    {
        var result = RigToRsToneConverter.Convert(new DcRigPreset([]), "Empty", "Empty");

        Assert.Contains(result.Losses, l => l.Kind == RigToneLossKind.NoAmp);
        Assert.Contains(result.Losses, l => l.Kind == RigToneLossKind.NoCabinet);
        Assert.True(RsToneCatalogValidation.IsValid(result.Tone, RsGearCatalog.Shared));
    }

    [Fact]
    public void FivePedalsBeforeTheAmp_AreRefused_UntilOneIsLeftOut()
    {
        using var none = SyntheticRsGame.UseNone();
        var keys = new[] { "Pedal_NoiseGate", "Pedal_Chorus", "Pedal_Compression", "Pedal_ClassicFlanger", "Pedal_DigitalVerb" };
        var tone = Tone(pre: [.. keys.Take(4)], amp: "Amp_TW22", cab: "Cab_Marshall1960TV_Ribbon_Cone", racks: [], post: []);
        var four = new RsToneConverter().Convert(tone, out _);
        var extra = Forward(Gear(keys[4]), RsToneAuthoring.DefaultKnobValues(Gear(keys[4])));
        var rig = new DcRigPreset([.. four.Modules.Take(4), .. extra, .. four.Modules.Skip(4)]);

        var refused = RigToRsToneConverter.Convert(rig, "Five", "Five");

        Assert.True(refused.IsRefused);
        Assert.Null(refused.Tone);
        Assert.Contains(refused.Overflows, o => o is { Rail: RsGearRole.PrePedal, Count: 5 });

        var dropped = refused.Slots.Single(s => s.ModuleIndex == 4).ModuleIndex;
        var accepted = RigToRsToneConverter.Convert(rig, "Five", "Five", new HashSet<int> { dropped });

        Assert.False(accepted.IsRefused);
        Assert.Contains(accepted.Losses, l => l is { Kind: RigToneLossKind.LeftOut, ModuleIndex: 4 });
        Assert.Contains(accepted.Slots, s => s is { ModuleIndex: 4, Included: false, SlotIndex: 0 });
        Assert.Equal(4, accepted.Tone!.PrePedals.Count);
        foreach (var (want, got) in tone.PrePedals.Zip(accepted.Tone.PrePedals)) AssertSameSound(want, got);
        Assert.True(RsToneCatalogValidation.IsValid(accepted.Tone, RsGearCatalog.Shared));
    }

    [Fact]
    public void KnobSearch_NeverLeavesTheCatalogueRange()
    {
        using var none = SyntheticRsGame.UseNone();
        var chorus = Forward(Gear("Pedal_Chorus"), RsToneAuthoring.DefaultKnobValues(Gear("Pedal_Chorus")));
        var pushed = chorus.Select(m => m with { Params = m.Params.ToDictionary(kv => kv.Key, kv => kv.Value * 50f + 1000f) }).ToList();

        var result = RigToRsToneConverter.Convert(new DcRigPreset(pushed), "Far", "Far");

        Assert.True(RsToneCatalogValidation.IsValid(result.Tone, RsGearCatalog.Shared),
            RsToneCatalogValidation.FirstFault(result.Tone, RsGearCatalog.Shared)?.Message);
        Assert.Contains(result.Losses, l => l.Kind == RigToneLossKind.KnobsApproximate);
    }

    // ------------------------------------------------------------------ helpers

    private static double Distance(IReadOnlyList<DcRigModule> a, IReadOnlyList<DcRigModule> b)
        => RigToRsKnobSearch.Distance(a, b);

    private static RsGearData Gear(string key)
        => RsGearCatalog.Shared.TryGetGear(key, out var gear) ? gear : throw new InvalidOperationException(key);

    private static IReadOnlyList<DcRigModule> Forward(RsGearData gear, IReadOnlyDictionary<string, float> knobs)
        => RsToneConverter.ConvertGear(RigToRsGearIndex.MakeGear(gear, knobs), RsGearCatalog.Shared, RsGearOverrides.Shared, new ConversionReport());

    /// <summary>The gear the rig module at <paramref name="moduleIndex" /> became, read back from the tone.</summary>
    private static RsGear GearAt(RigToRsToneResult result, int moduleIndex)
    {
        var slot = result.Slots.Single(s => s.ModuleIndex == moduleIndex);
        var tone = result.Tone!;
        return slot.Role switch
        {
            RsGearRole.Amp => tone.Amp,
            RsGearRole.Cabinet => tone.Cabinet,
            RsGearRole.Rack => tone.Racks[slot.SlotIndex - 1],
            RsGearRole.PostPedal => tone.PostPedals[slot.SlotIndex - 1],
            _ => tone.PrePedals[slot.SlotIndex - 1]
        };
    }

    /// <summary>Two gears the rig cannot tell apart: the same forward conversion at their knobs.</summary>
    private static void AssertSameSound(RsGear expected, RsGear actual)
    {
        var distance = Distance(Forward(Gear(expected.GearKey), expected.KnobValues), Forward(Gear(actual.GearKey), actual.KnobValues));
        Assert.True(distance < 1e-9, $"{expected.GearKey} came back as {actual.GearKey} ({distance:E2} apart)");
    }

    /// <summary>Each knob a third of the way up its range, on its step; an enum on its second option.</summary>
    private static Dictionary<string, float> OffDefaultKnobs(RsGearData gear)
    {
        var values = new Dictionary<string, float>(RsToneAuthoring.DefaultKnobValues(gear), StringComparer.Ordinal);
        foreach (var knob in gear.Knobs ?? [])
        {
            var lo = Math.Min(knob.MinValue, knob.MaxValue);
            var hi = Math.Max(knob.MinValue, knob.MaxValue);
            values[knob.Key] = RigToRsKnobSearch.Snap(knob, knob.UnitType == RsKnobUnit.Enum ? 1 : lo + ((hi - lo) / 3f));
        }

        return values;
    }

    private static RsTone Tone(string[] pre, string amp, string cab, string[] racks, string[] post)
    {
        static RsPedal Pedal(RsGearRole role, int i, string key, RsGearType type)
        {
            var g = Gear(key);
            return new RsPedal(role, i + 1, key, type, g.Category, OffDefaultKnobs(g));
        }

        var a = Gear(amp);
        return new RsTone("Whole", "Whole", -12f, [RsToneAuthoring.CleanDescriptorUIName],
            new RsAmp(amp, a.Category, OffDefaultKnobs(a)),
            new RsCab(cab, Gear(cab).Category, new Dictionary<string, float>()),
            [.. pre.Select((k, i) => Pedal(RsGearRole.PrePedal, i, k, RsGearType.Pedal))],
            [.. racks.Select((k, i) => Pedal(RsGearRole.Rack, i, k, RsGearType.Rack))],
            [.. post.Select((k, i) => Pedal(RsGearRole.PostPedal, i, k, RsGearType.Pedal))]);
    }
}
