using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     §7 of the owner's spec — the CORRECTED rig parameter sets.
///
///     <para>
///         <c>Amp and FX Rig.dc.html</c> ships a seventeen-entry <c>UNITS</c>
///         table, and each entry hand-writes the knobs its card draws. Several of
///         those knobs name a parameter the DSP does not have: the capture player
///         has no Sag, the IR loader has no Room / Distance / Air / Level, the
///         detuner has no Width or Tone. <b>A knob for a parameter the DSP does
///         not have is a lie on screen — it turns and nothing happens.</b>
///     </para>
///
///     <para>
///         The fix is structural, not a correction list: the rig screen renders
///         <see cref="PedalNodeViewModel.Params" />, which is materialized from
///         the module catalogue the native engine publishes. So the screen CANNOT
///         draw a phantom knob unless someone writes a per-module table into it.
///         These tests are what makes that permanent — they walk the mockup's own
///         table and assert, unit by unit, that every phantom is absent from the
///         real parameter set AND from the view models the board builds, and that
///         every REAL parameter is present.
///     </para>
///
///     <para>
///         The DSP truth here is <see cref="DcRigTargetCatalog" />, the managed
///         mirror of the native <c>DspModuleFactory</c> catalogue —
///         <c>DcRigTargetCatalogTests.ManagedCatalog_MatchesNativeCatalogue_WhenTheNativeAbiIsLoadable</c>
///         is what ties it back to the DLL, so this suite needs no engine and
///         still cannot drift from one.
///     </para>
/// </summary>
public sealed class AmpFxRigParameterSetTests
{
    /// <summary>
    ///     One row of <c>Amp and FX Rig.dc.html</c>'s <c>UNITS</c> table, as the
    ///     mockup wrote it, against the module it actually maps to.
    /// </summary>
    /// <param name="MockupUnit">The mockup's own key (<c>nam</c>, <c>cab</c>, …).</param>
    /// <param name="ModuleTypeId">The DSP module the rig really instantiates.</param>
    /// <param name="Phantom">
    ///     Knobs the mockup drew that the DSP has NO parameter for. Every one of
    ///     these must stay absent — adding one back is a regression, removing one
    ///     is the fix.
    /// </param>
    /// <param name="Missing">
    ///     REAL parameters the mockup's card left out. The screen shows these,
    ///     because a rig that hides a control the DSP has is the same defect
    ///     pointing the other way.
    /// </param>
    public sealed record MockupUnitRow(
        string MockupUnit,
        string ModuleTypeId,
        string[] Phantom,
        string[] Missing);

    /// <summary>
    ///     The whole mockup table. Transcribed from the <c>UNITS</c> literal in
    ///     <c>Amp and FX Rig.dc.html</c> and reconciled against
    ///     <c>JuceSharp/src/JuceSharp.Native/src/dsp/DspModuleFactory.cpp</c> +
    ///     <c>NamAmpModule.cpp</c>.
    ///     <para>
    ///         Two mockup units resolve to ONE module and are therefore not
    ///         separate rows here: <c>cab412</c> ("4×12 Stack") is <c>cab</c> at
    ///         <c>model</c> = 7, exactly what the gear rail's "browse by model"
    ///         path already adds. The two routing entries (<c>split</c>,
    ///         <c>merge</c>) draw no knobs in the mockup and have none.
    ///     </para>
    /// </summary>
    public static readonly MockupUnitRow[] MockupUnits =
    [
        // The mockup's Mix knob is real; nothing else on the compressor card is
        // invented. `detector` is the one the card left out — a continuous
        // peak↔RMS blend (dcdsp_compressor.h), not a switch.
        new("comp", "compressor", [], ["detector"]),
        // The gate's card drew a "Depth" that the expander has no parameter for;
        // what it really has is `ratio`.
        new("gate", "gate", ["depth"], ["ratio"]),
        // §7's headline. `namamp` is gain/bass/mid/treble/presence/resonance/
        // master + the two trims — the amp FACE (cores/dcdsp_ampface.h). There is
        // no sag model, and there is no wet/dry mix: a capture REPLACES the amp,
        // it is not an effect you blend in.
        new("nam", "namamp", ["sag"], ["inputDb", "outputDb"]),
        // The legacy amp's card omits the 24-model voicing picker, which is the
        // single control that most changes what it sounds like.
        new("legacy", "amp", [], ["voicing"]),
        // The cab is an IR loader plus two Butterworth cuts and a wet/dry mix
        // (dcdsp_cab.h: `y = dry*(1-mix) + w*mix`). Room, Distance, Air and Level
        // are studio-mic-placement controls this module does not model at all.
        new("cab", "cab", ["room", "distance", "air", "level"], ["model", "mix"]),
        // The EQ card is honest bar its output trim.
        new("eq", "eq", [], ["outputTrimDb"]),
        // Drive/distortion both carry a 4-model picker the cards omit; the
        // distortion also has a `tightHz` low-cut corner.
        new("od", "overdrive", [], ["model"]),
        new("dist", "distortion", [], ["model", "tightHz"]),
        new("fuzz", "bigmuff", [], []),
        // The detuner: two constant-ratio granular voices. `dcdsp_doubler.h` says
        // it in as many words — "the voices are CONSTANT-RATIO granular detuners,
        // NOT LFO vibrato" — so there is no LFO, no width and no tone control.
        new("mod", "doubler", ["width", "tone"], []),
        // The chorus gained four params in 0.18, every one of them a no-op at its
        // default; the card shows three of eight.
        new("chorus", "chorus",
            [], ["voices", "feedback", "spread", "lfoShape", "wetHpfHz"]),
        // The reverb card invented a pre-delay and a low cut. This reverb is a
        // Schroeder/Freeverb topology: room size, damping, width, mix.
        new("rev", "reverb", ["pre-delay", "low cut"], []),
        // The tape delay's card invented a low cut and renamed drive to
        // "saturation"; the real set carries `toneHz`, which it omits.
        new("delay", "tapedelay", ["low cut"], ["toneHz"]),
        // "Clean Boost" in the mockup is a three-knob EQ-ish lift. The module the
        // rig calls Clean Boost (`gain`) is ONE knob; the tilt/character pair
        // belongs to `boost` (Boost Clean/Treble), a different module.
        new("boost", "gain", ["bass", "treble"], [])
    ];

