using System.Text.Json;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tests.UI;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
///     The ABI-v34 sidechain key through the WHOLE persistence path: live chain →
///     snapshot → JSON → import → apply → live chain again.
///
///     <para>
///         <b>The bug this closes.</b> A native key does not survive a
///         remove/re-add, and <c>DcRigApplier</c> only re-pushes what a preset
///         actually CARRIES. So before this, keying a ducker off the song mix,
///         saving the tone and loading it back gave you a ducker that no longer
///         ducks — with nothing anywhere saying so, because every op in the apply
///         succeeded. It is the same shape as the MODELLESS-namamp and the
///         flattened-split-region failures the snapshot doc already records; this
///         is the third field to fall down the same hole.
///     </para>
/// </summary>
public sealed class TonePresetSidechainTests
{
    private static DspModuleType DuckerType() => new("ducker", "Ducker",
    [
        new DspParamInfo("thresholdDb", "Threshold (dB)", -60f, 0f, -30f),
        new DspParamInfo("duckDb", "Duck (dB)", 0f, 40f, 12f),
        new DspParamInfo("attackMs", "Attack (ms)", 1f, 200f, 10f),
        new DspParamInfo("holdMs", "Hold (ms)", 0f, 500f, 50f),
        new DspParamInfo("releaseMs", "Release (ms)", 10f, 2000f, 300f)
    ])
    { SupportsSidechain = true };

    private static DspModuleType RatType() => new("rat", "Rat",
    [
        new DspParamInfo("distortion", "Distortion", 0f, 1f, 0.5f),
        new DspParamInfo("filter", "Filter", 0f, 1f, 0.4f),
        new DspParamInfo("volume", "Volume", 0f, 1f, 0.5f)
    ]);

    private static RigCapabilityChain Chain() => new([RatType(), DuckerType()]);

    // ══ the whole path ═══════════════════════════════════════════════════════

    /// <summary>Capture → JSON → import → apply, on a SECOND chain. The engine
    /// really is asked for the key, and the node really does come back keyed.</summary>
    [Fact]
    public void AKeyedDucker_SurvivesSaveToJson_AndComesBackKeyedOnApply()
    {
        // ---- the player's live board: a rat into a ducker keyed off the song --
        var live = Chain();
        live.AddNode("rat");
        var ducker = live.AddNode("ducker")!;
        Assert.True(live.SetNodeSidechain(ducker.NodeId, JuceDspSidechainSource.SongMix));

        // ---- "Save as a tone" ------------------------------------------------
        var captured = DcRigSnapshot.FromNodes(live.GetNodes());
        Assert.Equal(JuceDspSidechainSource.None, captured.Modules[0].Sidechain);
        Assert.Equal(JuceDspSidechainSource.SongMix, captured.Modules[1].Sidechain);

        var json = TonePresetJsonFile.Serialize(
            new DspTonePreset("t", "T", DspToneCategory.Clean, "", captured, DspToneOrigin.User));

        // The document carries the ABI NUMBER, not a name and not a bare bool.
        Assert.Contains("\"Sidechain\": 2", json);

        // ---- somebody else opens it -----------------------------------------
        var imported = TonePresetJsonFile.Parse(json);
        Assert.True(imported.Ok, $"{imported.Fault} ({imported.Detail})");
        Assert.Equal(JuceDspSidechainSource.SongMix, imported.Preset!.Rig.Modules[1].Sidechain);

        // ---- …and applies it to a fresh chain --------------------------------
        var target = Chain();
        var result = new DcRigApplier(target).Apply(imported.Preset.Rig);

        Assert.Equal(0, result.FailedOps);
        Assert.Equal(2, result.Added);

        var applied = target.GetNodes();
        Assert.Equal(JuceDspSidechainSource.None, applied[0].Sidechain);
        Assert.Equal(JuceDspSidechainSource.SongMix, applied[1].Sidechain);

        // The engine was ASKED once, for the keyed module only — a None push on a
        // fresh node is a native call for a value it already has.
        var set = Assert.Single(target.SidechainSets);
        Assert.Equal(JuceDspSidechainSource.SongMix, set.Source);
        Assert.Equal(applied[1].NodeId, set.Node);
    }

