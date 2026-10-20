using System.Xml.Linq;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
/// The gate for two shipped pedalboard bugs — a param the user could SEE but not
/// USE, and a param whose knob told the truth about nothing.
///
/// <para><b>(1) Unreachable params.</b> A labelled (named-discrete) param renders
/// ONLY through a model SELECTOR — the knob lists filter labelled params out. The
/// retired rack's treadle face never rendered its selectors, so the swell pedal's
/// Auto-Swell/Volume switch was drawn nowhere at all and could not be changed; the
/// amp, graphic-EQ and junction faces had the same hole. These tests read the REAL
/// <c>RigModuleEditorView.axaml</c> and the REAL view models, so re-opening the hole
/// fails here.</para>
///
/// <para><b>Why this survived the rack.</b> Seven per-skin faces are what made the
/// bug possible: each could forget a surface independently, and the treadle did.
/// The Fire module editor has ONE face — <c>RigEditorFaces.For(skin)</c> picks its
/// CHROME, never which params it shows — so the "which face forgot what" axis is
/// gone and what is left to guard is that all of its surfaces are still there. A
/// deleted one takes a whole param class off the screen with no build error and no
/// exception.</para>
///
/// <para><b>(2) Discrete params as continuous knobs.</b> A native param the core
/// lrounds into an int has no in-between: there is no half a square wave, and no
/// 6.5-stage phaser. Every such param must be NAMED (value labels ⇒ a picker) or
/// ORDINAL (a step ⇒ a snapping knob). The native sweep below fails on any int
/// param that is neither.</para>
/// </summary>
public sealed class PedalboardSkinReachabilityTests
{
    /// <summary>The list surfaces the module editor renders, as
    /// <c>ItemsSource</c> binding paths in the shipped XAML.</summary>
    private static readonly string[] DockParamSurfaces =
        ["PrimaryKnobs", "SecondaryKnobs", "ExtraSelectors", "CabinetSlots", "Knobs",
         // The two surfaces a labelled param reaches when RigParamRoles says it
         // is NOT a picker. They are listed here for the same reason the rest
         // are: taking a param out of ModelSelectors and putting it on a list
         // nothing renders would make it invisible AND unreachable, which is
         // the exact bug this class exists for.
         "TransportParams", "MomentaryParams"];

    // ── (1) The view: the editor must render every param surface ─────────────

    /// <summary>THE bug, restated for the surface that shipped. Reads the XAML —
    /// not a mirror of it.</summary>
    [Fact]
    public void TheModuleEditor_RendersEveryParamSurface()
    {
        var dock = DockBody();
        var gaps = new List<string>();

        foreach (var surface in DockParamSurfaces)
        {
            if (!RendersItemsSource(dock, surface))
            {
                gaps.Add($"the module editor does not render {{Binding {surface}}} — every param that " +
                         "reaches the screen through that list is invisible and unreachable.");
            }
        }

        Assert.True(gaps.Count == 0, "Param surfaces the editor drops:\n  " + string.Join("\n  ", gaps));
        var slots = Assert.Single(dock.DescendantsAndSelf(), e => e.Attribute("ItemsSource")?.Value == "{Binding CabinetSlots}");
        Assert.Equal("{Binding IsCabFace}", slots.Attribute("IsVisible")?.Value);
        Assert.Contains(slots.Descendants(), e => e.Attribute("Content")?.Value == "{Binding Polarity}");
        Assert.Contains(dock.DescendantsAndSelf(), e =>
            e.Attribute("Value")?.Value == "{Binding Blend.Value, Mode=TwoWay}" &&
            e.Ancestors().Any(parent => parent.Attribute("IsVisible")?.Value == "{Binding HasBlend}"));
    }