    public static TheoryData<string> UnitIds()
    {
        var data = new TheoryData<string>();
        foreach (var row in MockupUnits) data.Add(row.MockupUnit);
        return data;
    }

    private static MockupUnitRow Row(string mockupUnit)
        => MockupUnits.Single(r => r.MockupUnit == mockupUnit);

    private static IReadOnlyList<DcRigTargetCatalog.ParamSpec> RealParams(string moduleTypeId)
    {
        var hit = DcRigTargetCatalog.AllModules
            .Where(m => m.ModuleTypeId == moduleTypeId)
            .Select(m => m.Params)
            .SingleOrDefault();
        Assert.True(hit is { Count: > 0 },
            $"'{moduleTypeId}' is not in the DSP catalogue — the mockup maps a unit onto a module that does not exist.");
        return hit!;
    }

    // ══ Anti-vacuity ═════════════════════════════════════════════════════════

    /// <summary>
    ///     The floor. A table that emptied itself, or a catalogue that stopped
    ///     answering, would make every assertion below pass while walking
    ///     nothing — which is exactly how the guards this repo has had to fix
    ///     failed.
    /// </summary>
    [Fact]
    public void TheMockupTable_AndTheDspCatalogue_AreBothPopulated()
    {
        Assert.True(MockupUnits.Length >= 14,
            $"the mockup draws 17 units and 14 distinct modules; the table has {MockupUnits.Length}");
        // MEASURED, not carried forward: 100 `public const string` ids in
        // DcRigTargetCatalog on 2026-09-12, after the JuceSharp 0.9 gear wave
        // took the native catalogue 57 → 100. The floor stood at `>= 50` with a
        // message that said 57 — so a mirror stalled at 57 while the engine
        // published 100 stayed green, which is exactly the drift this floor
        // exists to catch. Re-measure and raise it after the next wave.
        Assert.True(DcRigTargetCatalog.ModuleTypeIds.Count >= 100,
            $"the DSP catalogue should carry 100 modules (57 before the JuceSharp 0.9 gear " +
            $"wave); it reports {DcRigTargetCatalog.ModuleTypeIds.Count}");

        // …and the corrections themselves must not evaporate: §7 exists because
        // there ARE phantoms. If this reaches zero the suite still passes every
        // other test while proving nothing about the thing it was written for.
        Assert.True(MockupUnits.Sum(r => r.Phantom.Length) >= 8,
            "the §7 correction list is empty — the phantom knobs it was written for are gone from the table");
        Assert.True(MockupUnits.Sum(r => r.Missing.Length) >= 10,
            "the §7 restoration list is empty");
    }

    // ══ The palette rebuild is ONE pass over the catalogue ════════════════════

