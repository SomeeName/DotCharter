using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     The inverse of <see cref="RsToneConverter" />: the closest catalogue-valid Rocksmith tone
///     for a DotCharter rig. It keeps no mapping of its own — each rig module is answered by the
///     catalogue gear whose FORWARD conversion lands on the same module and model
///     (<see cref="RigToRsGearIndex" />), and that gear's knobs are searched inside their
///     catalogue ranges until the forward conversion reproduces the module's params
///     (<see cref="RigToRsKnobSearch" />). What has no counterpart (a NAM capture, an external IR,
///     a bypassed or unreached module, parallel routing) is a <see cref="RigToneLoss" />, and a rail
///     over Rocksmith's four slots refuses the tone until the caller leaves gear out.
///     Pure and deterministic; the knob search costs tens of milliseconds for a full rig, so a UI
///     runs it off its thread.
/// </summary>
public static class RigToRsToneConverter
{
    /// <summary>Below this a matched gear also reports <see cref="RigToneLossKind.KnobsApproximate" />.</summary>
    public const float ApproximateBelow = 0.95f;

    /// <summary>Rocksmith's slots per rail (PrePedal1–4, Rack1–4, PostPedal1–4).</summary>
    public const int MaxSlotsPerRail = 4;

    private const int FullFitCandidates = 6;

    /// <summary>Converts <paramref name="rig" /> with the process-wide catalogue and overrides.</summary>
    /// <param name="rig">The rig, modules in signal order.</param>
    /// <param name="key">The tone key to write.</param>
    /// <param name="name">The tone name to write.</param>
    /// <param name="leftOut">First module indices of gear the user left out (<see cref="RigToneSlot.ModuleIndex" />).</param>
    public static RigToRsToneResult Convert(DcRigPreset rig, string key, string name, IReadOnlySet<int>? leftOut = null)
        => Convert(rig, key, name, leftOut, RsGearCatalog.Shared, RsGearOverrides.Shared);

    internal static RigToRsToneResult Convert(
        DcRigPreset rig, string key, string name, IReadOnlySet<int>? leftOut, RsGearCatalog catalog, RsGearOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var walk = new ChainWalk(RigToRsGearIndex.For(catalog, overrides), rig.Modules, leftOut ?? new HashSet<int>());
        walk.Run();
        return walk.Build(key, name);
    }

    /// <summary>The best gear for the run of rig modules starting at <paramref name="start" />, or
    /// null when no gear converts to that module type. Exact model first, then the longest run,
    /// then (before the amp) a pedal over a rack, then the closest params, then guitar gear over the
    /// <c>Bass_</c> twin it cannot be told from, then the key.</summary>
    internal static (RigToRsFit Fit, bool ExactModel)? Match(
        RigToRsGearIndex index, IReadOnlyList<DcRigModule> modules, int start, bool beforeAmp)
    {
        var ranked = new List<(RigToRsFit Fit, bool Exact, int Rack)>();
        foreach (var entry in index.StartingWith(modules[start].ModuleTypeId))
        {
            if (!FitsShape(entry.Output, modules, start)) continue;
            var target = Slice(modules, start, entry.Output.Count);
            var rack = beforeAmp && entry.Gear.Type == "Racks" ? 1 : 0;
            ranked.Add((RigToRsKnobSearch.Fit(index, entry, target, coarse: true), SameModels(entry.Output, target), rack));
        }

        if (ranked.Count == 0) return null;
        var ordered = Order(ranked).ToList();
        var exact = ordered[0].Exact;
        var length = ordered[0].Fit.Entry.Output.Count;
        var finalists = ordered.Where(r => r.Exact == exact && r.Fit.Entry.Output.Count == length).Take(FullFitCandidates)
            .Select(r => (Fit: RigToRsKnobSearch.Fit(index, r.Fit.Entry, Slice(modules, start, length), coarse: false), r.Exact, r.Rack))
            .ToList();
        var best = Order(finalists).First();
        return (best.Fit, best.Exact);
    }

