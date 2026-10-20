using System.Globalization;
using System.Xml.Linq;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Extracts song-level metadata (time-signature sequence, initial key,
///     tempo automations) from a Guitar Pro <c>score.gpif</c> document. Pure
///     functions — no instance state.
/// </summary>
public static class GpMetadataParser
{
    public static void Populate(XDocument doc, GpImportMetadata meta)
    {
        var root = doc.Root;
        if (root == null) return;

        // Score-level song metadata. GP writes these as CDATA; XElement.Value
        // unwraps that. Trimmed because Guitar Pro preserves stray user
        // whitespace verbatim, and a whitespace-only value must read as empty
        // (the ChartData sentinel rule: blank means "not carried", and every
        // downstream blank check keys on that).
        var score = root.Element("Score");
        if (score != null)
        {
            meta.Title = score.Element("Title")?.Value.Trim() ?? "";
            meta.Artist = score.Element("Artist")?.Value.Trim() ?? "";
            meta.Album = score.Element("Album")?.Value.Trim() ?? "";
            meta.Tabber = score.Element("Tabber")?.Value.Trim() ?? "";
        }

        var masterBars = root.Element("MasterBars");
        var unfolded = masterBars != null
            ? GpMasterBarSequence.Unfold(masterBars)
            : [];

        var firstKeyCaptured = false;
        foreach (var entry in unfolded)
        {
            var mb = entry.Element;
            meta.Bars.Add(ReadBar(mb));
            if (!firstKeyCaptured)
                firstKeyCaptured = TryReadKey(mb, meta);
        }

        var automations = root.Element("MasterTrack")?.Element("Automations");
        if (automations != null)
            ReadTempoPoints(automations, unfolded, meta);
    }

    private static GpBarInfo ReadBar(XElement mb)
    {
        var bar = new GpBarInfo();
        var parts = mb.Element("Time")?.Value.Split('/');
        if (parts is { Length: 2 } && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var n) &&
            int.TryParse(parts[1], CultureInfo.InvariantCulture, out var d))
        {
            // This is the THIRD reader of <Time>, and it was the one that
            // did not clamp. GpBeatMapBuilder allocates a BeatMarker (plus
            // a LINQ pass) per iteration of `beatInBar < beatsInMeasure`,
            // so `<Time>2000000000/4</Time>` hung the import and then ran
            // out of memory — behind AppConfig.GpImportTimeSignatures,
            // which is ON by default. Its sibling GpArrangementParser reads
            // the SAME element three hundred lines away and has clamped it
            // all along; the bounds live in one place now so a fourth
            // reader cannot drift from them again.
            bar.BeatsInMeasure = Math.Clamp(n, 1, GpFormatBounds.MaxBeatsInMeasure);
            bar.NoteDenominator = Math.Clamp(d, 1, GpFormatBounds.MaxNoteDenominator);
        }

        var feel = mb.Element("TripletFeel")?.Value;
        if (!string.IsNullOrWhiteSpace(feel)) bar.TripletFeel = feel;
        return bar;
    }

    /// <summary>The song's key from the bar's <c>&lt;Key&gt;</c>; false when the bar carries none.</summary>
    private static bool TryReadKey(XElement mb, GpImportMetadata meta)
    {
        var keyNode = mb.Element("Key");
        if (keyNode == null) return false;

        if (int.TryParse(keyNode.Element("AccidentalCount")?.Value, CultureInfo.InvariantCulture, out var acc))
            meta.KeyAccidentals = acc;
        var mode = keyNode.Element("Mode")?.Value;
        meta.KeyIsMinor = string.Equals(mode, "Minor", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static void ReadTempoPoints(XElement automations, List<GpMasterBarSequence.UnfoldedBar> unfolded,
        GpImportMetadata meta)
    {
        // Build map: original bar index -> list of unfolded indices that
        // reference it, so a tempo point inside a repeated section fires on
        // every pass.
        var origToUnfolded = new Dictionary<int, List<int>>();
        for (var idx = 0; idx < unfolded.Count; idx++)
        {
            var orig = unfolded[idx].OriginalIndex;
            if (!origToUnfolded.TryGetValue(orig, out var list))
                origToUnfolded[orig] = list = [];
            list.Add(idx);
        }

        foreach (var a in automations.Elements("Automation"))
        {
            if (!TryReadTempo(a, out var origBarIdx, out var pos, out var bpm)) continue;

            if (origToUnfolded.TryGetValue(origBarIdx, out var passes))
            {
                foreach (var unfoldedIdx in passes)
                {
                    meta.TempoPoints.Add(new GpTempoPoint
                    { BarIndex = unfoldedIdx, BarPosition = pos, Bpm = bpm });
                }
            }
            else
            {
                meta.TempoPoints.Add(new GpTempoPoint
                { BarIndex = origBarIdx, BarPosition = pos, Bpm = bpm });
            }
        }
    }

    private static bool TryReadTempo(XElement a, out int origBarIdx, out double pos, out double bpm)
    {
        pos = 0.0;
        bpm = 0.0;
        if (!string.Equals(a.Element("Type")?.Value, "Tempo", StringComparison.OrdinalIgnoreCase))
        {
            origBarIdx = 0;
            return false;
        }

        if (!int.TryParse(a.Element("Bar")?.Value, CultureInfo.InvariantCulture, out origBarIdx)) return false;
        var posStr = a.Element("Position")?.Value;
        if (!string.IsNullOrWhiteSpace(posStr))
            double.TryParse(posStr, NumberStyles.Float, CultureInfo.InvariantCulture, out pos);

        var valueStr = a.Element("Value")?.Value ?? "";
        var valueParts = valueStr.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (valueParts.Length == 0) return false;
        if (!double.TryParse(valueParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out bpm))
            return false;
        // A non-positive (or non-finite) tempo makes GpBeatMapBuilder's
        // `60000.0 / bpm` Infinity, so every later BeatMarker.Time is
        // Infinity and the imported project is unusable with no error; a
        // negative one gives a backwards grid, silently breaking
        // TimelineMath.BinarySearchBeats' ascending-order assumption. Both
        // sibling paths already guard (GpLegacyImporter, SongsterrConverter).
        return double.IsFinite(bpm) && bpm > 0;
    }
}
