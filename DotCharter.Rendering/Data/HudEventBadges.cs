using DotCharter.Rendering.Data.Render;

namespace DotCharter.Rendering.Data;

/// <summary>
///     Which SECTION and which PHRASE the 3D HUD names right now: the latest
///     event point of each kind at or before the playhead. Pure, so the rule can
///     be proven without a font atlas — <c>TextBatcher.DrawText2D</c> returns
///     immediately when no atlas is bound, which makes the drawing itself
///     unobservable off a GPU.
/// </summary>
/// <remarks>
///     <para>
///         <b>The two are INDEPENDENT.</b> `HudComponent` must not wrap both draws
///         in <c>if (currentPhrase.HasValue &amp;&amp; Phrase != "END")</c>, or a
///         chart with sections and no phrase events would show no section name at
///         all, and every chart would lose its section badge the moment the terminating
///         `END` phrase went by. The 2D timeline does not do this — `EventLayer`
///         .`DrawPinnedLabels` walks for the latest section and the latest phrase
///         behind two separate `!Found` gates — so the two renderers disagreed
///         about a label the same snapshot fed both.
///     </para>
///     <para>
///         <b>Same-named is not deduplicated.</b> A section and a phrase that
///         carry the same name both show; that was proposed and REJECTED (see the
///         owner's standing verification rule), and the resolver returns both
///         independently precisely so nothing is tempted to fold them.
///     </para>
/// </remarks>
public static class HudEventBadges
{
    /// <summary>The 3D view's section name colour; the 2D timeline's SECTIONS chips use it too.</summary>
    public static readonly Avalonia.Media.Color SectionColor = Avalonia.Media.Color.FromArgb(255, 165, 54, 178);

    /// <summary>The 3D view's phrase name colour; the 2D timeline's PHRASES chips use it too.</summary>
    public static readonly Avalonia.Media.Color PhraseColor = Avalonia.Media.Color.FromArgb(255, 179, 118, 54);

    /// <summary>The Rocksmith terminator phrase. It ends the chart rather than
    /// naming a part of it, so it is never shown as a phrase — but it says
    /// nothing about the section, which keeps its own name.</summary>
    public const string TerminatorPhrase = "END";

    /// <summary>Section and phrase names to show at <paramref name="timeMs" />;
    /// either may be empty.</summary>
    public static (string section, string phrase) Resolve(
        RenderEventPoint[] eventPoints, int count, double timeMs)
    {
        var section = "";
        var phrase = "";
        var haveSection = false;
        var havePhrase = false;

        for (var i = Math.Min(count, eventPoints.Length) - 1; i >= 0 && !(haveSection && havePhrase); i--)
        {
            var ep = eventPoints[i];
            if (ep.Time > timeMs) continue;

            if (!haveSection && !string.IsNullOrEmpty(ep.Section))
            {
                section = ep.Section;
                haveSection = true;
            }

            if (!havePhrase && !string.IsNullOrEmpty(ep.Phrase))
            {
                phrase = ep.Phrase;
                havePhrase = true;
            }
        }

        if (string.Equals(phrase, TerminatorPhrase, StringComparison.Ordinal)) phrase = "";
        return (section, phrase);
    }
}
