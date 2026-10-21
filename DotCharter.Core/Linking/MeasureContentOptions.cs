using System.Globalization;

namespace DotCharter.Core.Linking;

/// <summary>The two roles of a measure-content key.</summary>
public enum MeasureContentKind
{
    /// <summary>
    ///     What SOUNDS, for detection and for checking that linked copies still agree:
    ///     onsets, strings, frets and techniques exactly on the beat grid; a sustain or hand
    ///     shape that reaches the next sound within the spacing gap reads as "held to it";
    ///     the fret-hand position in effect at each onset. Blind to everything the clean-up
    ///     passes change, so copies in different neighbourhoods and tempos compare equal.
    /// </summary>
    Detection,

    /// <summary>
    ///     The FULL stored content of the run, exactly: sustains, bends, every flag,
    ///     notation durations and notation voices, stored anchors and hand shapes, template
    ///     names. For fingerprinting ONE placement (has this copy changed?), never for
    ///     comparing two placements — clean-up legitimately makes copies differ here.
    /// </summary>
    Integrity
}

/// <summary>
///     The knobs of the measure-content comparison (<see cref="MeasureContentIndex" />). Every
///     knob is written into the key's header, so a key built under different options never
///     equals one built under these, and a stored hash from an older rule simply stops matching.
/// </summary>
public sealed record MeasureContentOptions
{
    /// <summary>The format version, first token of every key. Bump it with any change to what a key holds.</summary>
    public const string FormatVersion = "mc2";

    /// <summary>The rule linked measures use for detection and verification.</summary>
    public static readonly MeasureContentOptions Detection = new();

    /// <summary>The full-content fingerprint of one placement.</summary>
    public static readonly MeasureContentOptions Integrity = new() { Kind = MeasureContentKind.Integrity };

    /// <summary>Detection over the sounds and techniques only: no anchors, hand shapes or fingers.
    /// For comparisons where the hand data is regenerated (a Guitar Pro round trip).</summary>
    public static readonly MeasureContentOptions SoundsOnly = new()
    {
        IncludeAnchors = false,
        IncludeHandShapes = false,
        IncludeFingering = false
    };

    public MeasureContentKind Kind { get; init; } = MeasureContentKind.Detection;

    /// <summary>
    ///     The spacing gap the chart was cleaned up with (the app's minimum note distance).
    ///     Detection reads a sustain as "held to the next sound" exactly when it ends within
    ///     this gap of it — the bound <c>ChartSpacingEnforcer</c> clamps to, evaluated at that
    ///     sound's own position and tempo. A staccato note further from the next onset than the
    ///     gap keeps its exact length.
    /// </summary>
    public SpacingGap Spacing { get; init; } = SpacingGap.AppDefault;

    /// <summary><c>ChartSpacingEnforcer</c> snaps an anchor within this many ms of a sound onto it,
    /// so in detection an anchor just after an onset already governs it.</summary>
    public double AnchorSnapToleranceMs { get; init; } = 5.0;

    /// <summary>Detection: compare the fret-hand position in effect at every onset.</summary>
    public bool IncludeAnchors { get; init; } = true;

    /// <summary>Detection: compare the hand shapes over the run (clipped to it).</summary>
    public bool IncludeHandShapes { get; init; } = true;

    /// <summary>Detection: compare left-hand fingers (single-note fingers and chord-template fingers).</summary>
    public bool IncludeFingering { get; init; } = true;

    /// <summary>
    ///     Reads a <see cref="Header" /> back into the options that wrote it — the inverse that
    ///     makes a stored link rule self-describing. False for a header of another format
    ///     version or one this build cannot read; the caller then treats the stored hash as
    ///     unverifiable rather than as a divergence.
    /// </summary>
    public static bool TryParseHeader(string? header, out MeasureContentOptions options)
    {
        options = Detection;
        if (string.IsNullOrEmpty(header)) return false;
        var parts = header.Split(';');
        if (parts[0] != FormatVersion) return false;
        if (parts.Length == 2 && parts[1] == "int")
        {
            options = Integrity;
            return true;
        }

        if (parts.Length != 5 || parts[1] != "det" || !TryParseSpacing(parts[2], out var spacing)) return false;
        if (!parts[3].StartsWith("snap", StringComparison.Ordinal) ||
            !double.TryParse(parts[3].AsSpan(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var snap))
        {
            return false;
        }

        var flags = parts[4];
        if (flags.Any(c => c is not ('A' or 'H' or 'F'))) return false;
        options = new MeasureContentOptions
        {
            Spacing = spacing,
            AnchorSnapToleranceMs = snap,
            IncludeAnchors = flags.Contains('A'),
            IncludeHandShapes = flags.Contains('H'),
            IncludeFingering = flags.Contains('F')
        };
        return options.Header() == header;
    }

    private static bool TryParseSpacing(string text, out SpacingGap spacing)
    {
        spacing = SpacingGap.AppDefault;
        if (text.Length < 3) return false;
        var number = text.AsSpan(2);
        switch (text[..2])
        {
            case "ms" when double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms):
                spacing = new SpacingGap(SpacingGapMode.Milliseconds, 64, ms);
                return true;
            case "mf" when int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var mf) && mf > 0:
                spacing = new SpacingGap(SpacingGapMode.MeasureFraction, mf, 10.0);
                return true;
            case "bf" when int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var bf) && bf > 0:
                spacing = new SpacingGap(SpacingGapMode.BeatFraction, bf, 10.0);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The key header: version, kind and every knob that changes the key.</summary>
    public string Header() => Kind == MeasureContentKind.Integrity
        ? $"{FormatVersion};int"
        : string.Create(CultureInfo.InvariantCulture,
            $"{FormatVersion};det;{Spacing.Describe()};snap{AnchorSnapToleranceMs:0.###};{(IncludeAnchors ? "A" : "")}{(IncludeHandShapes ? "H" : "")}{(IncludeFingering ? "F" : "")}");
}
