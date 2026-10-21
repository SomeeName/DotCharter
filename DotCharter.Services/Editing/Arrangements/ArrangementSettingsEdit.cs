using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Arrangements;

/// <summary>One technique flag of <see cref="ArrangementProperties" />, reached through an accessor pair.</summary>
/// <remarks>
///     Accessors rather than 24 properties: the flags are a table, and a hand-written
///     property per flag is the shape that let <c>ArrangementProperties</c> pick up a
///     one-sided field in the first place (<c>RscpArrangementPropertyMap</c> exists for
///     the same reason).
/// </remarks>
public sealed record ArrangementFlag(
    string LabelKey,
    Func<ArrangementProperties, int> Get,
    Action<ArrangementProperties, int> Set);

/// <summary>Everything the arrangement dialog writes, as the user left it.</summary>
public sealed record ArrangementSettingsFields(
    string Name,
    ArrangementType Type,
    ArrangementSubtype Subtype,
    int Capo,
    double CentOffset,
    int Strings,
    string TuningPreset,
    bool TuningPresetTouched,
    string OpenedTuningType,
    IReadOnlyList<int> TuningSteps,
    bool HasScrollSpeed,
    double ScrollSpeed,
    bool PickedBass,
    int RouteMask,
    IReadOnlyList<bool> AuthoredFlags,
    bool OverrideDerivedFlags,
    IReadOnlyList<bool> DerivedFlags);

/// <summary>
///     The arrangement dialog's vocabulary and its Save: the five tuning presets and
///     the names <see cref="TuningUtilities" /> resolves, the path routes, the technique
///     flags, and the write onto an arrangement.
/// </summary>
public static class ArrangementSettingsEdit
{
    /// <summary>Rocksmith's own scroll-speed range in the units <c>DynamicVisualDensity</c> stores
    ///     (LOWER IS FASTER); mirrors the clamp in <c>RsManifestGenerator.CreateDynamicVisualDensity</c>.</summary>
    public const double MinScrollSpeed = 0.5;

    /// <inheritdoc cref="MinScrollSpeed" />
    public const double MaxScrollSpeed = 5.0;

    public const double DefaultScrollSpeed = 1.3;

    public const string CustomPreset = "Custom";

    public static IReadOnlyList<string> TuningPresets { get; } =
        ["E Standard", "Drop D", "Eb Standard", "D Standard", "Drop C", CustomPreset];

    /// <summary>(label key, route mask); 0 derives the route from the arrangement type.</summary>
    /// <remarks>The route is authorable apart from the type because the name does not always carry it:
    ///     the base game calls both of Satch Boogie's guitar arrangements "Combo".</remarks>
    public static IReadOnlyList<(string LabelKey, int RouteMask)> Paths { get; } =
    [
        ("Dialog_Path_Auto", 0),
        ("Dialog_Path_Lead", ArrangementPropertiesDeriver.RouteLead),
        ("Dialog_Path_Rhythm", ArrangementPropertiesDeriver.RouteRhythm),
        ("Dialog_Path_Any", ArrangementPropertiesDeriver.RouteLead | ArrangementPropertiesDeriver.RouteRhythm),
        ("Dialog_Path_Bass", ArrangementPropertiesDeriver.RouteBass)
    ];

    /// <summary>The 20 flags <see cref="ArrangementPropertiesDeriver" /> computes from chart content.</summary>
    public static IReadOnlyList<ArrangementFlag> DerivedFlags { get; } =
    [
        new("ArrProp_Bends", p => p.Bends, (p, v) => p.Bends = v),
        new("ArrProp_Slides", p => p.Slides, (p, v) => p.Slides = v),
        new("ArrProp_UnpitchedSlides", p => p.UnpitchedSlides, (p, v) => p.UnpitchedSlides = v),
        new("ArrProp_Hopo", p => p.Hopo, (p, v) => p.Hopo = v),
        new("ArrProp_Tapping", p => p.Tapping, (p, v) => p.Tapping = v),
        new("ArrProp_Vibrato", p => p.Vibrato, (p, v) => p.Vibrato = v),
        new("ArrProp_Tremolo", p => p.Tremolo, (p, v) => p.Tremolo = v),
        new("ArrProp_Sustain", p => p.Sustain, (p, v) => p.Sustain = v),
        new("ArrProp_PalmMutes", p => p.PalmMutes, (p, v) => p.PalmMutes = v),
        new("ArrProp_FretHandMutes", p => p.FretHandMutes, (p, v) => p.FretHandMutes = v),
        new("ArrProp_Harmonics", p => p.Harmonics, (p, v) => p.Harmonics = v),
        new("ArrProp_PinchHarmonics", p => p.PinchHarmonics, (p, v) => p.PinchHarmonics = v),
        new("ArrProp_SlapPop", p => p.SlapPop, (p, v) => p.SlapPop = v),
        new("ArrProp_PowerChords", p => p.PowerChords, (p, v) => p.PowerChords = v),
        new("ArrProp_DropDPower", p => p.DropDPower, (p, v) => p.DropDPower = v),
        new("ArrProp_BarreChords", p => p.BarreChords, (p, v) => p.BarreChords = v),
        new("ArrProp_OpenChords", p => p.OpenChords, (p, v) => p.OpenChords = v),
        new("ArrProp_NonStandardChords", p => p.NonStandardChords, (p, v) => p.NonStandardChords = v),
        new("ArrProp_DoubleStops", p => p.DoubleStops, (p, v) => p.DoubleStops = v),
        new("ArrProp_FifthsAndOctaves", p => p.FifthsAndOctaves, (p, v) => p.FifthsAndOctaves = v)
    ];

