using DotCharter.Services.Export.Sheet;

namespace DotCharter.UI.Controls.Sheet;

/// <summary>
/// The pure lookups <see cref="SheetMusicView" /> hit-tests through: the exporter's
/// beats paired with AlphaTab's rendered bounds, measure bounds by index, and the
/// nearest tab string to a source Y. No view state, so they live apart from it.
/// </summary>
internal static class SheetHitIndex
{
    /// <summary>
    /// Pairs the exporter's per-measure emitted beats with AlphaTab's rendered
    /// beat bounds. Both are ordered per bar, and alphaTex beats map 1:1 onto
    /// model beats, so index-zipping per bar is exact; a count mismatch (never
    /// seen, but AlphaTab is the ceiling here) drops that bar back to the
    /// nearest-time fallback instead of mis-selecting.
    /// </summary>
    internal static Dictionary<int, List<(SheetMusicRenderer.BeatBound Bound, AlphaTexExporter.SheetBeat Beat)>>
        BuildBeatLookup(
            IReadOnlyList<AlphaTexExporter.SheetBeat> beatMap,
            IReadOnlyList<SheetMusicRenderer.BeatBound> bounds)
    {
        var beatsPerBar = new Dictionary<int, List<AlphaTexExporter.SheetBeat>>();
        foreach (var b in beatMap)
        {
            if (!beatsPerBar.TryGetValue(b.MeasureIndex, out var list))
                beatsPerBar[b.MeasureIndex] = list = [];
            list.Add(b);
        }

        var boundsPerBar = new Dictionary<int, List<SheetMusicRenderer.BeatBound>>();
        foreach (var b in bounds)
        {
            if (!boundsPerBar.TryGetValue(b.BarIndex, out var list))
                boundsPerBar[b.BarIndex] = list = [];
            list.Add(b);
        }

        var result = new Dictionary<int, List<(SheetMusicRenderer.BeatBound, AlphaTexExporter.SheetBeat)>>();
        foreach (var (bar, barBounds) in boundsPerBar)
        {
            if (!beatsPerBar.TryGetValue(bar, out var barBeats)) continue;
            if (barBeats.Count != barBounds.Count) continue; // fall back for this bar
            var list = new List<(SheetMusicRenderer.BeatBound, AlphaTexExporter.SheetBeat)>(barBounds.Count);
            for (var i = 0; i < barBounds.Count; i++)
                list.Add((barBounds[i], barBeats[i]));
            result[bar] = list;
        }
        return result;
    }

    internal static Dictionary<int, SheetMusicRenderer.MeasureBound> BuildMeasureBoundIndex(
        IReadOnlyList<SheetMusicRenderer.MeasureBound> bounds)
    {
        var map = new Dictionary<int, SheetMusicRenderer.MeasureBound>(bounds.Count);
        foreach (var b in bounds) map[b.Index] = b;
        return map;
    }

    internal static int NearestStringOnLines(IReadOnlyList<SheetMusicRenderer.StringLine> lattice, double srcY)
    {
        if (lattice.Count == 0) return -1;
        if (lattice.Count == 1) return lattice[0].StringIndex;

        // Average spacing gives the outside tolerance.
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var l in lattice)
        {
            if (l.Y < minY) minY = l.Y;
            if (l.Y > maxY) maxY = l.Y;
        }
        var spacing = (maxY - minY) / Math.Max(1, lattice.Count - 1);
        if (spacing <= 0) spacing = 10;
        if (srcY < minY - spacing * 2 || srcY > maxY + spacing * 2) return -1;

        var best = -1;
        var bestDist = double.MaxValue;
        foreach (var l in lattice)
        {
            var d = Math.Abs(l.Y - srcY);
            if (d < bestDist) { bestDist = d; best = l.StringIndex; }
        }

        return best;
    }
}
