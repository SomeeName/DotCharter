using System.Text.Json;
using Avalonia.Platform;

namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>What a control on a faceplate IS, which decides what the app draws on
/// its nut: a knob cap, a stepped knob, a rotary selector, a bat toggle or a model
/// window.</summary>
public enum RigFaceControlKind
{
    Knob,
    SteppedKnob,
    Selector,
    Toggle,
    Window
}

/// <summary>One control on a faceplate: the parameter it turns and where its nut
/// is, as FRACTIONS of the face image (centre x/y; <see cref="Diameter" /> is the
/// nut's diameter as a fraction of the image width). <see cref="Min" />,
/// <see cref="Max" /> and <see cref="Default" /> are the param's catalogue range, so
/// a preview with no live unit (the gear library) turns the knob to where a fresh
/// unit starts.</summary>
public sealed record RigFaceControl(
    string Param, RigFaceControlKind Kind, double X, double Y, double Diameter,
    double Min = 0, double Max = 1, double Default = 0.5,
    IReadOnlyList<double>? Angles = null);

/// <summary>A round spot on a face (a footswitch): centre as fractions of the image,
/// diameter as a fraction of its width.</summary>
public sealed record RigFaceSpot(double X, double Y, double Diameter);

/// <summary>
///     A module's generated faceplate: the photoreal unit with a BARE NUT where each
///     control goes, and where those nuts are. The app draws the live control on
///     each nut, so the knob in the picture is the knob the player turns.
/// </summary>
/// <param name="ModuleTypeId">The native module type.</param>
/// <param name="File">The face image under <see cref="RigFaceplates.FacesRoot" />.</param>
/// <param name="PixelWidth">The image's width, for the aspect ratio.</param>
/// <param name="PixelHeight">The image's height.</param>
/// <param name="KnobStyle">The knob-cap style this unit wears (<c>Aluminium</c>, <c>Cream</c>, …).</param>
/// <param name="Controls">Every control drawn on the face, each on its nut.</param>
/// <param name="Footswitch">Where a floor unit's printed footswitch stands (centre and
/// collar diameter, the same fractions as a control), or null for a head, a rack or a
/// treadle: the app makes it the unit's bypass switch.</param>
public sealed record RigFaceplate(
    string ModuleTypeId,
    string File,
    int PixelWidth,
    int PixelHeight,
    string KnobStyle,
    IReadOnlyList<RigFaceControl> Controls,
    RigFaceSpot? Footswitch = null)
{
    /// <summary>Width over height.</summary>
    public double AspectRatio => PixelHeight > 0 ? (double)PixelWidth / PixelHeight : 1;

    /// <summary>The face image's full asset URI.</summary>
    public string Uri => RigFaceplates.FacesRoot + File;

    /// <summary>The knob-cap sprite this unit's knobs wear.</summary>
    public string KnobCapUri => RigFaceplates.CapsRoot + "knob-" + KnobStyle.ToLowerInvariant() + ".webp";

    /// <summary>
    ///     The diameter every knob cap on this face is drawn at, as a fraction of the
    ///     image width: 1.6x the face's typical nut, so the cap covers the nut and sits
    ///     inside the printed tick ring. One size per face — a row of knobs reads as a
    ///     row only when the caps match, and the detector's nut sizes vary by a few
    ///     pixels from knob to knob.
    /// </summary>
    public double KnobCapDiameter
    {
        get
        {
            var knobs = Controls.Where(c => c.Kind is not (RigFaceControlKind.Toggle or RigFaceControlKind.Window))
                .Select(c => c.Diameter).Order().ToList();
            if (knobs.Count == 0) return 0.1;
            var median = knobs[knobs.Count / 2];
            return Math.Clamp(median * 1.6, 0.035, 0.2);
        }
    }

    /// <summary>The control for <paramref name="param" />, or null.</summary>
    public RigFaceControl? ControlFor(string param) => Controls.FirstOrDefault(c => c.Param == param);
}

/// <summary>
///     The faceplate catalogue: <c>Assets/Rig/faceplates.json</c>, written by
///     <c>tools/rig-art/import-faceplates.py</c> from the generated art. Loaded once,
///     on first use; a module with no entry keeps the drawn enclosure.
/// </summary>
public static class RigFaceplates
{
    public const string FacesRoot = "avares://DotCharter.Tone.UI/Assets/Rig/Faces/";
    public const string CapsRoot = "avares://DotCharter.Tone.UI/Assets/Rig/Caps/";
    public const string CatalogUri = "avares://DotCharter.Tone.UI/Assets/Rig/faceplates.json";