    /// <summary>
    ///     The two STRING param kinds, which are not lists and so are not caught
    ///     above. Both were reachable on the retired rack. The free-text one
    ///     (the vocal module's phrases) briefly was not: the board card's load
    ///     strip offers a file picker, and a param with no file filter has no
    ///     file to pick, so it drew a Choose button that returned immediately.
    /// </summary>
    [Fact]
    public void TheModuleEditor_RendersBothStringParamKinds()
    {
        var bound = DockBody().DescendantsAndSelf()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .ToList();

        Assert.Contains(bound, v => v.Contains("FileParam.PickFileCommand", StringComparison.Ordinal));
        Assert.Contains(bound, v => v.Contains("LyricsParam.LyricsDraft", StringComparison.Ordinal));
        Assert.Contains(bound, v => v.Contains("LyricsParam.SaveLyricsCommand", StringComparison.Ordinal));
    }

    // ── (1) The view models: every param lands on a rendered surface ─────────

    /// <summary>Builds a node on each skin with a continuous param AND a labelled
    /// one, and asserts every param is reachable through a surface the editor
    /// renders. This is the VM-side twin of the XAML gate above: together they
    /// mean "declared in the catalogue ⇒ visible on the board".
    /// <para>
    ///     It goes through <see cref="RigDockUnit"/> rather than the node's raw
    ///     lists, because the editor is what decides which selector is the model
    ///     rail and which are the extras — asserting against the node would pass
    ///     while the screen dropped one.
    /// </para></summary>
    [Theory]
    [InlineData("overdrive", PedalSkin.Stompbox)]
    [InlineData("amp", PedalSkin.AmpHead)]
    [InlineData("namamp", PedalSkin.NeuralAmp)]
    [InlineData("cab", PedalSkin.Cabinet)]
    [InlineData("graphiceq", PedalSkin.GraphicEq)]
    [InlineData("wah", PedalSkin.Wah)]
    [InlineData("swell", PedalSkin.Wah)]
    [InlineData("merger", PedalSkin.Junction)]
    public void EveryParam_IsReachableInTheModuleEditor(string moduleTypeId, PedalSkin expectedSkin)
    {
        var type = new DspModuleType(moduleTypeId, moduleTypeId, new[]
        {
            // "position" keeps the treadle skins on the treadle (the face degrades
            // to a stompbox without it) and is a plain continuous param elsewhere.
            new DspParamInfo("position", "Position", 0f, 1f, 0.5f),
            new DspParamInfo("level", "Level", 0f, 1f, 0.7f),
            new DspParamInfo("resonance", "Resonance", 0f, 1f, 0.5f),
            new DspParamInfo("inputDb", "Input", -24f, 24f, 0f),
            new DspParamInfo("outputDb", "Output", -24f, 24f, 0f),
            new DspParamInfo("voicing", "Model", 0f, 1f, 0f) { ValueLabels = new[] { "A", "B" } },
            new DspParamInfo("mode", "Mode", 0f, 1f, 0f) { ValueLabels = new[] { "One", "Two" } }
        });

        var node = Node(type);
        Assert.Equal(expectedSkin, node.Skin);

        var dock = new RigDockUnit(node);
        if (dock.IsAmpFace)
            Assert.Equal(new[] { "inputDb", "outputDb", "resonance" }, dock.SecondaryKnobs.Select(p => p.Id).OrderBy(id => id));
        else
            Assert.Empty(dock.SecondaryKnobs);
        var reachable = ReachableParamIds(node);
        var unreachable = node.Params.Select(p => p.Id).Where(id => !reachable.Contains(id)).ToList();
        Assert.True(unreachable.Count == 0,
            $"'{moduleTypeId}' ({expectedSkin}) has params on NO rendered surface: {string.Join(", ", unreachable)}");
    }

