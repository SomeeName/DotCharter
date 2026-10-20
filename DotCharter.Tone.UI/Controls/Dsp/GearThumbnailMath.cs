namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>The miniature silhouette a gear-rail card draws — the thumbnail
/// mirror of the board's <see cref="PedalSkin"/> families. Kept separate from
/// <see cref="PedalSkin"/> because the thumbnail promotes amp-FAMILY modules
/// without a specialized board face onto the wide chassis so they read as amps
/// in the rail — family silhouette beats per-module detail at 100 px.</summary>
public enum GearThumbKind
{
    /// <summary>Narrow stompbox with LED + footswitch (the fallback).</summary>
    Stompbox,
    /// <summary>Wide chassis + faceplate band + power lamp (amp family).</summary>
    AmpHead,
    /// <summary>Wide chassis + grille + speaker driver (cab family).</summary>
    Cabinet,
    /// <summary>Fader bank slivers instead of knobs (graphiceq).</summary>
    GraphicEq,
    /// <summary>Angled treadle plate (wah).</summary>
    Wah,
    /// <summary>Routing junction (splitter/merger): a slim patchbay card with the
    /// fan-out/fan-in fork — NOT a stompbox.</summary>
    Junction
}

/// <summary>
/// Resolved miniature-face geometry for one gear card (see
/// <see cref="GearThumbnailMath.Compute"/>). All values are in the card's
/// local pixel space.
/// </summary>
public readonly record struct GearThumbnailLayout(
    double FaceX,
    double FaceY,
    double FaceWidth,
    double FaceHeight,
    double KnobRadius,
    double KnobCenterY,
    double[] KnobCenterXs,
    double NameBandY,
    double NameBandHeight);

/// <summary>
/// Pure layout math for the gear-card miniature — the custom-drawn
/// miniature of a DSP module's real skin shown on the gear-rail cards. Given
/// the card bounds, the module's knob count and its wide-vs-narrow silhouette
/// it yields the face rect, a centred single row of mini knob positions
/// (capped so a many-knob module never overcrowds the miniature) and the name
/// band. No Avalonia types — unit-tested in <c>GearRailTests</c>.
/// </summary>
public static class GearThumbnailMath
{
    /// <summary>Most mini knobs a thumbnail draws — a 10-knob EQ still reads
    /// as "a pedal with knobs" at card scale.</summary>
    public const int MaxMiniKnobs = 4;

    /// <summary>Fraction of the card width a stompbox face occupies (amp
    /// heads/cabs span the full width, stompboxes read as a narrower box).</summary>
    public const double StompboxWidthFraction = 0.62;

    /// <summary>Fraction of the card height reserved for the name band under
    /// the face.</summary>
    public const double NameBandFraction = 0.24;

    /// <summary>The thumbnail silhouette for a module id: specialized board
    /// skins keep their family (<see cref="DspModuleVisuals.SkinFor"/>), the
    /// routing ids (<c>splitter</c>/<c>merger</c>) draw the junction patchbay,
    /// and a module WITHOUT a specialized face is promoted onto the wide chassis
    /// when its visual family is Amp or Cab — so a future amp-family module with
    /// no board skin of its own still reads as an amp on the rail. Everything
    /// else stays the stompbox — EVERY catalogue id therefore renders a real
    /// face, never a blank plate (gated by <c>GearRailTests</c> against
    /// <c>DcRigTargetCatalog.ModuleTypeIds</c>).</summary>
    public static GearThumbKind KindFor(string moduleTypeId) =>
        DspModuleVisuals.SkinFor(moduleTypeId) switch
        {
            PedalSkin.AmpHead or PedalSkin.NeuralAmp => GearThumbKind.AmpHead,
            PedalSkin.Cabinet => GearThumbKind.Cabinet,
            PedalSkin.GraphicEq => GearThumbKind.GraphicEq,
            PedalSkin.Wah => GearThumbKind.Wah,
            PedalSkin.Junction => GearThumbKind.Junction,
            _ => DspModuleVisuals.Categorize(moduleTypeId) switch
            {
                DspModuleCategory.Amp => GearThumbKind.AmpHead,
                DspModuleCategory.Cab => GearThumbKind.Cabinet,
                _ => GearThumbKind.Stompbox
            }
        };

    /// <summary>True for the region-opening splitter (its fork fans OUT); the
    /// merger's mirror fans in. Only meaningful for
    /// <see cref="GearThumbKind.Junction"/>.</summary>
    public static bool IsFanOut(string moduleTypeId)
        => string.Equals(moduleTypeId, SplitRegionLayout.SplitterId, StringComparison.Ordinal);

    /// <summary>True when the kind renders on the full-width chassis.</summary>
    public static bool IsWide(GearThumbKind kind)
        => kind is GearThumbKind.AmpHead or GearThumbKind.Cabinet;

    /// <summary>Number of knobs the miniature actually draws for a module
    /// with <paramref name="paramCount"/> parameters (at least one — every
    /// module face carries a knob so the card reads as gear, capped at
    /// <see cref="MaxMiniKnobs"/>).</summary>
    public static int MiniKnobCount(int paramCount)
        => Math.Clamp(paramCount, 1, MaxMiniKnobs);

    /// <summary>
    /// Computes the miniature layout. Degenerate bounds (≤ 0) collapse to an
    /// empty layout the caller can skip drawing.
    /// </summary>
    public static GearThumbnailLayout Compute(
        double width, double height, int paramCount, bool isWide)
    {
        if (width <= 0 || height <= 0)
            return new GearThumbnailLayout(0, 0, 0, 0, 0, 0, [], 0, 0);

        var nameBandHeight = height * NameBandFraction;
        var faceHeight = height - nameBandHeight;
        var nameBandY = faceHeight;

        double faceWidth, faceX;
        if (isWide)
        {
            // Amp/cab: a wide, slightly squat chassis spanning the card.
            faceWidth = width;
            faceX = 0;
            var squash = faceHeight * 0.12;
            faceHeight -= squash;
            nameBandY = faceHeight + squash * 0.4;
        }
        else
        {
            faceWidth = width * StompboxWidthFraction;
            faceX = (width - faceWidth) / 2.0;
        }

        var knobs = MiniKnobCount(paramCount);

        // Knob row: centred horizontally on the face, sized so the row (with
        // one knob-radius of breathing room per side) always fits the face.
        var maxRadiusByWidth = faceWidth / (2.0 * (knobs + 1));
        var maxRadiusByHeight = faceHeight * 0.16;
        var radius = Math.Min(maxRadiusByWidth, maxRadiusByHeight);

        var centers = new double[knobs];
        var span = faceWidth / (knobs + 1);
        for (var i = 0; i < knobs; i++)
            centers[i] = faceX + span * (i + 1);

        // Amp heads carry their knob row low (a recessed control panel);
        // stompboxes centre it in the upper body above the "footswitch" zone.
        var knobCenterY = isWide ? faceHeight * 0.68 : faceHeight * 0.42;

        return new GearThumbnailLayout(
            faceX, 0, faceWidth, faceHeight,
            radius, knobCenterY, centers,
            nameBandY, nameBandHeight);
    }
}