    /// <summary>
    ///     <c>AmpFxRigViewModel.RebuildGearGroups</c> enumerates the rail's
    ///     cards <b>once</b>.
    ///
    ///     <para>
    ///         It used to loop <c>FireRigCategory.DisplayOrder</c> and re-scan
    ///         every card inside each arm, so the work was O(identities ×
    ///         cards): eight passes and eight <c>ForModule</c> lookups per card,
    ///         i.e. 800 over a 100-module catalogue — and a rebuild runs on
    ///         every tab click, every favourite toggle and every coalesced
    ///         search keystroke, on the UI thread, inside the ≤150 ms switch
    ///         budget. There is no seam to count enumerations through
    ///         (<c>GearRail.Cards</c> is a plain <c>ObservableCollection</c> the
    ///         rail owns), so the guard is structural: the ONE read of
    ///         <c>GearRail.Cards</c> must sit outside the DisplayOrder loop.
    ///     </para>
    /// </summary>
    [Fact]
    public void RebuildGearGroups_ReadsTheCatalogueOnce_NotOncePerCategory()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Tone.UI", "ViewModels", "PlayMode", "AmpFxRigViewModel.cs"));
        var body = MethodBody(source, "private void RebuildGearGroups()");

        // Anti-vacuity: the body must really be the rebuild.
        Assert.Contains("GearGroups.Clear();", body);
        Assert.Contains("FireRigCategory.DisplayOrder", body);

        var reads = Occurrences(body, "GearRail.Cards");
        Assert.True(reads == 1,
            $"RebuildGearGroups reads GearRail.Cards {reads} times; one pass over the catalogue means once");

        // …and that read comes BEFORE the DisplayOrder loop, which is what makes
        // it a pass over the cards rather than a pass per identity.
        Assert.True(body.IndexOf("GearRail.Cards", StringComparison.Ordinal)
                    < body.IndexOf("FireRigCategory.DisplayOrder", StringComparison.Ordinal),
            "the catalogue is scanned inside the DisplayOrder loop — that is O(identities × cards)");

        // The per-card identity lookup happens once per card for the same
        // reason: one ForModule call site, and it is in the bucketing pass.
        var lookups = Occurrences(body, "FireRigCategory.ForModule(");
        Assert.True(lookups == 1,
            $"RebuildGearGroups calls FireRigCategory.ForModule from {lookups} sites; one pass means one");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    /// <summary>The text between a method's signature and its matching closing
    /// brace — brace-counted, so a nested block cannot end it early.</summary>
    private static string MethodBody(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is not in AmpFxRigViewModel.cs — this lint is scanning nothing");

        var open = source.IndexOf('{', at + signature.Length);
        Assert.True(open >= 0, $"'{signature}' has no body");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        Assert.Fail($"'{signature}' has an unbalanced body");
        return string.Empty;
    }

    // ══ The correction itself ════════════════════════════════════════════════

    /// <summary>
    ///     THE §7 test. Every knob the mockup drew that the DSP has no parameter
    ///     for must be absent from the real parameter set — id-wise and
    ///     label-wise, because a phantom smuggled back in under a display name
    ///     would be just as dead.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnitIds))]
    public void NoPhantomKnob_HasAParameterBehindIt(string mockupUnit)
    {
        var row = Row(mockupUnit);
        var real = RealParams(row.ModuleTypeId);
        Assert.NotEmpty(real);

        foreach (var phantom in row.Phantom)
        {
            Assert.DoesNotContain(real,
                p => string.Equals(p.Id, phantom, StringComparison.OrdinalIgnoreCase));
            // The mockup's labels are words ("Low cut", "Pre-delay"); a real
            // param whose id differs but whose LABEL matches would mean the
            // mockup was right and this row is wrong. Compare on the squashed
            // form so "Low cut" and "lowCutHz" cannot slip past each other.
            var squashed = Squash(phantom);
            Assert.DoesNotContain(real, p => Squash(p.Id).StartsWith(squashed, StringComparison.Ordinal));
        }
    }