    /// <summary>The REUSE half. A reused node keeps its native key, so applying a
    /// tone with no key has to turn it OFF — otherwise the next tone silently
    /// inherits the last one's sidechain, which is the same inheritance bug the
    /// string-param reset path exists for.</summary>
    [Fact]
    public void ApplyingAnUnkeyedTone_OverAKeyedNode_TurnsTheKeyOff()
    {
        var chain = Chain();
        var ducker = chain.AddNode("ducker")!;
        chain.SetNodeSidechain(ducker.NodeId, JuceDspSidechainSource.SeatInput);
        chain.SidechainSets.Clear();

        var unkeyed = new DcRigPreset([
            new DcRigModule("ducker", new Dictionary<string, float> { ["duckDb"] = 12f })
        ]);

        var result = new DcRigApplier(chain).Apply(unkeyed);

        Assert.Equal(0, result.FailedOps);
        Assert.Equal(0, result.Added); // the node was REUSED, not re-added
        Assert.Equal(JuceDspSidechainSource.None, chain.GetNodes()[0].Sidechain);
        Assert.Equal(JuceDspSidechainSource.None, Assert.Single(chain.SidechainSets).Source);
    }

    /// <summary>An IDENTICAL re-apply stays a NoOp: the key is diffed against the
    /// mirror like the branch tag, never re-pushed. A field that re-pushed every
    /// time would turn every tone re-select into a chain edit.</summary>
    [Fact]
    public void ReApplyingTheSameKeyedTone_IsStillANoOp()
    {
        var chain = Chain();
        var rig = DcRigTargetCatalog.FillDefaults(new DcRigPreset([
            new DcRigModule("ducker", new Dictionary<string, float>())
                { Sidechain = JuceDspSidechainSource.SongMix }
        ]));

        var applier = new DcRigApplier(chain);
        applier.Apply(rig);
        chain.SidechainSets.Clear();

        var again = applier.Apply(rig);

        Assert.True(again.NoOp, $"a re-apply moved something: {again}");
        Assert.Empty(chain.SidechainSets);
    }

    // ══ old files ════════════════════════════════════════════════════════════

    /// <summary>The compatibility fixture: a tone saved before this field existed
    /// has no <c>Sidechain</c> property at all and must load EXACTLY as it did —
    /// unkeyed, and with nothing else disturbed.</summary>
    [Fact]
    public void OldJson_WithNoSidechainField_LoadsUnchanged_AsNone()
    {
        // Verbatim shape of a pre-v34 saved tone: no Sidechain anywhere.
        const string legacy = """
        {
          "Id": "legacy-tone",
          "Name": "Legacy Tone",
          "Category": 0,
          "Description": "saved before the key existed",
          "Modules": [
            { "ModuleTypeId": "rat", "Params": { "distortion": 0.7 }, "Bypassed": false },
            { "ModuleTypeId": "ducker", "Params": { "duckDb": 9.0 }, "Bypassed": false }
          ]
        }
        """;
        Assert.DoesNotContain("Sidechain", legacy, StringComparison.OrdinalIgnoreCase);

        var imported = TonePresetJsonFile.Parse(legacy);

        Assert.True(imported.Ok, $"{imported.Fault} ({imported.Detail})");
        Assert.All(imported.Preset!.Rig.Modules, m => Assert.Equal(JuceDspSidechainSource.None, m.Sidechain));
        // …and the rest of the tone is untouched by the new field.
        Assert.Equal(["rat", "ducker"], imported.Preset.Rig.Modules.Select(m => m.ModuleTypeId));
        Assert.Equal(0.7f, imported.Preset.Rig.Modules[0].Params["distortion"], 0.0001);
        Assert.Equal(9.0f, imported.Preset.Rig.Modules[1].Params["duckDb"], 0.0001);

        // Applying it asks the engine for NO key at all — an unkeyed tone must not
        // cost a native call per module.
        var chain = Chain();
        new DcRigApplier(chain).Apply(imported.Preset.Rig);
        Assert.Empty(chain.SidechainSets);
    }

    /// <summary>An unkeyed module writes no <c>Sidechain</c> property, so the
    /// store an existing player already has does not grow a field per module the
    /// day they update.</summary>
    [Fact]
    public void None_IsOmittedFromJson_ButAKeyIsWritten()
    {
        var dto = new UserTonePresetDto
        {
            Id = "t",
            Name = "T",
            Modules =
            [
                new UserToneModuleDto { ModuleTypeId = "rat" },                     // None
                new UserToneModuleDto { ModuleTypeId = "ducker", Sidechain = 1 },   // SeatInput
            ],
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"Sidechain\":1", json);
        Assert.DoesNotContain("\"Sidechain\":0", json);
    }

