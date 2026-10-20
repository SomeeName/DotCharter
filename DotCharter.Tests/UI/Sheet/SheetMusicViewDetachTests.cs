using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI.Sheet;

/// <summary>
/// <c>SheetMusicView</c> is a DOCK PANE view: it is destroyed and re-created on
/// every float, redock and pop-out, while the three services it listens to
/// (<c>ProjectState</c>, <c>ISelectionService</c>, <c>IHistoryService</c>) are
/// singletons that live for the process. One missing <c>-=</c> roots every
/// detached strip — each holding its AlphaTab score, its rendered SKImage chunks
/// and its beat lookup — for the rest of the session.
/// <para><b>Why this exists next to <c>ViewSubscriptionLeakGuardTests</c>.</b>
/// That guard is deliberately structural: it only asserts that a control
/// overriding <c>OnAttachedToVisualTree</c> also overrides
/// <c>OnDetachedFromVisualTree</c>. It cannot see an override that EXISTS but
/// forgets one of its unsubscriptions — which is the shape this leak actually
/// takes, since the detach override is written once and then a fourth
/// subscription gets added to attach. This lint names the subscriptions.</para>
/// <para>A source lint rather than a headless attach/detach test on purpose:
/// building the view means building its <c>ItemsControl</c> content, which needs
/// a dispatcher and throws under the full gate (see DotCharter.Tests/AGENTS.md).</para>
/// </summary>
public class SheetMusicViewDetachTests
{
    /// <summary>The view and its collaborators (<see cref="SheetMusicViewSource" />), the view
    /// first: a subscription that moves into a collaborator is still seen.</summary>
    private static string ViewSource() => SheetMusicViewSource.ReadAllText();

    /// <summary>The body of <c>OnDetachedFromVisualTree</c>, comments stripped —
    /// a <c>//</c> mention of an unsubscription is exactly as inert as a
    /// <c>see cref</c>.</summary>
    private static string DetachBody()
    {
        var source = ViewSource();
        var start = source.IndexOf("protected override void OnDetachedFromVisualTree", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "SheetMusicView no longer overrides OnDetachedFromVisualTree — every float/redock now leaks " +
            "the strip and its rendered chunks.");

        var open = source.IndexOf('{', start);
        var depth = 0;
        var end = open;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0) { end = i; break; }
        }

        var body = source.Substring(open, end - open);
        return Regex.Replace(body, @"//[^\r\n]*", string.Empty);
    }

    [Theory]
    // Every singleton event the attach path subscribes to.
    [InlineData("PropertyChanged -= OnStateChanged", "ProjectState.PropertyChanged")]
    [InlineData("SelectionChanged -= OnSelectionChanged", "ISelectionService.SelectionChanged")]
    [InlineData("HistoryChanged -= OnHistoryChanged", "IHistoryService.HistoryChanged")]
    public void OnDetached_UnsubscribesFromEverySingletonServiceItListensTo(string expected, string what)
    {
        Assert.True(DetachBody().Contains(expected, StringComparison.Ordinal),
            $"SheetMusicView.OnDetachedFromVisualTree does not unsubscribe from {what} " +
            $"(looked for \"{expected}\"). A pane view is re-created on every float/redock, so the " +
            "singleton keeps every detached strip alive.");
    }

    /// <summary>
    /// Stopping the debounce only prevents the NEXT rebuild. A render already in
    /// flight would keep an AlphaTab layout + AlphaSkia raster running for a view
    /// nobody will ever show again — every float/redock during a rebuild burned
    /// one.
    /// </summary>
    [Fact]
    public void OnDetached_StopsTheDebounce_AndCancelsAnInFlightRender()
    {
        var body = DetachBody();

        Assert.True(body.Contains("_debounce.Stop()", StringComparison.Ordinal),
            "SheetMusicView.OnDetachedFromVisualTree must stop the rebuild debounce.");
        Assert.True(body.Contains("_renderCts?.Cancel()", StringComparison.Ordinal),
            "SheetMusicView.OnDetachedFromVisualTree must cancel the in-flight render — stopping the " +
            "debounce alone only prevents the NEXT rebuild.");
    }

    /// <summary>
    /// Proves the lint above is looking at the right place: every unsubscription
    /// it demands has a matching <c>+=</c> somewhere in the file, so the list
    /// cannot silently drift into naming events the view never takes.
    /// </summary>
    [Theory]
    [InlineData("PropertyChanged += OnStateChanged")]
    [InlineData("SelectionChanged += OnSelectionChanged")]
    [InlineData("HistoryChanged += OnHistoryChanged")]
    public void TheSubscriptionsThisLintNames_AreRealSubscriptionsTheViewTakes(string subscription) => Assert.Contains(subscription, ViewSource(), StringComparison.Ordinal);
}
