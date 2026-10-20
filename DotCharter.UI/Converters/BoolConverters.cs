using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DotCharter.UI.Converters;

/// <summary>
///     One-way converters that turn a <see cref="bool" /> into other primitive
///     visual properties.
/// </summary>
public static class BoolConverters
{
    // Reserved for future converters. ToOpacity was removed — all tab systems
    // now use IsVisible gating which is more efficient and is backed by the
    // PrewarmTopLevelViews pattern for first-switch performance.

    /// <summary>
    ///     Maps <c>ILocalizationService.IsRightToLeft</c> to a
    ///     <see cref="FlowDirection" /> so the shell root can flip to RTL when a
    ///     right-to-left language pack is active. Default (LTR) leaves every
    ///     surface untouched, so this is a no-op for the built-in languages.
    /// </summary>
    public static readonly FuncValueConverter<bool, FlowDirection> ToFlowDirection =
        new(rtl => rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
}