    /// <summary>
    ///     …and the other direction: every parameter the mockup's card left out
    ///     is REAL, so the screen showing it is a restoration and not an
    ///     invention of its own.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnitIds))]
    public void EveryRestoredParameter_IsOneTheDspReallyHas(string mockupUnit)
    {
        var row = Row(mockupUnit);
        var real = RealParams(row.ModuleTypeId).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var missing in row.Missing)
            Assert.Contains(missing, real);
    }

    // ══ …and that the SCREEN renders exactly that set ════════════════════════

    /// <summary>
    ///     The structural half. The board builds a
    ///     <see cref="PedalNodeViewModel" /> per chain node and renders its
    ///     <see cref="PedalNodeViewModel.Params" /> — so this drives the real
    ///     view-model construction over a fake chain whose catalogue is the DSP's
    ///     own parameter list, and asserts the result is that list EXACTLY: same
    ///     ids, same order, nothing added, nothing dropped.
    ///     <para>
    ///         This is what would fail if someone gave the rig screen a
    ///         per-module knob table — which is the only way a phantom can come
    ///         back.
    ///     </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(UnitIds))]
    public void TheBoardsParameterList_IsTheDspsParameterList(string mockupUnit)
    {
        var row = Row(mockupUnit);
        var real = RealParams(row.ModuleTypeId);

        var type = new DspModuleType(
            row.ModuleTypeId,
            row.ModuleTypeId,
            real.Select(p => new DspParamInfo(p.Id, p.Id, p.Min, p.Max, p.Default)).ToList());

        var chain = new CatalogOnlyDspChainService(type);
        var node = chain.AddNode(row.ModuleTypeId);
        Assert.NotNull(node);

        var vm = new PedalNodeViewModel(chain, node!, type, FakeLocalization.English());

        Assert.Equal(real.Select(p => p.Id), vm.Params.Select(p => p.Id));

        // Every param reaches the user through exactly ONE of the two surfaces
        // the card offers: a knob, or a named picker. A param in neither is a
        // control the DSP has and the screen does not.
        foreach (var param in vm.Params)
        {
            var onACard = vm.KnobParams.Contains(param)
                          || vm.ModelSelectors.Any(s => s.ParamId == param.Id);
            Assert.True(onACard, $"'{row.ModuleTypeId}.{param.Id}' reaches no control on the card");
        }
    }

    /// <summary>
    ///     Squashes a param id or a mockup label to a comparable key: lower-case
    ///     letters only. "Low cut" → "lowcut", "lowCutHz" → "lowcuthz".
    /// </summary>
    private static string Squash(string s)
        => new(s.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    ///     The smallest chain service that can build a pedal card: a catalogue of
    ///     one module type and an in-memory node list. It is deliberately NOT a
    ///     mock of the engine — the point is that the card's parameter list comes
    ///     from the CATALOGUE, so the catalogue is the only thing it needs.
    /// </summary>
    private sealed class CatalogOnlyDspChainService : IDspChainService
    {
        private readonly List<DspNode> _nodes = [];
        private readonly Dictionary<(int, string), float> _values = [];
        private int _next;

        public CatalogOnlyDspChainService(DspModuleType type) => Catalog = [type];

        public void AttachEngine(nint engineHandle) { }
        // Catalogue-only stand-in: it never talks to an engine, but the cards it
        // builds have to behave as though one is there.
        public bool HasEngine => true;
        public int Player => 0;
        public IReadOnlyList<DspModuleType> Catalog { get; }
        public IReadOnlyList<DspNode> GetNodes() => _nodes;
        public event Action? NodesChanged;
        public event Action? EngineAttached;

        public DspNode? AddNode(string moduleTypeId, bool initiallyBypassed = false)
        {
            var type = Catalog.FirstOrDefault(t => t.Id == moduleTypeId);
            if (type is null) return null;
            var defaults = type.Params.ToDictionary(p => p.Id, p => p.Default, StringComparer.Ordinal);
            var node = new DspNode(_next++, moduleTypeId, initiallyBypassed, defaults);
            _nodes.Add(node);
            foreach (var p in type.Params) _values[(node.NodeId, p.Id)] = p.Default;
            NodesChanged?.Invoke();
            EngineAttached?.Invoke();
            return node;
        }

        public bool RemoveNode(int nodeId) => _nodes.RemoveAll(n => n.NodeId == nodeId) > 0;
        public bool MoveNode(int nodeId, int newIndex) => true;
        public bool SetBypass(int nodeId, bool bypassed) => true;

        public bool SetParam(int nodeId, string paramId, float value)
        {
            _values[(nodeId, paramId)] = value;
            return true;
        }

        public float? GetParam(int nodeId, string paramId)
            => _values.TryGetValue((nodeId, paramId), out var v) ? v : null;

        public bool SetNodeString(int nodeId, string paramId, string value) => true;
        public string? GetNodeString(int nodeId, string paramId) => string.Empty;
        public bool SetNodeBranch(int nodeId, int branch) => true;
        public int GetNodeBranch(int nodeId) => 0;
    }
}