    /// <summary>The shared toggle / selector / LED / footswitch sprites.</summary>
    public const string ToggleUp = CapsRoot + "toggle-up.webp";
    public const string ToggleDown = CapsRoot + "toggle-down.webp";
    public const string SelectorCap = CapsRoot + "selector-cap.webp";

    private static readonly Lazy<IReadOnlyDictionary<string, RigFaceplate>> Catalog = new(Load, isThreadSafe: true);

    /// <summary>Modules with NO faceplate on purpose, drawn as the enclosure instead:
    /// <c>rsamp</c>, whose ten knobs take the names of whichever Rocksmith amp
    /// its picker names — a printed face would print the wrong ones, the enclosure's
    /// captions follow the pick.</summary>
    public static IReadOnlySet<string> DrawnEnclosureByDesign { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "rsamp" };

    /// <summary>Every faceplate, by module type id.</summary>
    public static IReadOnlyDictionary<string, RigFaceplate> All => Catalog.Value;

    /// <summary>The faceplate for <paramref name="moduleTypeId" />, or null.</summary>
    public static RigFaceplate? For(string? moduleTypeId)
        => !SuppressedForTests && moduleTypeId is not null && All.TryGetValue(moduleTypeId, out var face) ? face : null;

    /// <summary>Test seam: pretend no module has a faceplate, so the drawn-enclosure
    /// FALLBACK can still be laid out and asserted. Headless UI tests only (one
    /// serialized collection), always reset in a <c>finally</c>.</summary>
    internal static bool SuppressedForTests { get; set; }

    private static IReadOnlyDictionary<string, RigFaceplate> Load()
    {
        try
        {
            var uri = new Uri(CatalogUri);
            if (!AssetLoader.Exists(uri)) return new Dictionary<string, RigFaceplate>();
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception)
        {
            // A catalogue that cannot be read means drawn enclosures, never a crash.
            return new Dictionary<string, RigFaceplate>();
        }
    }

    /// <summary>Parses the catalogue JSON. Pure, for tests.</summary>
    public static IReadOnlyDictionary<string, RigFaceplate> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var styles = root.TryGetProperty("knobStyles", out var s)
            ? s.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "Aluminium", StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, RigFaceplate>(StringComparer.Ordinal);
        foreach (var face in root.GetProperty("faces").EnumerateObject())
        {
            var v = face.Value;
            var controls = v.GetProperty("controls").EnumerateArray().Select(c => new RigFaceControl(
                c.GetProperty("param").GetString()!,
                KindOf(c.GetProperty("kind").GetString()),
                c.GetProperty("x").GetDouble(),
                c.GetProperty("y").GetDouble(),
                c.GetProperty("d").GetDouble(),
                c.TryGetProperty("min", out var min) ? min.GetDouble() : 0,
                c.TryGetProperty("max", out var max) ? max.GetDouble() : 1,
                c.TryGetProperty("default", out var def) ? def.GetDouble() : 0.5,
                // where a selector's positions are PRINTED, one angle per value
                c.TryGetProperty("angles", out var angles)
                    ? angles.EnumerateArray().Select(a => a.GetDouble()).ToArray()
                    : null)).ToList();
            result[face.Name] = new RigFaceplate(
                face.Name,
                v.GetProperty("file").GetString()!,
                v.GetProperty("width").GetInt32(),
                v.GetProperty("height").GetInt32(),
                styles.TryGetValue(face.Name, out var style) ? style : "Aluminium",
                controls,
                v.TryGetProperty("footswitch", out var foot)
                    ? new RigFaceSpot(foot.GetProperty("x").GetDouble(), foot.GetProperty("y").GetDouble(), foot.GetProperty("d").GetDouble())
                    : null);
        }

        return result;
    }

    private static RigFaceControlKind KindOf(string? kind) => kind switch
    {
        "knob_stepped" => RigFaceControlKind.SteppedKnob,
        "rotary_selector" => RigFaceControlKind.Selector,
        "toggle" => RigFaceControlKind.Toggle,
        "model_window" => RigFaceControlKind.Window,
        _ => RigFaceControlKind.Knob
    };
}