    /// <summary>The reported bug, pinned: the swell pedal wears the treadle skin
    /// and its <c>mode</c> (Auto Swell / Volume) is a labelled param, so it MUST
    /// come out as a selector — and the editor must be the thing that shows
    /// it.</summary>
    [Fact]
    public void SwellMode_IsASelectorOnTheTreadleSkin()
    {
        var type = new DspModuleType("swell", "Volume / Swell", new[]
        {
            new DspParamInfo("mode", "Mode", 0f, 1f, 0f) { ValueLabels = new[] { "Auto Swell", "Volume" } },
            new DspParamInfo("attackMs", "Swell Time (ms)", 5f, 2000f, 350f),
            new DspParamInfo("position", "Position", 0f, 1f, 1f)
        });

        var node = Node(type);

        Assert.Equal(PedalSkin.Wah, node.Skin);
        Assert.True(node.HasModelSelectors);
        Assert.Equal("mode", Assert.Single(node.ModelSelectors).ParamId);
        Assert.DoesNotContain(node.KnobParams, p => p.Id == "mode"); // it is NOT a knob
        Assert.Contains("mode", ReachableParamIds(node));
    }

    // ── (2) The gear rail must not turn a labelled param into a "model" ──────

    /// <summary>Why the labels were withheld in the first place: the rail treated a
    /// module's FIRST labelled param as its model param, so labelling a footswitch
    /// would have let "browse models" add a pedal already stomped. The model param
    /// is DECLARED now, so a chorus/freeze/swell has none however many labelled
    /// params it carries.</summary>
    [Theory]
    [InlineData("amp", "voicing")]
    [InlineData("cab", "model")]
    [InlineData("overdrive", "model")]
    [InlineData("distortion", "model")]
    [InlineData("chorus", null)]
    [InlineData("freeze", null)]
    [InlineData("swell", null)]
    [InlineData("tremolo", null)]
    public void ModelParamOf_ResolvesTheDeclaredModelParam_NotTheFirstLabelledOne(string moduleId, string? expected)
    {
        // Every module here carries a labelled param FIRST that is not a model.
        var type = new DspModuleType(moduleId, moduleId, new[]
        {
            new DspParamInfo("capture", "Capture", 0f, 1f, 0f) { ValueLabels = new[] { "Released", "Held" } },
            new DspParamInfo("mode", "Mode", 0f, 1f, 0f) { ValueLabels = new[] { "Auto", "Manual" } },
            new DspParamInfo("model", "Model", 0f, 1f, 0f) { ValueLabels = new[] { "Generic", "Screamer" } },
            new DspParamInfo("voicing", "Voicing", 0f, 1f, 0f) { ValueLabels = new[] { "Clean", "Crunch" } }
        });

        Assert.Equal(expected, GearCardViewModel.ModelParamOf(type)?.Id);
    }

    // ── (2) The native catalogue: no int param may render as a plain knob ────

    /// <summary>Param ids whose native core field is an INT (the adapter lrounds
    /// them) or a bool. Every one of them must be NAMED (value labels ⇒ a picker)
    /// or ORDINAL (a step ⇒ a snapping knob) — a plain knob would let the user
    /// park it between two values the DSP cannot run. A future module that adds a
    /// "mode"/"waveform"/"trigger" param without either fails here.</summary>
    private static readonly HashSet<string> DiscreteParamIds = new(StringComparer.Ordinal)
    {
        "mode", "waveform", "shape", "trigger", "capture", "direction", "model", "voicing",
        "pattern", "octave", "key", "chord", "harmonic", "filterType", "lfoShape", "reverse",
        "pingPong", "formant", "lowLatency", "detector", "bits", "downsample", "stages",
        "voices", "steps", "repeats", "enabledBranches"
    };

