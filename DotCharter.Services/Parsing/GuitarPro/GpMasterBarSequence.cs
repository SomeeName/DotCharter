using System.Globalization;
using System.Xml.Linq;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Expands a GP bar sequence into the linear playback order, honouring
///     <c>Repeat start/end</c> markers and the alternate-ending bitmask. The
///     result is the order a Rocksmith-style linear chart should follow.
///     <para>
///         <b>Both</b> Guitar Pro importers unfold through this one type — the
///         gpif path over <c>&lt;MasterBar&gt;</c> elements
///         (<see cref="Unfold(XElement)" />) and the GP3/4/5 binary path over
///         its measure headers (<see cref="Unfold(IReadOnlyList{BarRepeat})" />,
///         the shared core). The binary reader must not READ the repeat bits and
///         throw them away: a <c>.gp5</c> with <c>|: 8 bars :|</c> would import
///         eight bars short and every later note would land early. Never write a
///         second unfolder: an importer that disagrees about the playback order
///         is exactly that bug again.
///     </para>
///     <para>
///         Every <see cref="UnfoldedBar" /> records the original 0-based bar
///         index (for tempo / track-bar lookups) and, for gpif, the XML element
///         so callers can read per-bar attributes (time signature, key, triplet
///         feel) without re-walking the document.
///     </para>
///     <para>
///         A repeat CLOSE with no matching OPEN opens implicitly at the top of
///         the current block (bar 0, or the bar after the previous close) — that
///         is what GP itself plays. An OPEN with no close is the malformed case
///         and falls back to straight playback so no bar is lost.
///     </para>
/// </summary>
internal static class GpMasterBarSequence
{
    public readonly record struct UnfoldedBar(int OriginalIndex, XElement Element, int RepeatPass);

    /// <summary>One bar's repeat markers, spelled the same way by both file
    /// formats so the unfold core need not know which one it came from.
    /// <c>RepeatCount</c> is the TOTAL number of times the section is played
    /// (gpif's <c>count</c>, GP5's end-of-repeat byte, GP3/4's "number of
    /// repeats" byte + 1); anything &lt;= 0 means "not stated" and reads as 2.
    /// <c>AlternateEndingsMask</c> is a bitmask over passes, bit 0 = pass
    /// 1.</summary>
    public readonly record struct BarRepeat(
        bool RepeatStart,
        bool RepeatEnd,
        int RepeatCount,
        int AlternateEndingsMask);

    public readonly record struct UnfoldedIndex(int OriginalIndex, int RepeatPass);

    /// <summary>Most times one repeat section may be played. The
    /// alternate-ending bitmask only spells 8 passes anyway, so anything
    /// past this is unusable as well as unauthorable.</summary>
    private const int MaxRepeatPasses = 64;

