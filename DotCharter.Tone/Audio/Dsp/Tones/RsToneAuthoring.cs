using System.Text.RegularExpressions;

namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>One entry of the fixed Rocksmith tone-descriptor catalogue: the
/// display name and the loc-id-prefixed <c>UIName</c> the manifest stores
/// (e.g. <c>$[35720]CLEAN</c>).</summary>
public sealed record ToneDescriptorOption(string Name, string UIName);

/// <summary>
/// Pure helpers behind the in-editor Tone Designer (plan 2.6): tone-key
/// validation/uniquing, catalogue slicing for the gear slot pickers, the fixed
/// Rocksmith tone-descriptor catalogue and a default authored tone. Zero I/O,
/// deterministic, throw-free — the UI view model stays a thin binding shell and
/// everything here is unit-testable without an Avalonia instance.
/// </summary>
public static partial class RsToneAuthoring
{
    /// <summary>Generous cap — official keys stay well under this; it only
    /// guards against pathological input.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>Tone volume range the editor exposes, mirroring DLC Builder's
    /// tone-details volume control (0.1..36 dB of attenuation ⇒ −36..−0.1 dB).</summary>
    public const float MinVolumeDb = -36f;

    /// <inheritdoc cref="MinVolumeDb"/>
    public const float MaxVolumeDb = -0.1f;

    /// <summary>The volume a freshly authored tone starts at (the common
    /// official-content ballpark).</summary>
    public const float DefaultVolumeDb = -12f;

    /// <summary>The clean descriptor's UIName — the default for new tones
    /// (mirrors Rocksmith2014.NET's <c>getDescriptionsOrDefault</c> fallback).</summary>
    public const string CleanDescriptorUIName = "$[35720]CLEAN";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex KeyRegex();

    /// <summary>The fixed Rocksmith tone-descriptor catalogue, verbatim from
    /// Rocksmith2014.NET's <c>ToneDescriptor.all</c> (the loc ids are what the
    /// game resolves — do not invent new ones).</summary>
    public static IReadOnlyList<ToneDescriptorOption> Descriptors { get; } =
    [
        new("Acoustic", "$[35721]ACOUSTIC"),
        new("Banjo", "$[27201]BANJO"),
        new("Bass", "$[35715]BASS"),
        new("Chorus", "$[35723]CHORUS"),
        new("Clean", CleanDescriptorUIName),
        new("Crunch", "$[27156]CRUNCH"),
        new("Delay", "$[35753]DELAY"),
        new("Direct", "$[35752]DIRECT"),
        new("Distortion", "$[35722]DISTORTION"),
        new("Echo", "$[35754]ECHO"),
        new("Effect", "$[35733]EFFECT"),
        new("Emulated", "$[27119]EMULATED"),
        new("Filter", "$[35729]FILTER"),
        new("Flanger", "$[35731]FLANGER"),
        new("Fuzz", "$[35756]FUZZ"),
        new("High Gain", "$[35755]HIGH GAIN"),
        new("Lead", "$[35724]LEAD"),
        new("Low Output", "$[35732]LOW OUTPUT"),
        new("Mandolin", "$[27202]MANDOLIN"),
        new("Multi Effect", "$[35751]MULTI-EFFECT"),
        new("Octave", "$[35719]OCTAVE"),
        new("Overdrive", "$[35716]OVERDRIVE"),
        new("Phaser", "$[35730]PHASER"),
        new("Piano", "$[29495]PIANO"),
        new("Processed", "$[35734]PROCESSED"),
        new("Reverb", "$[35726]REVERB"),
        new("Rotary", "$[35725]ROTARY"),
        new("Special Effect", "$[35750]SPECIAL EFFECT"),
        new("Tremolo", "$[35727]TREMOLO"),
        new("Ukulele", "$[27204]UKULELE"),
        new("Vibrato", "$[35728]VIBRATO"),
        new("Vocal", "$[35718]VOCAL")
    ];

    /// <summary>Finds the descriptor option for a manifest UIName (ordinal).
    /// Null for unknown/legacy strings — the editor then shows them read-only
    /// but never drops them silently on load.</summary>
    public static ToneDescriptorOption? FindDescriptor(string? uiName)
    {
        if (string.IsNullOrEmpty(uiName)) return null;
        foreach (var d in Descriptors)
        {
            if (string.Equals(d.UIName, uiName, StringComparison.Ordinal))
                return d;
        }

        return null;
    }

    /// <summary>RS tone keys are identifier-like: an ASCII letter followed by
    /// ASCII letters/digits/underscores (they land in manifest attribute names
    /// like <c>Tone_A</c> lookups and Wwise event names).</summary>
    public static bool IsValidKey(string? key)
        => !string.IsNullOrEmpty(key) && key!.Length <= MaxKeyLength && KeyRegex().IsMatch(key);