    /// <summary>The FOUR params whose id LOOKS discrete but which the core reads
    /// as a continuous BLEND — each verified in its header, each correctly a knob:
    /// <list type="bullet">
    /// <item><c>compressor.detector</c> — <c>level = (1-det)*peak + det*rms</c>.</item>
    /// <item><c>univibe.mode</c> — smoothed morph, <c>wetAmt = mix + mode*(1-mix)</c>.</item>
    /// <item><c>envfilter.direction</c> — <c>cutoff = cutUp + dir*(cutDown-cutUp)</c>
    ///       (the autowah's same-named param IS an int switch — they differ).</item>
    /// <item><c>octfuzz.octave</c> (JuceSharp 0.9 gear wave) —
    ///       <c>dcdsp_octfuzz.h</c>: "the <c>octave</c> knob CROSSFADES between the
    ///       plain and the rectified leg", and it is a <c>SmoothedValue</c> walked
    ///       per sample (<c>octBuf_[b] = octave_.next()</c>). The id is shared with
    ///       <c>miku.octave</c> / <c>mikustomp.octave</c> (whole octaves, rounded,
    ///       four labels) and <c>resonator.octave</c> (a register, five labels),
    ///       which ARE discrete and keep their labels — the same param name
    ///       meaning two different things on different pedals is exactly why this
    ///       list is keyed by (module, param) and not by param id.</item>
    /// </list>
    /// Labelling one of these would be a LIE about the DSP, so they are asserted to
    /// stay continuous, not merely allowed to be.</summary>
    private static readonly HashSet<(string Module, string Param)> ContinuousByDesign =
    [
        ("compressor", "detector"),
        ("univibe", "mode"),
        ("envfilter", "direction"),
        ("octfuzz", "octave")
    ];