    /// <summary>The shared unfold. Pure: bar markers in, playback order out.</summary>
    public static List<UnfoldedIndex> Unfold(IReadOnlyList<BarRepeat> bars)
    {
        var unfolded = new List<UnfoldedIndex>(Math.Min(bars.Count, GpFormatBounds.MaxUnfoldedBars));
        // Checked after each straight run and each pass, so the list never
        // grows past the cap by more than one run of the input's own bars.
        void Bound()
        {
            if (unfolded.Count > GpFormatBounds.MaxUnfoldedBars)
            {
                throw new InvalidDataException(
                    $"The score unfolds to more than {GpFormatBounds.MaxUnfoldedBars} bars once its repeats are expanded.");
            }
        }

        var i = 0;
        while (i < bars.Count)
        {
            // Find the next repeat block, if any. Both markers are scanned,
            // because a repeat CLOSE with no matching OPEN is not a no-op:
            // Guitar Pro (and alphaTab) play `:|` with no `|:` as a jump back to
            // the start of the piece — or, after an earlier close, to the first
            // bar of the section that follows it, which is `i` here. Emitting it
            // verbatim silently dropped a whole repeated section, and every
            // later note landed early by exactly that many bars.
            var explicitStart = -1;
            var firstEnd = -1;
            for (var k = i; k < bars.Count; k++)
            {
                if (explicitStart < 0 && bars[k].RepeatStart) explicitStart = k;
                if (firstEnd < 0 && bars[k].RepeatEnd) firstEnd = k;
                if (explicitStart >= 0 && firstEnd >= 0) break;
            }

            var repeatStart = explicitStart;
            // A close that comes BEFORE the next open (or with no open at all)
            // opens implicitly at the top of the current block.
            if (firstEnd >= 0 && (explicitStart < 0 || firstEnd < explicitStart)) repeatStart = i;

            if (repeatStart == -1)
            {
                // No more repeats — emit the remaining bars verbatim.
                for (var k = i; k < bars.Count; k++)
                    unfolded.Add(new UnfoldedIndex(k, 1));
                Bound();
                break;
            }

            // Emit any straight bars before the repeat start.
            for (var k = i; k < repeatStart; k++)
                unfolded.Add(new UnfoldedIndex(k, 1));
            Bound();

            // Find matching repeat end on or after repeatStart.
            var repeatEnd = -1;
            // The repeat count is the total number of times the section is
            // played (matches alphaTab + GP's own playback). Default to 2 (one
            // initial pass + one repeat) when it is missing or invalid.
            var totalPasses = 2;
            for (var k = repeatStart; k < bars.Count; k++)
            {
                if (!bars[k].RepeatEnd) continue;
                repeatEnd = k;
                // The count is unfolded into real bars, so it is an emit
                // loop driven straight by the file: `count="2000000000"`
                // over a 4-bar repeat asked for 8 billion UnfoldedBars —
                // the import hangs and then OOMs. Guitar Pro's UI cannot
                // author anywhere near this; alphaTab clamps the same way.
                if (bars[k].RepeatCount > 0) totalPasses = Math.Min(bars[k].RepeatCount, MaxRepeatPasses);
                break;
            }

            if (repeatEnd == -1)
            {
                // Malformed — treat as straight playback to avoid losing bars.
                for (var k = repeatStart; k < bars.Count; k++)
                    unfolded.Add(new UnfoldedIndex(k, 1));
                Bound();
                break;
            }

            for (var pass = 1; pass <= totalPasses; pass++)
            {
                for (var k = repeatStart; k <= repeatEnd; k++)
                {
                    var altMask = bars[k].AlternateEndingsMask;
                    // altMask == 0 means "always play"; otherwise only on the
                    // matching pass(es).
                    if (altMask != 0 && (altMask & (1 << (pass - 1))) == 0)
                        continue;
                    unfolded.Add(new UnfoldedIndex(k, pass));
                }

                Bound();
            }

            i = repeatEnd + 1;
        }

        return unfolded;
    }

    public static List<UnfoldedBar> Unfold(XElement masterBarsNode)
    {
        var rawBars = masterBarsNode.Elements("MasterBar").ToList();
        var markers = new List<BarRepeat>(rawBars.Count);

        foreach (var bar in rawBars)
        {
            var rep = bar.Element("Repeat");
            var count = 0;
            if (rep != null) int.TryParse(rep.Attribute("count")?.Value, CultureInfo.InvariantCulture, out count);
            markers.Add(new BarRepeat(
                rep != null && IsTrue(rep.Attribute("start")),
                rep != null && IsTrue(rep.Attribute("end")),
                count,
                ParseAlternateEndingsMask(bar.Element("AlternateEndings")?.Value)));
        }

        var unfolded = Unfold(markers);
        var result = new List<UnfoldedBar>(unfolded.Count);
        foreach (var entry in unfolded)
            result.Add(new UnfoldedBar(entry.OriginalIndex, rawBars[entry.OriginalIndex], entry.RepeatPass));
        return result;
    }

    private static bool IsTrue(XAttribute? attribute)
        => attribute != null && string.Equals(attribute.Value, "true", StringComparison.OrdinalIgnoreCase);

    private static int ParseAlternateEndingsMask(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var mask = 0;
        foreach (var token in raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= 8)
                mask |= 1 << (n - 1);
        }

        return mask;
    }
}
