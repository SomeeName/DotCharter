using System.Xml.Linq;

namespace DotCharter.Services.Parsing.GuitarPro;

// GPIF supplies these names. GP5 supplies signed, one-based measures in this order.
internal enum GpDirection
{
    Coda, DoubleCoda, Segno, SegnoSegno, Fine,
    DaCapo, DaCapoAlCoda, DaCapoAlDoubleCoda, DaCapoAlFine,
    DaSegno, DaSegnoAlCoda, DaSegnoAlDoubleCoda, DaSegnoAlFine,
    DaSegnoSegno, DaSegnoSegnoAlCoda, DaSegnoSegnoAlDoubleCoda, DaSegnoSegnoAlFine,
    DaCoda, DaDoubleCoda
}

internal readonly record struct GpDirectionMark(GpDirection Direction, int WrittenBar);

internal static class GpDirections
{
    public static List<GpDirectionMark> ReadLegacy(GpLegacyBinaryReader reader)
    {
        var result = new List<GpDirectionMark>();
        foreach (var direction in Enum.GetValues<GpDirection>())
        {
            var bar = reader.ReadInt16();
            if (bar > 0) result.Add(new GpDirectionMark(direction, bar - 1));
        }

        return result;
    }

    public static IEnumerable<GpDirectionMark> ReadGpif(XElement bar, int writtenBar)
    {
        foreach (var element in bar.Element("Directions")?.Elements() ?? [])
        {
            var name = element.Value.Trim();
            if (name.StartsWith("Target", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            if (name.StartsWith("Jump", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            if (Enum.TryParse<GpDirection>(name, true, out var direction) && Enum.IsDefined(direction))
                yield return new GpDirectionMark(direction, writtenBar);
        }
    }

    public static List<GpMasterBarSequence.UnfoldedIndex> Apply(List<GpMasterBarSequence.UnfoldedIndex> playback,
        bool[] skippedOnReturn, IReadOnlyList<GpDirectionMark> marks)
    {
        var valid = marks.Where(m => m.WrittenBar >= 0 && m.WrittenBar < skippedOnReturn.Length).ToList();
        var byDirection = valid.ToLookup(m => m.Direction, m => m.WrittenBar);
        var lastPlays = playback.Select((bar, at) => (bar, at)).ToLookup(p => p.bar.OriginalIndex, p => p.at);
        var chosenAt = playback.Count;
        (int Start, int Stop, int Coda)? chosen = null;
        foreach (var mark in valid)
        {
            var plays = lastPlays[mark.WrittenBar];
            if (!plays.Any() || plays.Last() >= chosenAt) continue;
            if (Route(mark, byDirection, skippedOnReturn) is not { } route) continue;
            chosenAt = plays.Last();
            chosen = route;
        }

        if (chosen is not { } trip) return playback;
        var result = playback.GetRange(0, chosenAt + 1);
        result[^1] = result[^1] with { EndsBlock = true };
        AddReplay(result, trip.Start, trip.Stop, skippedOnReturn);
        if (trip.Coda >= 0)
        {
            var codaAt = lastPlays[trip.Coda].FirstOrDefault(-1);
            if (codaAt >= 0)
            {
                result.Add(playback[codaAt] with { StartsBlock = true });
                result.AddRange(playback.Skip(codaAt + 1));
            }
            else
            {
                AddReplay(result, trip.Coda, skippedOnReturn.Length - 1, skippedOnReturn);
            }
        }

        return result;
    }

    private static void AddReplay(List<GpMasterBarSequence.UnfoldedIndex> result, int first, int last, bool[] skipped)
    {
        var start = result.Count;
        for (var bar = first; bar <= last; bar++)
        {
            if (!skipped[bar]) result.Add(new GpMasterBarSequence.UnfoldedIndex(bar, 2, StartsBlock: result.Count == start));
        }

        if (result.Count > start) result[^1] = result[^1] with { EndsBlock = true };
    }

    private static (int Start, int Stop, int Coda)? Route(GpDirectionMark mark, ILookup<GpDirection, int> targets, bool[] skipped)
    {
        var direction = (int)mark.Direction - (int)GpDirection.DaCapo;
        if (direction is < 0 or >= 12) return null;
        var start = (direction / 4) switch
        {
            0 => 0,
            1 => targets[GpDirection.Segno].FirstOrDefault(-1),
            _ => targets[GpDirection.SegnoSegno].FirstOrDefault(-1)
        };
        if (start < 0 || start > mark.WrittenBar) return null;

        return (direction % 4) switch
        {
            1 => ViaCoda(targets, start, mark.WrittenBar, GpDirection.DaCoda, GpDirection.Coda, skipped),
            2 => ViaCoda(targets, start, mark.WrittenBar, GpDirection.DaDoubleCoda, GpDirection.DoubleCoda, skipped),
            3 => (start, targets[GpDirection.Fine].FirstOrDefault(b => b >= start && !skipped[b], skipped.Length - 1), -1),
            _ => (start, skipped.Length - 1, -1)
        };
    }

    private static (int Start, int Stop, int Coda)? ViaCoda(ILookup<GpDirection, int> targets,
        int start, int jump, GpDirection exit, GpDirection landing, bool[] skipped)
    {
        var stop = targets[exit].FirstOrDefault(b => b >= start && b <= jump && !skipped[b], -1);
        var coda = targets[landing].FirstOrDefault(b => b > stop, -1);
        return stop >= 0 && coda >= 0 ? (start, stop, coda) : null;
    }
}