    [Fact]
    public void EveryNativeDiscreteParam_IsEitherNamedOrOrdinal()
    {
        var native = TryLoadNativeCatalog();
        Assert.SkipWhen(native.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var offenders = new List<string>();
        foreach (var module in native)
        {
            foreach (var p in module.Params)
            {
                if (!DiscreteParamIds.Contains(p.Id)) continue;

                if (ContinuousByDesign.Contains((module.Id, p.Id)))
                {
                    Assert.False(p.HasValueLabels,
                        $"'{module.Id}.{p.Id}' is a continuous BLEND in its core header — labelling it claims the DSP " +
                        "has discrete states it does not have. Re-read the header before changing this.");
                    Assert.False(p.IsStepped, $"'{module.Id}.{p.Id}' is continuous by design — it must not snap.");
                    continue;
                }

                if (!p.HasValueLabels && !p.IsStepped)
                {
                    offenders.Add($"'{module.Id}.{p.Id}' [{p.Min}..{p.Max}] renders as a CONTINUOUS knob, but the core " +
                              "rounds it to an int. Give it value labels (a named choice) or a step (an ordinal count).");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Discrete native params rendering as continuous knobs ({offenders.Count}):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>A labelled param must name EVERY value in its range (the UI maps
    /// index → <c>Min + i</c>, so a short list makes the top of the range
    /// unselectable) and must not also carry a step.</summary>
    [Fact]
    public void EveryNativeLabelledParam_CoversItsWholeRange()
    {
        var native = TryLoadNativeCatalog();
        Assert.SkipWhen(native.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var offenders = new List<string>();
        foreach (var module in native)
        {
            foreach (var p in module.Params.Where(p => p.HasValueLabels))
            {
                var expected = (int)Math.Round(p.Max - p.Min) + 1;
                if (p.ValueLabels!.Count != expected)
                    offenders.Add($"'{module.Id}.{p.Id}' [{p.Min}..{p.Max}] has {p.ValueLabels.Count} labels, needs {expected}.");
                if (p.ValueLabels.Any(string.IsNullOrWhiteSpace))
                    offenders.Add($"'{module.Id}.{p.Id}' has a blank label.");
                if (p.IsStepped)
                    offenders.Add($"'{module.Id}.{p.Id}' carries BOTH labels and a step — it is a picker, not a knob.");
            }
        }

        Assert.True(offenders.Count == 0, "Broken value-label sets:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>A stepped (ordinal) param's range and default must sit ON the grid,
    /// or the knob's snap would fight the core's rounding.</summary>
    [Fact]
    public void EveryNativeSteppedParam_HasAConsistentGrid()
    {
        var native = TryLoadNativeCatalog();
        Assert.SkipWhen(native.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var stepped = native.SelectMany(m => m.Params.Select(p => (Module: m.Id, Param: p)))
            .Where(x => x.Param.IsStepped)
            .ToList();

        // The 0.18 ordinal counts. If this ever reads 0 the step metadata silently
        // stopped arriving from the ABI and every count is a continuous knob again.
        Assert.NotEmpty(stepped);

        foreach (var (moduleId, p) in stepped)
        {
            var span = (p.Max - p.Min) / p.Step;
            Assert.True(Math.Abs(span - Math.Round(span)) < 1e-4,
                $"'{moduleId}.{p.Id}': range [{p.Min}..{p.Max}] is not a whole number of {p.Step}-sized steps.");

            var snapped = KnobMath.Snap(p.Default, p.Min, p.Max, p.Step);
            Assert.True(Math.Abs(snapped - p.Default) < 1e-4,
                $"'{moduleId}.{p.Id}': default {p.Default} is not on the step grid (snaps to {snapped}).");
        }
    }

    /// <summary>The phaser's stage count is EVEN — the core does <c>s &amp;= ~1</c>.
    /// A step of 1 would let the face show 7 stages while the cascade ran 6.</summary>
    [Fact]
    public void PhaserStages_StepIsTwo_BecauseTheCoreForcesItEven()
    {
        var native = TryLoadNativeCatalog();
        Assert.SkipWhen(native.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var stages = native.FirstOrDefault(m => m.Id == "phaser")?.Params.FirstOrDefault(p => p.Id == "stages");
        Assert.SkipWhen(stages is null, "native catalogue has no phaser 'stages' param");

        Assert.Equal(2f, stages!.Step);
        Assert.Equal(6.0, KnobMath.Snap(6.5, stages.Min, stages.Max, stages.Step)); // never 6.5, never 7
        Assert.Equal(8.0, KnobMath.Snap(7.5, stages.Min, stages.Max, stages.Step));
    }

    /// <summary>Every module that carries a labelled param must have those labels
    /// land on a surface the editor renders — the native-driven closing of the
    /// loop (the XAML gate proves the surfaces exist, this proves the catalogue
    /// only uses them).</summary>
    [Fact]
    public void EveryNativeModuleWithLabelledParams_HasThemRendered()
    {
        var native = TryLoadNativeCatalog();
        Assert.SkipWhen(native.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var gaps = new List<string>();
        foreach (var module in native.Where(m => m.Params.Any(p => p.HasValueLabels)))
        {
            var reachable = ReachableParamIds(Node(module));
            var missing = module.Params.Where(p => p.HasValueLabels && !reachable.Contains(p.Id))
                .Select(p => p.Id)
                .ToList();
            if (missing.Count > 0)
            {
                gaps.Add($"'{module.Id}' ({DspModuleVisuals.SkinFor(module.Id)}) labelled params the editor never " +
                         $"shows: {string.Join(", ", missing)}");
            }
        }

        Assert.True(gaps.Count == 0,
            "Modules whose labelled params never reach the screen:\n  " + string.Join("\n  ", gaps));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Every param id the module editor puts on screen for
    /// <paramref name="node"/> — exactly the surfaces
    /// <see cref="DockParamSurfaces"/> names.</summary>
    private static HashSet<string> ReachableParamIds(PedalNodeViewModel node)
    {
        var dock = new RigDockUnit(node);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var projected = dock.PrimaryKnobs
            .Concat(dock.ShowSecondaryKnobs ? dock.SecondaryKnobs : [])
            .Concat(dock.IsCabFace ? dock.CabinetSlots.SelectMany(slot => slot.Knobs) : [])
            .Concat(dock.HasBlend && dock.Blend is { } blend ? [blend] : [])
            .ToArray();
        Assert.Equal(projected.Length, projected.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(node.KnobParams.Select(p => p.Id).OrderBy(id => id), projected.Select(p => p.Id).OrderBy(id => id));
        foreach (var p in projected)
        {
            Assert.Contains(node.Params, actual => ReferenceEquals(actual, p));
            Assert.True(reachable.Add(p.Id), $"Duplicate rendered parameter {p.Id}");
        }
        // The transport / momentary lists, which the shared rack renders.
        foreach (var transport in dock.TransportParams)
        {
            Assert.Contains(node.Params, actual => actual.Id == transport.Id);
            Assert.DoesNotContain(node.ModelSelectors, s => s.ParamId == transport.Id);
            Assert.True(reachable.Add(transport.Id), $"Duplicate rendered transport {transport.Id}");
        }
        foreach (var momentary in dock.MomentaryParams)
        {
            Assert.Contains(node.Params, actual => actual.Id == momentary.Id);
            Assert.DoesNotContain(node.ModelSelectors, s => s.ParamId == momentary.Id);
            Assert.True(reachable.Add(momentary.Id), $"Duplicate rendered momentary {momentary.Id}");
        }

        var selectors = dock.ExtraSelectors
            .Concat(dock.ModelRail is { } rail ? [rail] : [])
            .Concat(dock.IsCabFace ? dock.CabinetSlots.Select(slot => slot.Polarity).OfType<PedalModelSelectorViewModel>() : []);
        foreach (var selector in selectors)
        {
            Assert.True(ReferenceEquals(node.AmpModelSelector, selector) ||
                node.ModelSelectors.Any(actual => ReferenceEquals(actual, selector)));
            Assert.True(reachable.Add(selector.ParamId), $"Duplicate rendered selector {selector.ParamId}");
        }
        return reachable;
    }

    private static PedalNodeViewModel Node(DspModuleType type)
    {
        var dsp = new FakeDspChainService(new[] { type });
        var node = new DspNode(1, type.Id, false, new Dictionary<string, float>());
        return new PedalNodeViewModel(dsp, node, type, FakeLocalization.English());
    }

    /// <summary>The module editor's body — the one region every param surface
    /// lives inside.</summary>
    private static XElement DockBody()
    {
        // The editor is a host over one file per family (RigEditors/); fold
        // every root under one synthetic element so the scans below see them all.
        var body = new XElement("RigEditors");
        foreach (var (path, doc) in RigEditorViews.Documents(TestPaths.RepoRoot()))
        {
            Assert.NotNull(doc.Root);
            Assert.Equal("vm:RigDockUnit", doc.Root!.Attributes().Single(a => a.Name.LocalName == "DataType").Value);
            body.Add(new XElement(doc.Root));
        }
        Assert.Contains(body.Descendants(), e => e.Attribute("Content")?.Value == "{Binding ModelRail}");
        Assert.Contains(body.Descendants(), e => e.Name.LocalName == "ComboBox" && e.Attribute("ItemsSource")?.Value == "{Binding Options}");
        return body;
    }

    private static bool RendersItemsSource(XElement scope, string property)
        => scope.DescendantsAndSelf()
            .Any(e => (string?)e.Attribute("ItemsSource") == $"{{Binding {property}}}");

    /// <summary>
    ///     "The DLL is not on this machine" is a legitimate skip. Anything else
    ///     is a REGRESSION and must propagate.
    ///     <para>
    ///         This was a blanket <c>catch { return []; }</c> until 2026-08-29,
    ///         and a blanket catch cannot tell a missing DLL from
    ///         <c>LoadCatalog</c> throwing because the ABI moved, an export
    ///         vanished or the catalogue came back malformed — the failure the
    ///         root guide warns about most (<i>"An ABI bump means rebuilding it
    ///         before any managed code runs, or you get a version mismatch at
    ///         engine open"</i>). It reported as five green
    ///         <c>Assert.SkipWhen</c>s and took the whole discrete-param contract
    ///         down with it.
    ///     </para>
    ///     Only the two "there is no usable native library here" shapes are
    ///     caught: <see cref="DllNotFoundException" /> (no file) and
    ///     <see cref="BadImageFormatException" /> (wrong architecture). An
    ///     <c>EntryPointNotFoundException</c> is deliberately NOT caught — that
    ///     is exactly the stale-ABI case, and it should fail loudly.
    /// </summary>
    private static IReadOnlyList<DspModuleType> TryLoadNativeCatalog()
    {
        try
        {
            var svc = new JuceDspChainService();
            svc.LoadCatalog();
            return svc.Catalog;
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }
}
