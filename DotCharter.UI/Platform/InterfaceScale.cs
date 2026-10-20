using DotCharter.Services.Infrastructure.Appearance;
using DotCharter.UI.Services;

namespace DotCharter.UI.Platform;

/// <summary><see cref="IInterfaceScale" /> over the process-wide <see cref="UiScaleController" /> and the fixed <see cref="InterfaceScaleChoices" />.</summary>
public sealed class InterfaceScale(ILocalizationService localization) : IInterfaceScale
{
    public IReadOnlyList<string> ChoiceLabels() => InterfaceScaleChoices.Labels(localization);

    public int IndexFor(bool auto, double factor) => InterfaceScaleChoices.IndexFor(auto, factor);

    public double? FactorFor(int index) => InterfaceScaleChoices.FactorFor(index);

    public bool IsAuto(int index) => InterfaceScaleChoices.IsAuto(index);

    public double Clamp(double factor) => UiScaleController.Clamp(factor);

    public double AppliedFactor => UiScaleController.Scale;

    /// <summary>
    ///     <c>SetManual</c> for a factor, because a pick leaves Auto; entering Auto
    ///     only flips the mode, and the shell re-derives from its client height.
    /// </summary>
    public void Apply(int index)
    {
        if (InterfaceScaleChoices.FactorFor(index) is { } factor) UiScaleController.SetManual(factor);
        else UiScaleController.SetAutoMode(true);
    }

    /// <summary>The mode first, then the manual value through <c>Set</c>, which does not cancel the mode.</summary>
    public void Restore(bool auto, double factor)
    {
        UiScaleController.SetAutoMode(auto);
        if (!auto) UiScaleController.Set(factor);
    }
}