    /// <summary>The four flags no note field encodes: always editable, and carried verbatim by the deriver.</summary>
    public static IReadOnlyList<ArrangementFlag> AuthoredFlags { get; } =
    [
        new("ArrProp_FingerPicking", p => p.FingerPicking, (p, v) => p.FingerPicking = v),
        new("ArrProp_TwoFingerPicking", p => p.TwoFingerPicking, (p, v) => p.TwoFingerPicking = v),
        new("ArrProp_PickDirection", p => p.PickDirection, (p, v) => p.PickDirection = v),
        new("ArrProp_Syncopation", p => p.Syncopation, (p, v) => p.Syncopation = v)
    ];

    /// <summary>
    ///     The picker's names in the vocabulary <see cref="TuningUtilities" /> resolves. It
    ///     once emitted <c>DROP_D</c> / <c>DROP_C</c>, which nothing could expand: the tuner
    ///     had no target, the refret planner fell back and <c>RscpWriter</c> wrote CUSTOM
    ///     (<c>UnknownTuningNameTests</c> asserts both are refused).
    /// </summary>
    public static string PresetToTuningType(string preset) => preset switch
    {
        "Drop D" => "E_DROP_D",
        "Eb Standard" => "E_FLAT_STANDARD",
        "D Standard" => "D_STANDARD",
        "Drop C" => "D_DROP_C",
        CustomPreset => "CUSTOM",
        _ => "E_STANDARD"
    };

    /// <summary>
    ///     …and back. An unknown type is <b>Custom</b>, never "E Standard": the domain knows
    ///     twenty-odd tunings this picker cannot name, and folding them onto the first entry
    ///     mislabelled the row and zeroed its offsets. The two legacy spellings are still read.
    /// </summary>
    public static string TuningTypeToPreset(string? type) => type switch
    {
        "E_DROP_D" or "DROP_D" => "Drop D",
        "E_FLAT_STANDARD" => "Eb Standard",
        "D_STANDARD" => "D Standard",
        "D_DROP_C" or "DROP_C" => "Drop C",
        "E_STANDARD" or "" or null => "E Standard",
        _ => CustomPreset
    };

    /// <summary>A preset's canonical six-string offsets; null for Custom (the offsets stay as typed).</summary>
    public static int[]? PresetOffsets(string preset) => preset switch
    {
        CustomPreset or "" => null,
        "Drop D" => [-2, 0, 0, 0, 0, 0],
        "Eb Standard" => [-1, -1, -1, -1, -1, -1],
        "D Standard" => [-2, -2, -2, -2, -2, -2],
        "Drop C" => [-4, -2, -2, -2, -2, -2],
        _ => [0, 0, 0, 0, 0, 0]
    };

    /// <summary>The route mask an arrangement has AUTHORED (0 when it derives it).</summary>
    public static int AuthoredRoute(Arrangement arrangement) =>
        (arrangement.Properties.PathLead != 0 ? ArrangementPropertiesDeriver.RouteLead : 0)
        | (arrangement.Properties.PathRhythm != 0 ? ArrangementPropertiesDeriver.RouteRhythm : 0)
        | (arrangement.Properties.PathBass != 0 ? ArrangementPropertiesDeriver.RouteBass : 0);

