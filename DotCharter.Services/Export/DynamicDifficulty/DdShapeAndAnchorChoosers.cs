namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/HandShapeChooser.fs; MIT,
///     Copyright (c) 2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): the hand
///     shapes a level keeps, and the narrower template each one needs.
/// </summary>
internal static class DdHandShapeChooser
{
    public static List<(DdHandShape Shape, DdTemplateRequest? Request)> Choose(double diffPercent,
        IReadOnlyList<DdEntity> levelEntities, IReadOnlyList<DdEntity> allEntities, int maxChordNotes,
        List<DdChordTemplate> templates, IReadOnlyList<DdHandShape> handShapes)
    {
        var allowedNotes = DdTemplates.AllowedChordNotes(diffPercent, maxChordNotes);
        var result = new List<(DdHandShape, DdTemplateRequest?)>();
        foreach (var hs in handShapes)
        {
            var template = templates[hs.ChordId];
            var noteCount = DdTemplates.NoteCount(template);
            if (NoNotesInHandShape(levelEntities, hs)) continue;
            if (IsArpeggio(allEntities, hs))
            {
                // Always use the full handshape for arpeggios
                result.Add((hs, null));
            }
            else if (allowedNotes <= 1)
            {
                continue;
            }
            else if (allowedNotes >= noteCount)
            {
                result.Add((hs, null));
            }
            else
            {
                var copy = new DdHandShape(hs);
                result.Add((copy, DdTemplates.Request(hs.ChordId, allowedNotes, noteCount, template, null, copy)));
            }
        }

        return result;
    }

    private static bool IsInside(DdHandShape hs, int time) => time >= hs.StartTime && time < hs.EndTime;

    private static bool NoNotesInHandShape(IReadOnlyList<DdEntity> entities, DdHandShape hs) =>
        !entities.Any(x => IsInside(hs, x.Time) || IsInside(hs, x.Time + x.Sustain));

    private static bool IsArpeggio(IReadOnlyList<DdEntity> entities, DdHandShape hs)
    {
        var notes = entities.Where(e => e.Note != null && IsInside(hs, e.Note.Time)).Select(e => e.Note!).ToList();
        if (notes.Count == 0) return false;
        return !notes.All(n => n.String == notes[0].String || n.Time == notes[0].Time);
    }
}

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/AnchorChooser.fs; MIT, Copyright
///     (c) 2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): the anchors a level needs.
/// </summary>
internal static class DdAnchorChooser
{
    // Assume that notes up to 3ms before the anchor were meant to be on the anchor
    private const int ErrorMargin = 3;

    public static List<DdAnchor> Choose(IReadOnlyList<DdEntity> entities, IReadOnlyList<DdAnchor> anchors,
        int phraseStartTime, int phraseEndTime)
    {
        var result = new List<DdAnchor>();
        for (var i = 0; i < anchors.Count; i++)
        {
            var a = anchors[i];
            var endTime = i + 1 < anchors.Count ? anchors[i + 1].Time : phraseEndTime;
            if (a.Time == phraseStartTime || ShouldInclude(entities, a.Time, endTime)) result.Add(a);
        }

        return result;
    }

    private static bool ShouldInclude(IReadOnlyList<DdEntity> entities, int startTime, int endTime) =>
        entities.Any(e =>
        {
            var time = e.Time;
            var sustain = e.Sustain;
            return (time + ErrorMargin >= startTime && time < endTime)
                   || (time + sustain + ErrorMargin >= startTime && time + sustain < endTime);
        });
}
