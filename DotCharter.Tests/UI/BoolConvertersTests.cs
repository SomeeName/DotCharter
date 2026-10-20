namespace DotCharter.Tests.UI;

/// <summary>
///     Tests for the value converters that drive the cached-tab visibility
///     model. Tabs are now gated via IsVisible directly — ToOpacity was
///     removed in favour of the more efficient IsVisible approach backed
///     by the PrewarmTopLevelViews pattern for first-switch performance.
/// </summary>
public sealed class BoolConvertersTests
{
    // ToOpacity tests removed alongside the converter — all tab systems now
    // use IsVisible gating. See MainWindow.PrewarmTopLevelViews for the
    // replacement pattern.
}