    /// <summary>A fresh arrangement for "New arrangement": six strings in E standard, one level.</summary>
    public static Arrangement NewArrangement(int existingCount)
    {
        var arrangement = new Arrangement { Name = $"Arrangement {existingCount + 1}" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        return arrangement;
    }

    /// <summary>Writes <paramref name="fields" /> onto <paramref name="arrangement" /> in place.</summary>
    public static void Apply(Arrangement arrangement, ArrangementSettingsFields fields)
    {
        arrangement.Name = fields.Name;
        arrangement.ArrangementType = fields.Type;
        arrangement.Subtype = fields.Subtype;
        arrangement.Capo = fields.Capo;
        arrangement.CentOffset = fields.CentOffset;
        arrangement.StringCount = fields.Strings;
        // An UNTOUCHED picker keeps the type the arrangement came in with: rewriting it from
        // the five presets on every Save flattened DADGAD, the open tunings and every drop
        // below C to E_STANDARD — a Save meant to change the capo silently retuned the chart.
        arrangement.TuningType = fields.TuningPresetTouched || string.IsNullOrEmpty(fields.OpenedTuningType)
            ? PresetToTuningType(fields.TuningPreset)
            : fields.OpenedTuningType;

        arrangement.TuningPitches.Clear();
        for (var i = 0; i < fields.Strings; i++)
            arrangement.TuningPitches.Add(new TuningOffset(i < fields.TuningSteps.Count ? fields.TuningSteps[i] : 0));

        // A name that does not describe the offsets beside it is CUSTOM: the offsets are
        // the truth, and a shorthand nothing can expand must not be certified (the same
        // verdict as PhraseCatalogFragment.Build and RscpWriter).
        var actual = arrangement.TuningPitches.Select(t => t.Step).ToArray();
        if (!TuningUtilities.TryGetOffsetsForTuningType(arrangement.TuningType, arrangement.StringCount,
                out var canonical) || !canonical.SequenceEqual(actual))
        {
            arrangement.TuningType = "CUSTOM";
        }

        arrangement.Tuning = TuningUtilities.GetTuningNames(arrangement.StringCount, actual);
        ApplyRocksmithProperties(arrangement, fields);
    }

    private static void ApplyRocksmithProperties(Arrangement arrangement, ArrangementSettingsFields fields)
    {
        arrangement.ScrollSpeed = fields.HasScrollSpeed
            ? Math.Clamp(fields.ScrollSpeed, MinScrollSpeed, MaxScrollSpeed)
            : null;

        // The deriver refuses picked bass off a bass arrangement, but the field is what the
        // rscp stores — don't leave a stale true behind a type change.
        arrangement.PickedBass = fields.PickedBass && fields.Type == ArrangementType.Bass;

        var properties = arrangement.Properties;
        properties.PathLead = (fields.RouteMask & ArrangementPropertiesDeriver.RouteLead) != 0 ? 1 : 0;
        properties.PathRhythm = (fields.RouteMask & ArrangementPropertiesDeriver.RouteRhythm) != 0 ? 1 : 0;
        properties.PathBass = (fields.RouteMask & ArrangementPropertiesDeriver.RouteBass) != 0 ? 1 : 0;

        // Always written: nothing derives these, so the checkbox IS the value.
        for (var i = 0; i < AuthoredFlags.Count && i < fields.AuthoredFlags.Count; i++)
            AuthoredFlags[i].Set(properties, fields.AuthoredFlags[i] ? 1 : 0);

        properties.ManualOverride = fields.OverrideDerivedFlags;
        // Only written while pinned: storing the derived answer with the pin off would make
        // the next save look authored and grow the rscp with values the export recomputes.
        if (!fields.OverrideDerivedFlags) return;
        for (var i = 0; i < DerivedFlags.Count && i < fields.DerivedFlags.Count; i++)
            DerivedFlags[i].Set(properties, fields.DerivedFlags[i] ? 1 : 0);
    }
}

/// <summary>"New arrangement" committed from the arrangement dialog: the add and the switch to it are one
///     undo node, and undo restores the previous pick.</summary>
public sealed class AddArrangementAction(IChartEditorSurface editor, IList<Arrangement> arrangements,
    Arrangement arrangement) : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly Arrangement? _previousActive = editor.ActiveArrangement;

    public void Execute()
    {
        arrangements.Add(arrangement);
        editor.SetActiveArrangement(arrangement);
    }

    public void Undo()
    {
        arrangements.Remove(arrangement);
        editor.SetActiveArrangement(_previousActive);
    }
}

/// <summary>
///     One arrangement-settings Save over an existing arrangement, recorded with <c>PushAction</c>
///     (the live object is already written). Snapshots come from
///     <see cref="ChartProjectCloner.CloneArrangement" /> and undo/redo restore IN PLACE through
///     <see cref="ChartProjectRestorer.RestoreArrangement" />, so everything bound to it stays bound.
/// </summary>
public sealed class MutateArrangementSettingsAction(
    Arrangement live,
    Arrangement before,
    Arrangement after,
    Action? onChanged) : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats (a tuning, capo or string-count change unlinks the placements that cannot share, under every policy).</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public void Execute()
    {
        ChartProjectRestorer.RestoreArrangement(live, after);
        onChanged?.Invoke();
    }

    public void Undo()
    {
        ChartProjectRestorer.RestoreArrangement(live, before);
        onChanged?.Invoke();
    }
}
