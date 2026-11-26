using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     The Issues tab's "I checked this, it is intended": which findings may be ignored, the
///     <see cref="IgnoredFinding" /> a finding is stored as, and the split of a pass into shown and ignored.
///     <list type="bullet">
///         <item>Only a <see cref="ArrangementIssueSeverity.Warning" /> is ignorable. An Important finding (the
///         errors) always shows, even when the project file lists it: ignoring never waives an error, and export
///         validation and Fix chart errors never read the list.</item>
///         <item>A finding matches by rule code, its English sentence and a fingerprint of its SUBJECT as it
///         stands now — the element's own fields, or a lyric's syllable and its whole line. An edit to the
///         subject changes the fingerprint, so the finding comes back.</item>
///     </list>
///     Pure and UI-free: <c>FindingIgnoresTests</c>.
/// </summary>
public static class FindingIgnores
{
    /// <summary>A warning can be ignored; an error (Important) never.</summary>
    public static bool IsIgnorable(ArrangementIssue issue) => issue.Severity == ArrangementIssueSeverity.Warning;

    /// <summary>The entry <paramref name="issue" /> is stored as, read against the live <paramref name="project" />.</summary>
    public static IgnoredFinding EntryFor(ArrangementIssue issue, ChartProject project) =>
        new(issue.Code, issue.Message, Fingerprint(Subject(issue, project)));

    /// <summary>Two findings are "alike" (one group, one "Ignore all like this") when rule and sentence match.</summary>
    public static (string Code, string Message) LikeKey(ArrangementIssue issue) => (issue.Code, issue.Message);

    /// <summary>
    ///     Splits <paramref name="issues" /> (order kept) into the ones to show and the ignored ones. With nothing
    ///     ignored in the project this costs one count check.
    /// </summary>
    public static (List<ArrangementIssue> Shown, List<ArrangementIssue> Ignored) Partition(
        IReadOnlyList<ArrangementIssue> issues, ChartProject? project)
    {
        if (project == null || project.IgnoredFindings.Count == 0) return ([.. issues], []);
        var set = new HashSet<IgnoredFinding>(project.IgnoredFindings);
        List<ArrangementIssue> shown = [];
        List<ArrangementIssue> ignored = [];
        foreach (var issue in issues)
        {
            if (IsIgnorable(issue) && set.Contains(EntryFor(issue, project))) ignored.Add(issue);
            else shown.Add(issue);
        }

        return (shown, ignored);
    }

    /// <summary>The subject's description, before hashing: what an edit has to change to bring a finding back.</summary>
    internal static string Subject(ArrangementIssue issue, ChartProject project)
    {
        var text = new StringBuilder();
        if (issue.Note is { } note)
        {
            text.Append("N|").Append(note.Position).Append('|');
            Sound(text, note);
            text.Append('|').Append(Invariant(note.LeftHandFinger)).Append('|').Append(note.Ignore);
        }
        else if (issue.Chord is { } chord)
        {
            text.Append("C|").Append(chord.Position).Append('|').Append(Invariant(chord.TemplateId))
                .Append('|').Append(chord.Strum).Append('|').Append(chord.Ignore);
            foreach (var chordNote in chord.ChordNotes)
            {
                text.Append("|[");
                Sound(text, chordNote);
                text.Append(']');
            }
        }
        else if (issue.Anchor is { } anchor)
        {
            text.Append("A|").Append(anchor.Position).Append('|').Append(Invariant(anchor.Fret)).Append('|').Append(Invariant(anchor.Width));
        }
        else if (issue.HandShape is { } shape)
        {
            Shape(text.Append("H|"), shape);
        }
        else if (issue.HandShapes is { Count: > 0 } shapes)
        {
            text.Append("S");
            foreach (var each in shapes) Shape(text.Append('|'), each);
        }
        else if (issue.Lyric is { } place)
        {
            Lyric(text, place, project);
        }
        else
        {
            // A whole-arrangement finding: its sentence and its time are all there is.
            text.Append("T|").Append(issue.TimeMs?.ToString("R", CultureInfo.InvariantCulture) ?? "-");
        }

        if (issue.StringIndex is { } stringIndex) text.Append("|s").Append(Invariant(stringIndex));
        return text.ToString();
    }

    private static void Lyric(StringBuilder text, LyricPlace place, ChartProject project)
    {
        if (place.Find(project) is not var (track, lyric))
        {
            text.Append("L|-");
            return;
        }

        // The syllable and its WHOLE line: an edit anywhere in the line brings the finding back.
        text.Append("L|").Append(lyric.Position).Append('|').Append(lyric.EndPosition).Append('|')
            .Append(lyric.Text).Append('|').Append(lyric.IsWordPart);
        foreach (var each in track.Phrases[place.Phrase].Lyrics)
            text.Append('|').Append(each.Position).Append(':').Append(each.Text).Append(each.IsWordPart ? "-" : "");
    }

    private static void Shape(StringBuilder text, HandShape shape) =>
        text.Append(shape.Position).Append('~').Append(shape.EndPosition).Append(':').Append(Invariant(shape.TemplateId))
            .Append(':').Append(shape.Name).Append(':').Append(Invariant(shape.GhostMask));

    private static void Sound(StringBuilder text, IGuitarSound sound)
    {
        text.Append(Invariant(sound.StringIndex)).Append(',').Append(Invariant(sound.Fret)).Append(',').Append(sound.Sustain)
            .Append(',').Append(sound.Mute).Append(',').Append(sound.Hopo).Append(',').Append(sound.Harmonic)
            .Append(',').Append(sound.BassPicking).Append(',').Append(sound.Accent).Append(',').Append(sound.Vibrato)
            .Append(',').Append(Invariant(sound.VibratoStrength)).Append(',').Append(sound.Tremolo).Append(',').Append(sound.LinkNext)
            .Append(',').Append(Invariant(sound.SlideTo)).Append(',').Append(sound.UnpitchedSlide);
        foreach (var bend in sound.BendValues)
            text.Append(",b").Append(bend.Position).Append('/').Append(bend.Step.ToString("R", CultureInfo.InvariantCulture));
    }

    private static string Invariant(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    /// <summary>A short, stable hash of the subject: the project file stores this, not the subject itself.</summary>
    private static string Fingerprint(string subject) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)), 0, 8);
}