    /// <summary>Folds arbitrary input into a valid tone key: keeps ASCII
    /// letters/digits/underscores, maps spaces/dashes to underscores, prefixes
    /// non-letter starts, truncates, and falls back to <c>"tone"</c> for empty
    /// input. Idempotent for already-valid keys.</summary>
    public static string SanitizeKey(string? raw)
    {
        var trimmed = raw?.Trim() ?? "";
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                sb.Append(c);
            else if (c is ' ' or '-')
                sb.Append('_');
            // anything else is dropped
        }

        var key = sb.ToString();
        if (key.Length == 0) return "tone";
        if (key[0] is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))) key = "tone_" + key;
        return key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];
    }

    /// <summary>Sanitizes <paramref name="desired"/> and suffixes <c>_2</c>,
    /// <c>_3</c>, … until it collides with none of <paramref name="existingKeys"/>
    /// (case-insensitive — tone-key matching is case-insensitive everywhere else).</summary>
    public static string MakeUniqueKey(string desired, IEnumerable<string> existingKeys)
    {
        var existing = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);
        var baseKey = SanitizeKey(desired);
        if (!existing.Contains(baseKey)) return baseKey;

        for (var i = 2; ; i++)
        {
            var suffix = "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var stem = baseKey.Length + suffix.Length <= MaxKeyLength
                ? baseKey
                : baseKey[..(MaxKeyLength - suffix.Length)];
            var candidate = stem + suffix;
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    /// <summary>The catalogue slice for one gear slot picker (63 amps /
    /// 450 cabinets / 83 pedals / 17 racks), sorted by display name for the UI.</summary>
    public static IReadOnlyList<RsGearData> GearOptions(RsGearCatalog catalog, RsGearType type)
    {
        var typeString = type switch
        {
            RsGearType.Amp => "Amps",
            RsGearType.Cabinet => "Cabinets",
            RsGearType.Pedal => "Pedals",
            RsGearType.Rack => "Racks",
            _ => ""
        };

        return catalog.AllGear
            .Where(g => string.Equals(g.Type, typeString, StringComparison.Ordinal))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every knob of <paramref name="gear"/> at its catalogue default,
    /// keyed by the FULL knob key (<c>&lt;GearKey&gt;_&lt;Pot&gt;</c>) — the shape
    /// <see cref="RsGear.KnobValues"/> carries.</summary>
    public static IReadOnlyDictionary<string, float> DefaultKnobValues(RsGearData gear)
    {
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        if (gear.Knobs is { } knobs)
        {
            foreach (var knob in knobs)
                values[knob.Key] = knob.DefaultValue;
        }

        return values;
    }

    /// <summary>Clamps a knob value into the knob's declared range.</summary>
    public static float ClampKnobValue(RsGearKnob knob, float value)
        => Math.Clamp(value, Math.Min(knob.MinValue, knob.MaxValue), Math.Max(knob.MinValue, knob.MaxValue));

    /// <summary>Clamps a tone volume into the editor's DLC-Builder-parity range.</summary>
    public static float ClampVolumeDb(float volumeDb)
        => Math.Clamp(volumeDb, MinVolumeDb, MaxVolumeDb);

    /// <summary>A freshly authored tone: the first catalogue amp at default
    /// knobs, the RS default cabinet, no pedals/racks, −12 dB, Clean descriptor.
    /// Degrades to the parser's synthetic keys on an empty catalogue (never
    /// throws, never returns null).</summary>
    public static RsTone CreateDefaultTone(string key, string name, RsGearCatalog catalog)
    {
        var empty = new Dictionary<string, float>(StringComparer.Ordinal);

        var ampGear = GearOptions(catalog, RsGearType.Amp).FirstOrDefault();
        var amp = ampGear is not null
            ? new RsAmp(ampGear.Key, ampGear.Category, DefaultKnobValues(ampGear))
            : new RsAmp(RsToneJsonParser.SyntheticAmpKey, null, empty);

        RsCab cab;
        if (catalog.TryGetGear(RsToneJsonParser.DefaultCabinetKey, out var defaultCab))
        {
            cab = new RsCab(defaultCab.Key, defaultCab.Category, empty);
        }
        else
        {
            var cabGear = GearOptions(catalog, RsGearType.Cabinet).FirstOrDefault();
            cab = new RsCab(cabGear?.Key ?? RsToneJsonParser.DefaultCabinetKey, cabGear?.Category, empty);
        }

        return new RsTone(
            key,
            string.IsNullOrWhiteSpace(name) ? key : name,
            DefaultVolumeDb,
            new[] { CleanDescriptorUIName },
            amp,
            cab,
            Array.Empty<RsPedal>(),
            Array.Empty<RsPedal>(),
            Array.Empty<RsPedal>());
    }
}
