using AT = AlphaTab.Model;

namespace DotCharter.Services.Export.GuitarPro;

/// <summary>A bend segment belongs to the written tied beat, with continuous boundary values.</summary>
internal static class GpBendCurve
{
    public static void AddTo(IGuitarSound sound, FractionalPosition onset, FractionalPosition start,
        FractionalPosition end, AT.Note note)
    {
        if (sound.BendValues.Count == 0 || end <= start) return;

        var points = sound.BendValues.OrderBy(b => b.Position)
            .Select(b => (Offset: (b.Position - onset).DoubleValue, b.Step)).ToList();
        if (points[0].Offset > 0) points.Insert(0, (0, 0));
        var from = (start - onset).DoubleValue;
        var to = (end - onset).DoubleValue;
        note.BendType = AT.BendType.Custom;
        // Domain strengths are semitones; alphaTab uses quarter tones.
        note.AddBendPoint(new AT.BendPoint(0, ValueAt(from) * 2));
        foreach (var point in points)
        {
            if (point.Offset <= from || point.Offset >= to) continue;
            note.AddBendPoint(new AT.BendPoint((point.Offset - from) / (to - from) * 60, point.Step * 2));
        }
        note.AddBendPoint(new AT.BendPoint(60, ValueAt(to) * 2));
        // Finish infers a simple release from nonzero endpoints and drops an interior
        // peak on a tied continuation. Keep the authored bend/release explicit.
        if (note.MaxBendPoint!.Value > Math.Max(note.BendPoints![0].Value, note.BendPoints[^1].Value))
            note.BendType = AT.BendType.BendRelease;

        double ValueAt(double offset)
        {
            var previous = points[0];
            foreach (var point in points)
            {
                if (point.Offset > offset)
                {
                    if (point.Offset == previous.Offset) return previous.Step;
                    var ratio = Math.Clamp((offset - previous.Offset) / (point.Offset - previous.Offset), 0, 1);
                    return previous.Step + (point.Step - previous.Step) * ratio;
                }
                previous = point;
            }
            return previous.Step;
        }
    }
}
