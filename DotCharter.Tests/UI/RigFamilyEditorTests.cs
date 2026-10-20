using System.Xml.Linq;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The five SPECIALISED module editors — graphic EQ, parametric EQ, the
///     expression treadle, the vocal devices and the dynamics rack.
///
///     <para>
///         <b>The fault these exist for is a control that is not there.</b> A
///         hand-arranged editor names the params it knows about, and a param it
///         forgets is not a visual bug: it is a knob the player can no longer
///         reach, on a module that still HAS it. So the contract is a set
///         equation — everything the family projections expose is exactly
///         <c>KnobParams</c> ∪ <c>ModelSelectors</c> ∪ the free-text strings,
///         each exactly once, and each the SAME live instance the board card
///         binds.
///     </para>
///     <para>
///         The catalogue below is written out rather than read from the native
///         DLL on purpose: the ids and ranges are the ones
///         <c>DspModuleFactory.cpp</c> declares, so the theory runs identically
///         on a machine with no engine. The native side is pinned separately by
///         <see cref="AmpFxRigParameterSetTests" /> and
///         <see cref="PedalboardSkinReachabilityTests" />.
///     </para>
/// </summary>
public sealed class RigFamilyEditorTests
{
    [Theory]
    [InlineData("graphiceq", RigEditorFamily.GraphicEq)]
    [InlineData("eq", RigEditorFamily.ParametricEq)]
    [InlineData("wah", RigEditorFamily.Expression)]
    [InlineData("whammy", RigEditorFamily.Expression)]
    [InlineData("swell", RigEditorFamily.Expression)]
    [InlineData("miku", RigEditorFamily.Vocal)]
    [InlineData("mikustomp", RigEditorFamily.Vocal)]
    [InlineData("compressor", RigEditorFamily.Dynamics)]
    [InlineData("gate", RigEditorFamily.Dynamics)]
    [InlineData("noisereducer", RigEditorFamily.Dynamics)]
    public void EveryFamilyProjection_ReachesEveryParamTheModuleHas(string moduleId, RigEditorFamily family)
    {
        var dock = Dock(moduleId);
        Assert.Equal(family, dock.Family);

        var knobs = ProjectedKnobs(dock).ToArray();
        Assert.Equal(knobs.Length, knobs.Distinct().Count());
        Assert.Equal(
            dock.Node.KnobParams.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal),
            knobs.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));
        foreach (var knob in knobs)
            Assert.Contains(dock.Node.Params, actual => ReferenceEquals(actual, knob));

        var choices = ProjectedChoices(dock).ToArray();
        Assert.Equal(choices.Length, choices.Distinct().Count());
        Assert.Equal(
            dock.Node.ModelSelectors.Select(s => s.ParamId).OrderBy(id => id, StringComparer.Ordinal),
            choices.Select(s => s.ParamId).OrderBy(id => id, StringComparer.Ordinal));
        foreach (var choice in choices)
            Assert.Contains(dock.Node.ModelSelectors, actual => ReferenceEquals(actual, choice));

        Assert.Equal(
            dock.Node.StringParams.Where(p => p.IsLyrics).Select(p => p.Id),
            dock.LyricParams.Select(p => p.Id));
        foreach (var lyric in dock.LyricParams)
            Assert.Contains(dock.Node.StringParams, actual => ReferenceEquals(actual, lyric));
    }

    /// <summary>
    ///     The ten modules the five families cover are ALL of the ones
    ///     <see cref="RigArtwork.FamilyFor" /> routes to them. A module folded
    ///     onto a specialised family with no case in the theory above would open
    ///     an editor nothing has ever checked for completeness.
    /// </summary>
    [Fact]
    public void TheTheoryCovers_EveryModuleFoldedOntoASpecialisedFamily()
    {
        var specialised = new[]
        {
            RigEditorFamily.GraphicEq, RigEditorFamily.ParametricEq,
            RigEditorFamily.Expression, RigEditorFamily.Vocal, RigEditorFamily.Dynamics
        };

        var routed = RigArtwork.IllustratedIds
            .Where(id => specialised.Contains(RigArtwork.FamilyFor(id)))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Catalogue().Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal), routed);
    }

    /// <summary>
    ///     The families' free-text params open on the STORED value. A field that
    ///     opens blank looks like an empty phrase list and overwrites the real
    ///     one the moment Save is pressed — and the stomp has three of them,
    ///     where the generic editor seeds only the first.
    /// </summary>
    [Fact]
    public void EveryLyricField_OpensOnItsStoredValue()
    {
        var dock = Dock("mikustomp");
        Assert.Equal(3, dock.LyricParams.Count);
        foreach (var lyric in dock.LyricParams)
        {
            lyric.Value = "la " + lyric.Id;
            lyric.BeginLyricsEdit();
            Assert.Equal(lyric.Value, lyric.LyricsDraft);
        }

        // …and a second unit over the same node re-seeds all three.
        var reopened = new RigDockUnit(dock.Node);
        Assert.Equal(dock.LyricParams.Select(p => p.Value), reopened.LyricParams.Select(p => p.LyricsDraft));
    }

    /// <summary>
    ///     A dynamics section carries the params of the module in front of you,
    ///     never a fixed table: the three modules share a family and not a
    ///     control set. The headings are resource keys, so the rack never prints
    ///     text the markup typed.
    /// </summary>
    [Theory]
    [InlineData("compressor", "Rig_Editor_Sec_Detection", "Rig_Editor_Sec_Envelope", "Rig_Editor_Sec_Output")]
    [InlineData("gate", "Rig_Editor_Sec_Threshold", "Rig_Editor_Sec_Envelope")]
    [InlineData("noisereducer", "Rig_Editor_Sec_Reduction", "Rig_Editor_Sec_HumFilter")]
    public void EachDynamicsModule_GetsItsOwnSections(string moduleId, params string[] keys)
    {
        var dock = Dock(moduleId);
        Assert.Equal(keys, dock.DynamicsSections.Select(s => s.TitleKey));
        Assert.All(dock.DynamicsSections, s => Assert.NotEmpty(s.Knobs));
    }

    /// <summary>The treadle and its fader are ONE param, and the words on the
    /// plate follow the module rather than the family.</summary>
    [Theory]
    [InlineData("wah", "Rig_Editor_Expr_Plate_Wah", "FireRigAccentWah")]
    [InlineData("whammy", "Rig_Editor_Expr_Plate_Whammy", "FireRigAccentWhammy")]
    [InlineData("swell", "Rig_Editor_Expr_Plate_Swell", "FireRigAccentWah")]
    public void TheTreadle_NamesOnePositionParamAndItsOwnPlate(string moduleId, string plateKey, string accentKey)
    {
        var dock = Dock(moduleId);
        Assert.Same(Assert.Single(dock.Node.Params, p => p.Id == "position"), dock.ExpressionPosition);
        Assert.DoesNotContain(dock.ExpressionKnobs, p => p.Id == "position");
        Assert.Equal(dock.SectionLabel(plateKey), dock.ExpressionPlate);
        Assert.Equal(accentKey, dock.ExpressionAccentKey);
    }

    // ══ The view each family opens ═══════════════════════════════════════════

    // `BuildFor` CONSTRUCTS a view, so its theory lives in
    // `Perf/RigFamilyEditorsUiPerfSmokeTests.BuildFor_OpensTheViewItsFamilyNames`
    // and not here: inflating one of these views in the default gate creates
    // live `{localization:Loc}` tooltip bindings, and a culture write from a
    // parallel collection then re-evaluates them off the dispatcher thread —
    // which took thirteen localization cases down in one gate run while this
    // class passed in isolation (the shape `DotCharter.Tests/AGENTS.md`
    // describes under the ToolTipHoverPolicy race).

    /// <summary>
    ///     …and the view really BINDS the projections its family exposes. The
    ///     set equation above is over view-model properties; without this it
    ///     would pass over a view that renders none of them — the vacuous shape
    ///     this repo has already shipped more than once.
    /// </summary>
    [Theory]
    [InlineData("RigGraphicEqView.axaml", "GraphicEqBands", "GraphicEqLevel", "MoreKnobs", "MoreChoices")]
    [InlineData("RigParametricEqView.axaml", "ParametricSections", "Knobs", "MoreKnobs", "MoreChoices")]
    [InlineData("RigExpressionView.axaml", "ExpressionPosition", "ExpressionChoices", "ExpressionKnobs", "MoreKnobs")]
    [InlineData("RigVocalView.axaml", "VocalMainKnobs", "VocalArticulation", "VocalPattern", "VocalOctave",
        "LyricParams", "MoreKnobs", "MoreChoices")]
    [InlineData("RigDynamicsView.axaml", "DynamicsSections", "Knobs", "MoreKnobs", "MoreChoices")]
    public void EachFamilyView_BindsItsOwnProjections(string file, params string[] projections)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "RigEditors", file);
        var bound = XDocument.Load(path).Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .ToList();

        foreach (var projection in projections)
        {
            Assert.True(bound.Any(v => v.Contains("{Binding " + projection, StringComparison.Ordinal)),
                $"{file} never binds {projection} — the params it carries reach no control");
        }

        // The bypass foot is the one control every family view has to have.
        Assert.Contains(bound, v => v == "{Binding Node.Bypassed, Mode=TwoWay}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<PedalParamViewModel> ProjectedKnobs(RigDockUnit dock) => dock.Family switch
    {
        RigEditorFamily.GraphicEq => dock.GraphicEqBands
            .Concat(One(dock.GraphicEqLevel))
            .Concat(dock.MoreKnobs),
        RigEditorFamily.ParametricEq => dock.ParametricSections.SelectMany(s => s.Knobs).Concat(dock.MoreKnobs),
        RigEditorFamily.Expression => One(dock.ExpressionPosition)
            .Concat(dock.ExpressionKnobs)
            .Concat(dock.MoreKnobs),
        RigEditorFamily.Vocal => dock.VocalMainKnobs.Concat(dock.VocalArticulation).Concat(dock.MoreKnobs),
        RigEditorFamily.Dynamics => dock.DynamicsSections.SelectMany(s => s.Knobs).Concat(dock.MoreKnobs),
        _ => throw new InvalidOperationException($"{dock.Family} is not a specialised family")
    };

    private static IEnumerable<PedalModelSelectorViewModel> ProjectedChoices(RigDockUnit dock) => dock.Family switch
    {
        RigEditorFamily.Expression => dock.ExpressionChoices.Concat(dock.MoreChoices),
        RigEditorFamily.Vocal => One(dock.VocalOctave)
            .Concat(One(dock.VocalPattern))
            .Concat(dock.MoreChoices),
        _ => dock.MoreChoices
    };

    private static IEnumerable<T> One<T>(T? item) where T : class =>
        item is null ? Array.Empty<T>() : [item];

    private static RigDockUnit Dock(string moduleId)
    {
        var type = Catalogue().Single(t => t.Id == moduleId);
        var chain = new FakeDspChainService([type]);
        var node = chain.AddNode(moduleId);
        Assert.NotNull(node);
        return new RigDockUnit(new PedalNodeViewModel(chain, node!, type, FakeLocalization.English()));
    }

    /// <summary>The ten modules, with the ids, ranges and value labels
    /// <c>DspModuleFactory.cpp</c> declares for them.</summary>
    private static DspModuleType[] Catalogue() =>
    [
        new("graphiceq", "Graphic EQ",
        [
            new("band31", "31 Hz", -12, 12, 0), new("band63", "63 Hz", -12, 12, 0),
            new("band125", "125 Hz", -12, 12, 0), new("band250", "250 Hz", -12, 12, 0),
            new("band500", "500 Hz", -12, 12, 0), new("band1k", "1 kHz", -12, 12, 0),
            new("band2k", "2 kHz", -12, 12, 0), new("band4k", "4 kHz", -12, 12, 0),
            new("band8k", "8 kHz", -12, 12, 0), new("band16k", "16 kHz", -12, 12, 0),
            new("level", "Level (dB)", -12, 12, 0)
        ]),
        new("eq", "Parametric EQ",
        [
            new("lowFreq", "Low Freq", 40, 400, 100), new("lowGainDb", "Low Gain (dB)", -18, 18, 0),
            new("midFreq", "Mid Freq", 200, 4000, 1000), new("midQ", "Mid Q", 0.3f, 8, 1),
            new("midGainDb", "Mid Gain (dB)", -18, 18, 0), new("highFreq", "High Freq", 2000, 12000, 6000),
            new("highGainDb", "High Gain (dB)", -18, 18, 0), new("outputTrimDb", "Output (dB)", -24, 24, 0)
        ]),
        new("wah", "Wah / Auto-Wah",
        [
            new("mode", "Mode", 0, 1, 0) { ValueLabels = ["Auto", "Manual"] },
            new("position", "Position", 0, 1, 0.5f), new("sensitivity", "Sensitivity", 0, 1, 0.5f),
            new("rangeLoHz", "Range Lo (Hz)", 200, 800, 400), new("rangeHiHz", "Range Hi (Hz)", 1200, 3000, 2000),
            new("resonance", "Resonance", 0, 1, 0.5f), new("mix", "Mix", 0, 1, 1)
        ]),
        new("whammy", "Whammy (Pitch Treadle)",
        [
            new("position", "Position", 0, 1, 0), new("heelSemis", "Heel (semitones)", -24, 24, 0),
            new("toeSemis", "Toe (semitones)", -24, 24, 12), new("mix", "Mix", 0, 1, 1),
            new("glideMs", "Glide (ms)", 0, 200, 20),
            new("formant", "Formant Preserve", 0, 1, 0) { ValueLabels = ["Off", "On"] },
            new("lowLatency", "Low Latency", 0, 1, 0) { ValueLabels = ["Off", "On"] }
        ]),
        new("swell", "Volume / Swell",
        [
            new("mode", "Mode", 0, 1, 0) { ValueLabels = ["Auto Swell", "Volume"] },
            new("attackMs", "Swell Time (ms)", 5, 2000, 350), new("sensitivity", "Sensitivity", 0, 1, 0.5f),
            new("depth", "Depth", 0, 1, 1), new("position", "Position", 0, 1, 1)
        ]),
        new("miku", "Miku Vocal Synth",
        [
            new("mix", "Mix", 0, 1, 0.6f), new("character", "Character", 0, 1, 0.5f),
            new("octave", "Octave", -1, 2, 1) { ValueLabels = ["-1 Oct", "Unison", "+1 Oct", "+2 Oct"] },
            new("pattern", "Pattern", 0, 7, 0)
            {
                ValueLabels = ["Looh", "Lahh", "Ahh", "Pahh", "Nyan", "Scat", "Random 1", "Random 2"]
            }
        ]) { StringParams = [new("phrases", "Phrases", "")] },
        new("mikustomp", "Miku Stomp (Vocaloid)",
        [
            new("mix", "Mix", 0, 1, 1), new("character", "Character (Dark-Kawaii)", 0, 1, 0.5f),
            new("breath", "Breathiness", 0, 1, 0.35f), new("attackMs", "Attack (ms)", 1, 200, 12),
            new("portamentoMs", "Portamento (ms)", 0, 300, 50), new("vibrato", "Vibrato Depth", 0, 1, 0.45f),
            new("vibratoRateHz", "Vibrato Rate (Hz)", 3, 8, 5.8f),
            new("octave", "Octave", -1, 2, 1) { ValueLabels = ["-1 Oct", "Unison", "+1 Oct", "+2 Oct"] },
            new("pattern", "Lyric Pattern", 0, 10, 0)
            {
                ValueLabels =
                [
                    "Ahh", "Lahh", "Looh", "Pahh", "Nyan", "Scat",
                    "Phrase 1", "Phrase 2", "Phrase 3", "Random 1", "Random 2"
                ]
            }
        ])
        {
            StringParams =
            [
                new("phrases", "Phrase 1 Lyrics", ""), new("phrase2", "Phrase 2 Lyrics", ""),
                new("phrase3", "Phrase 3 Lyrics", "")
            ]
        },
        new("compressor", "Compressor",
        [
            new("thresholdDb", "Threshold (dB)", -60, 0, -18), new("ratio", "Ratio", 1, 20, 4),
            new("attackMs", "Attack (ms)", 0.1f, 100, 10), new("releaseMs", "Release (ms)", 5, 1000, 120),
            new("makeupDb", "Makeup (dB)", 0, 24, 0), new("kneeDb", "Knee (dB)", 0, 24, 6),
            new("detector", "Detector (Peak-RMS)", 0, 1, 1)
        ]),
        new("gate", "Noise Gate",
        [
            new("thresholdDb", "Threshold (dB)", -80, 0, -55), new("ratio", "Ratio", 1, 50, 10),
            new("attackMs", "Attack (ms)", 0.1f, 50, 1), new("holdMs", "Hold (ms)", 0, 500, 40),
            new("releaseMs", "Release (ms)", 5, 1000, 120)
        ]),
        new("noisereducer", "Noise Reducer",
        [
            new("amount", "Amount", 0, 1, 0.5f), new("thresholdDb", "Threshold (dB)", -80, -20, -55),
            new("humFreq", "Hum Freq (Hz)", 50, 60, 50), new("humDepth", "Hum Depth", 0, 1, 0),
            new("releaseMs", "Release (ms)", 20, 500, 120)
        ])
    ];
}
