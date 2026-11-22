namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>Compare authored musical values, not allocated IDs or exporter output used as an oracle.</summary>
internal static class EofMusicComparison
{
    public static List<string> Issues(ChartProject project, Arrangement actual)
    {
        var issues = new List<string>();
        var expected = project.Arrangements[0];
        if (expected.Levels.Count != actual.Levels.Count) issues.Add("difficulty count");
        foreach (var level in expected.Levels)
        {
            var back = actual.Levels.SingleOrDefault(l => l.DifficultyIndex == level.DifficultyIndex);
            if (back == null) { issues.Add("missing difficulty"); continue; }
            if (level.Notes.Count != back.Notes.Count) issues.Add("single count");
            foreach (var note in level.Notes)
            {
                var target = back.Notes.SingleOrDefault(n => n.StringIndex == note.StringIndex && Near(project, note.Position, n.Position));
                if (target == null) { issues.Add("single attack"); continue; }
                Compare(project, note, target, note.Position, target.Position, issues);
                if (note.Accent != target.Accent || note.Ignore != target.Ignore || note.PassOtherNotes != target.PassOtherNotes) issues.Add("single flags");
            }
            if (level.Chords.Count != back.Chords.Count) issues.Add("chord count");
            foreach (var chord in level.Chords)
            {
                var target = back.Chords.SingleOrDefault(c => Near(project, chord.Position, c.Position));
                if (target == null) { issues.Add("chord attack"); continue; }
                var name = expected.ChordTemplates.Single(t => t.Id == chord.TemplateId);
                var targetName = actual.ChordTemplates.Single(t => t.Id == target.TemplateId);
                if (name.Name != targetName.Name) issues.Add("chord name");
                if (name.NopInRs != targetName.NopInRs) issues.Add("Nop flag");
                if (name.Fingerless != targetName.Fingerless) issues.Add("fingerless flag");
                if (!name.Fingers.OrderBy(p => p.Key).SequenceEqual(targetName.Fingers.OrderBy(p => p.Key))) issues.Add("chord fingering");
                if (chord.Accent != target.Accent || chord.Ignore != target.Ignore || chord.PassOtherNotes != target.PassOtherNotes || chord.SplitIntoNotes != target.SplitIntoNotes) issues.Add("chord flags");
                if (chord.ChordNotes.Count != target.ChordNotes.Count) issues.Add("chord tone count");
                foreach (var note in chord.ChordNotes)
                {
                    var tone = target.ChordNotes.SingleOrDefault(n => n.StringIndex == note.StringIndex);
                    if (tone == null) { issues.Add("missing chord tone"); continue; }
                    Compare(project, note, tone, chord.Position, target.Position, issues);
                }
            }
        }
        return issues.Distinct().ToList();
    }

    private static void Compare(ChartProject project, IGuitarSound source, IGuitarSound target,
        FractionalPosition sourceAt, FractionalPosition targetAt, List<string> issues)
    {
        if (source.Fret != target.Fret || Grace(source) != Grace(target)) issues.Add("fret/grace");
        if (!Near(project, sourceAt + source.Sustain, targetAt + target.Sustain)) issues.Add("end");
        if (source.SlideTo != target.SlideTo || source.UnpitchedSlide != target.UnpitchedSlide) issues.Add("slide target");
        if (source.Mute != target.Mute || source.Hopo != target.Hopo || source.Harmonic != target.Harmonic ||
            source.BassPicking != target.BassPicking || source.LinkNext != target.LinkNext || source.Tremolo != target.Tremolo ||
            source.Vibrato != target.Vibrato || source.VibratoStrength != target.VibratoStrength)
        {
            issues.Add("technique");
        }
        if (source.BendValues.Count != target.BendValues.Count)
        {
            issues.Add("bend point count");
        }
        else
        {
            for (var i = 0; i < source.BendValues.Count; i++)
            {
                if (source.BendValues[i].Step != target.BendValues[i].Step || !Near(project, source.BendValues[i].Position, target.BendValues[i].Position)) issues.Add("bend curve");
            }
        }
    }

    private static bool Near(ChartProject project, FractionalPosition a, FractionalPosition b) =>
        Math.Abs(a.GetPositionMs(project.Beats) - b.GetPositionMs(project.Beats)) <= 1;

    private static int? Grace(IGuitarSound sound) => sound is GuitarNote note ? note.GraceFret : ((ChordNote)sound).GraceFret;
}