    /// <summary>A key number this build has never heard of — a tone from a NEWER
    /// app, or a hand-edited file — reads as None rather than being cast blindly.
    /// An undefined value would reach
    /// <c>dca_engine_dsp_set_node_sidechain</c>, be rejected, and cost the module
    /// its audibility (a rejected configuration stays bypassed).</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    public void AKeyNumberThisBuildDoesNotKnow_ReadsAsNone(int stored)
    {
        var json = $$"""
        { "Id":"t", "Name":"T", "Modules":[
          { "ModuleTypeId":"ducker", "Params":{}, "Sidechain": {{stored}} } ] }
        """;

        var imported = TonePresetJsonFile.Parse(json);

        Assert.True(imported.Ok, $"{imported.Fault} ({imported.Detail})");
        Assert.Equal(JuceDspSidechainSource.None, imported.Preset!.Rig.Modules[0].Sidechain);
        Assert.Equal(JuceDspSidechainSource.None, UserToneModuleDto.SidechainOf(stored));
    }

    // ══ the DTO's own round trip ═════════════════════════════════════════════

    [Fact]
    public void Sidechain_RoundTrips_ThroughFromModule_ToModule_AndClone()
    {
        var preset = new DcRigPreset([
            new DcRigModule("rat", new Dictionary<string, float>()),
            new DcRigModule("ducker", new Dictionary<string, float>())
                { Sidechain = JuceDspSidechainSource.SeatInput },
        ]);
        var tone = new DspTonePreset("t", "T", DspToneCategory.Clean, "", preset, DspToneOrigin.User);

        var dto = UserTonePresetDto.FromPreset(tone);
        Assert.Equal(0, dto.Modules[0].Sidechain);
        Assert.Equal(1, dto.Modules[1].Sidechain);

        var back = dto.Clone().ToPreset();
        Assert.Equal(JuceDspSidechainSource.None, back.Rig.Modules[0].Sidechain);
        Assert.Equal(JuceDspSidechainSource.SeatInput, back.Rig.Modules[1].Sidechain);
    }

    /// <summary>The two preparation steps rewrite modules with <c>with</c>
    /// expressions, which is the only reason the key survives them — a step that
    /// rebuilt a module by hand would drop it silently, which is exactly how the
    /// string params were lost twice.</summary>
    [Fact]
    public void ThePreparationPipeline_DoesNotDropTheKey()
    {
        var preset = new DspTonePreset("t", "T", DspToneCategory.Clean, "",
            new DcRigPreset([
                new DcRigModule("ducker", new Dictionary<string, float>())
                    { Sidechain = JuceDspSidechainSource.SongMix },
            ]),
            DspToneOrigin.User);

        var prepared = DcRigPresetPreparation.Prepare(preset, namCaptureDirectory: null);

        Assert.Equal(JuceDspSidechainSource.SongMix, prepared.Modules[0].Sidechain);
        // FillDefaults ran (the module's params were empty), so this is the
        // `with` path and not a lucky pass-through.
        Assert.True(prepared.Modules[0].Params.Count >= 5);
    }

    /// <summary>The EDITED badge is a COMPARISON, so a field the comparison does
    /// not read is a change the badge cannot see — and the badge is what stops a
    /// player overwriting a tone they meant to keep.</summary>
    [Fact]
    public void TheEditedBadge_SeesAKeyChange()
    {
        var chain = Chain();
        var ducker = chain.AddNode("ducker")!;
        var saved = DcRigSnapshot.FromNodes(chain.GetNodes());

        Assert.False(DotCharter.Tone.UI.ViewModels.PlayMode.RigEditState.IsEdited(
            DcRigSnapshot.FromNodes(chain.GetNodes()), saved));

        chain.SetNodeSidechain(ducker.NodeId, JuceDspSidechainSource.SongMix);

        Assert.True(DotCharter.Tone.UI.ViewModels.PlayMode.RigEditState.IsEdited(
            DcRigSnapshot.FromNodes(chain.GetNodes()), saved));
    }
}
