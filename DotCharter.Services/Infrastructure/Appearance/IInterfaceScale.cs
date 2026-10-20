namespace DotCharter.Services.Infrastructure.Appearance;

/// <summary>
///     The interface-scale setting as a settings screen drives it: a fixed list
///     of choices whose first entry is Auto (derived from the window) and whose
///     rest are fixed factors. Implemented in <c>DotCharter.UI</c> over the
///     process-wide scale controller the shell applies.
/// </summary>
public interface IInterfaceScale
{
    /// <summary>The choice labels in the current culture, Auto first.</summary>
    IReadOnlyList<string> ChoiceLabels();

    /// <summary>The choice to show for a persisted (auto, factor) pair; an off-list factor snaps to the nearest one.</summary>
    int IndexFor(bool auto, double factor);

    /// <summary>The manual factor a choice selects, or null for Auto and anything out of range.</summary>
    double? FactorFor(int index);

    bool IsAuto(int index);

    /// <summary>Bounds a manual factor to the range the shell accepts.</summary>
    double Clamp(double factor);

    /// <summary>The factor the shell is applying now; under Auto, the one it derived.</summary>
    double AppliedFactor { get; }

    /// <summary>A user's pick, previewed live: a factor leaves Auto, the Auto choice enters it.</summary>
    void Apply(int index);

    /// <summary>Puts back a persisted mode and factor without treating it as a pick.</summary>
    void Restore(bool auto, double factor);
}
