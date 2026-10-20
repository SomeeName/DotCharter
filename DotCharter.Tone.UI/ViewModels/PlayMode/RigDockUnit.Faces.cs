// RigDockUnit: the AMP FACE projection - shell artwork, brand plate and lamp for the two amp heads.

using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class RigDockUnit
{
    /// <summary>True for the amp family: the wide head with the shell artwork,
    /// the brand plate, the six face knobs and the panel lamp.</summary>
    public bool IsAmpFamily => Family == RigEditorFamily.AmpFace;

    /// <summary>The shell picture behind the face: the NAM shell for a neural
    /// capture, the brushed legacy shell for the built-in valve amp.</summary>
    public string AmpShellFile => RigArtwork.ShellFor(Node.ModuleTypeId);

    /// <summary>A neural capture player (NAM / AIDA-X) rather than the built-in
    /// valve amp — decides the brand plate's wording.</summary>
    public bool IsNeuralAmpFace => Node.IsNeuralAmp;

    /// <summary>The brand mark on the enclosure and the racks.</summary>
    public string BrandMark => RigArtwork.BrandMark;

    /// <summary>The head's plate: NAM for a capture player, LEGACY for the
    /// built-in valve amp.</summary>
    public string AmpBrandTitle => IsNeuralAmpFace ? RigArtwork.NeuralBrand : RigArtwork.LegacyBrand;

    /// <summary>The line under the brand: the neural product's expansion, or
    /// the localized "valve amplifier".</summary>
    public string AmpBrandSubtitle => IsNeuralAmpFace
        ? RigArtwork.NeuralBrandSubtitle
        : _loc["Rig_Editor_LegacySub"];

    /// <summary>The design lays the six face knobs across the panel; a module
    /// with fewer still spreads what it has.</summary>
    public int AmpFaceColumns => Math.Clamp(PrimaryKnobs.Count, 1, 6);

    /// <summary>The secondary row's card count (resonance, input, output) plus
    /// the signal panel beside them.</summary>
    public bool HasAmpSecondaryRow => SecondaryKnobs.Count > 0;
}