    private static IOrderedEnumerable<(RigToRsFit Fit, bool Exact, int Rack)> Order(IEnumerable<(RigToRsFit Fit, bool Exact, int Rack)> ranked)
        => ranked.OrderByDescending(r => r.Exact)
            .ThenByDescending(r => r.Fit.Entry.Output.Count)
            .ThenBy(r => r.Rack)
            .ThenBy(r => r.Fit.Distance)
            .ThenBy(r => r.Fit.Entry.Gear.Key.StartsWith("Bass_", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(r => r.Fit.Entry.Gear.Key, StringComparer.Ordinal);

    private static bool FitsShape(IReadOnlyList<DcRigModule> output, IReadOnlyList<DcRigModule> modules, int start)
    {
        if (start + output.Count > modules.Count) return false;
        for (var i = 0; i < output.Count; i++)
        {
            var m = modules[start + i];
            if (!string.Equals(m.ModuleTypeId, output[i].ModuleTypeId, StringComparison.Ordinal)) return false;
            if (i > 0 && m.Bypassed) return false;
        }

        return true;
    }

    private static IReadOnlyList<DcRigModule> Slice(IReadOnlyList<DcRigModule> modules, int start, int count)
        => [.. modules.Skip(start).Take(count)];

    private static bool SameModels(IReadOnlyList<DcRigModule> output, IReadOnlyList<DcRigModule> target)
    {
        for (var i = 0; i < output.Count; i++)
        {
            if (ModelParamOf(output[i].ModuleTypeId) is not { } param) continue;
            if (!output[i].Params.TryGetValue(param, out var a)) continue;
            var b = target[i].Params.GetValueOrDefault(param, float.NaN);
            if (MathF.Round(a) != MathF.Round(b)) return false;
        }

        return true;
    }

    /// <summary>The param that names a module's model (roster amp, cabinet IR, mic, drive circuit).</summary>
    internal static string? ModelParamOf(string moduleTypeId) => moduleTypeId switch
    {
        DcRigTargetCatalog.Amp => "voicing",
        DcRigTargetCatalog.Cab => "model",
        DcRigTargetCatalog.MicPos => "mic",
        DcRigTargetCatalog.Overdrive => "model",
        DcRigTargetCatalog.Distortion => "model",
        DcRigTargetCatalog.BassAmp => "model",
        DcRigTargetCatalog.RsAmpChain => "model",
        _ => null
    };

    private sealed record Placed(RigToRsFit Fit, int Start, RsGearRole Role, bool Included, bool StandIn);

    /// <summary>One left-to-right pass over the rig: what each module becomes, and where it goes.</summary>
    private sealed class ChainWalk(RigToRsGearIndex index, IReadOnlyList<DcRigModule> modules, IReadOnlySet<int> leftOut)
    {
        private readonly List<Placed> _placed = [];
        private readonly List<RigToneLoss> _losses = [];
        private readonly List<(int Weight, float Closeness)> _scores = [];
        private bool _hasAmp;
        private bool _hasCab;
        private bool _hasPost;
        private bool _flattened;

        public void Run()
        {
            var i = 0;
            while (i < modules.Count) i += Step(i);
        }

        private int Step(int i)
        {
            var m = modules[i];
            if (i == modules.Count - 1 && m.ModuleTypeId == DcRigTargetCatalog.Gain && !m.Bypassed) return 1;
            if (m.Bypassed) return Lose(RigToneLossKind.Bypassed, i, null, null, scored: false);
            if (m.ModuleTypeId is DcRigTargetCatalog.Splitter or DcRigTargetCatalog.Merger) return Flatten(i);
            if (m.ModuleTypeId == DcRigTargetCatalog.NamAmp) return NamCapture(i, m);

            if (Match(index, modules, i, beforeAmp: !_hasAmp) is not { } match)
                return Lose(RigToneLossKind.NoCounterpart, i, null, null, scored: true);

            var length = match.Fit.Entry.Output.Count;
            Place(match.Fit, i, match.ExactModel);
            return length;
        }

        private int Flatten(int i)
        {
            if (!_flattened) _losses.Add(new RigToneLoss(RigToneLossKind.ParallelFlattened, i, modules[i].ModuleTypeId, null, null));
            _flattened = true;
            return 1;
        }

        private int NamCapture(int i, DcRigModule m)
        {
            var capture = m.StringParams?.GetValueOrDefault("modelPath") is { Length: > 0 } path
                ? Path.GetFileNameWithoutExtension(path)
                : null;
            if (_hasAmp || leftOut.Contains(i))
                return Lose(leftOut.Contains(i) ? RigToneLossKind.LeftOut : RigToneLossKind.NamCaptureDropped, i, null, capture, scored: true);

            var amp = StandInAmp();
            _losses.Add(new RigToneLoss(RigToneLossKind.NamCapture, i, m.ModuleTypeId, amp.Entry.Gear.Key, capture));
            _placed.Add(new Placed(amp, i, RsGearRole.Amp, Included: true, StandIn: true));
            _scores.Add((1, 0f));
            _hasAmp = true;
            return 1;
        }

        private int Lose(RigToneLossKind kind, int i, string? gearKey, string? detail, bool scored)
        {
            _losses.Add(new RigToneLoss(kind, i, modules[i].ModuleTypeId, gearKey, detail));
            if (scored) _scores.Add((1, 0f));
            return 1;
        }

        private void Place(RigToRsFit fit, int start, bool exactModel)
        {
            var gear = fit.Entry.Gear;
            var length = fit.Entry.Output.Count;
            var role = RoleOf(gear.Type);
            if (leftOut.Contains(start))
            {
                _losses.Add(new RigToneLoss(RigToneLossKind.LeftOut, start, modules[start].ModuleTypeId, gear.Key, null));
                _placed.Add(new Placed(fit, start, role, Included: false, StandIn: false));
                _scores.Add((length, 0f));
                return;
            }

            if (!Claim(role, start, gear.Key)) return;

            if (!exactModel) _losses.Add(new RigToneLoss(RigToneLossKind.NearestModel, start, modules[start].ModuleTypeId, gear.Key, null));
            else if (fit.Closeness < ApproximateBelow) _losses.Add(new RigToneLoss(RigToneLossKind.KnobsApproximate, start, modules[start].ModuleTypeId, gear.Key, Percent(fit.Closeness)));
            if (role == RsGearRole.Cabinet && modules[start].StringParams?.GetValueOrDefault("irPath") is { Length: > 0 } ir)
                _losses.Add(new RigToneLoss(RigToneLossKind.ExternalIr, start, modules[start].ModuleTypeId, gear.Key, Path.GetFileName(ir)));

            _placed.Add(new Placed(fit, start, role, Included: true, StandIn: false));
            _scores.Add((length, fit.Closeness));
        }

        private RsGearRole RoleOf(string gearType) => gearType switch
        {
            "Amps" => RsGearRole.Amp,
            "Cabinets" => RsGearRole.Cabinet,
            "Racks" => RsGearRole.Rack,
            _ => _hasAmp ? RsGearRole.PostPedal : RsGearRole.PrePedal
        };

        /// <summary>Takes the one amp / cabinet slot, or reports the second one; notes a rack moved.</summary>
        private bool Claim(RsGearRole role, int start, string gearKey)
        {
            switch (role)
            {
                case RsGearRole.Amp when _hasAmp:
                    Lose(RigToneLossKind.ExtraAmp, start, gearKey, null, scored: true);
                    return false;
                case RsGearRole.Amp:
                    _hasAmp = true;
                    return true;
                case RsGearRole.Cabinet when _hasCab:
                    Lose(RigToneLossKind.ExtraCabinet, start, gearKey, null, scored: true);
                    return false;
                case RsGearRole.Cabinet:
                    _hasCab = true;
                    return true;
                case RsGearRole.Rack when !_hasAmp || _hasPost:
                    _losses.Add(new RigToneLoss(RigToneLossKind.MovedToRacks, start, modules[start].ModuleTypeId, gearKey, null));
                    return true;
                case RsGearRole.PostPedal:
                    _hasPost = true;
                    return true;
                default:
                    return true;
            }
        }

        private RigToRsFit StandInAmp()
        {
            var tone = RsToneAuthoring.CreateDefaultTone("Stand_In", "", index.Catalog);
            var gear = index.Catalog.TryGetGear(tone.Amp.GearKey, out var g) ? g : null;
            var entry = new RigToRsGearEntry(gear ?? new RsGearData(tone.Amp.GearKey, tone.Amp.GearKey, "Amps", "", null), [], []);
            return new RigToRsFit(entry, tone.Amp.KnobValues, 1);
        }

        public RigToRsToneResult Build(string key, string name)
        {
            var defaults = RsToneAuthoring.CreateDefaultTone(key, name, index.Catalog);
            if (!_hasAmp)
            {
                _losses.Add(new RigToneLoss(RigToneLossKind.NoAmp, -1, "", defaults.Amp.GearKey, null));
                _placed.Add(new Placed(StandInAmp(), -1, RsGearRole.Amp, Included: true, StandIn: true));
            }

            if (!_hasCab) _losses.Add(new RigToneLoss(RigToneLossKind.NoCabinet, -1, "", defaults.Cabinet.GearKey, null));

            var slots = Slots();
            var overflows = Overflows(slots);
            var weight = _scores.Sum(s => s.Weight);
            var closeness = weight == 0 ? 0f : _scores.Sum(s => s.Weight * s.Closeness) / weight;
            var tone = overflows.Count > 0 ? null : Tone(defaults, slots);
            return new RigToRsToneResult(tone, slots, _losses, overflows, closeness);
        }

        private List<RigToneSlot> Slots()
        {
            var counters = new Dictionary<RsGearRole, int>();
            var slots = new List<RigToneSlot>(_placed.Count);
            foreach (var p in _placed.OrderBy(p => SignalRank(p.Role)).ThenBy(p => p.Start))
            {
                var rail = p.Role is RsGearRole.PrePedal or RsGearRole.PostPedal or RsGearRole.Rack;
                var slotIndex = rail && p.Included ? counters[p.Role] = counters.GetValueOrDefault(p.Role) + 1 : 0;
                var types = p.Start < 0 ? [] : modules.Skip(p.Start).Take(Math.Max(1, p.Fit.Entry.Output.Count)).Select(m => m.ModuleTypeId).ToList();
                slots.Add(new RigToneSlot(p.Start, types, p.Role, slotIndex, p.Fit.Entry.Gear.Key, p.Fit.Entry.Gear.Name,
                    p.Fit.Closeness, p.Included, p.StandIn));
            }

            return slots;
        }

        private static int SignalRank(RsGearRole role) => role switch
        {
            RsGearRole.PrePedal => 0,
            RsGearRole.Amp => 1,
            RsGearRole.Cabinet => 2,
            RsGearRole.Rack => 3,
            _ => 4
        };

        private static List<RigToneRailOverflow> Overflows(IReadOnlyList<RigToneSlot> slots)
            => [.. slots.Where(s => s.Included && s.SlotIndex > 0).GroupBy(s => s.Role)
                .Where(g => g.Count() > MaxSlotsPerRail)
                .Select(g => new RigToneRailOverflow(g.Key, g.Count()))];

        private RsTone Tone(RsTone defaults, IReadOnlyList<RigToneSlot> slots)
        {
            var included = _placed.Where(p => p.Included).ToList();
            var amp = included.FirstOrDefault(p => p.Role == RsGearRole.Amp) is { } a
                ? new RsAmp(a.Fit.Entry.Gear.Key, a.Fit.Entry.Gear.Category, Knobs(a.Fit))
                : defaults.Amp;
            var cab = included.FirstOrDefault(p => p.Role == RsGearRole.Cabinet) is { } c
                ? new RsCab(c.Fit.Entry.Gear.Key, c.Fit.Entry.Gear.Category, Knobs(c.Fit))
                : defaults.Cabinet;
            return defaults with
            {
                Amp = amp,
                Cabinet = cab,
                PrePedals = Rail(slots, RsGearRole.PrePedal, RsGearType.Pedal),
                Racks = Rail(slots, RsGearRole.Rack, RsGearType.Rack),
                PostPedals = Rail(slots, RsGearRole.PostPedal, RsGearType.Pedal)
            };
        }

        private List<RsPedal> Rail(IReadOnlyList<RigToneSlot> slots, RsGearRole role, RsGearType type)
        {
            var rail = new List<RsPedal>();
            foreach (var slot in slots.Where(s => s.Included && s.Role == role))
            {
                var fit = _placed.First(p => p.Start == slot.ModuleIndex && p.Included).Fit;
                rail.Add(new RsPedal(role, slot.SlotIndex, fit.Entry.Gear.Key, type, fit.Entry.Gear.Category, Knobs(fit)));
            }

            return rail;
        }

        /// <summary>Every knob the gear has: the searched values, the rest at their defaults.</summary>
        private static Dictionary<string, float> Knobs(RigToRsFit fit)
        {
            var knobs = new Dictionary<string, float>(RsToneAuthoring.DefaultKnobValues(fit.Entry.Gear), StringComparer.Ordinal);
            foreach (var (k, v) in fit.Knobs)
            {
                if (knobs.ContainsKey(k)) knobs[k] = v;
            }

            return knobs;
        }

        private static string Percent(float closeness)
            => (closeness * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
