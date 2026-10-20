using System.Xml.Linq;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     <see cref="ChordTemplate.Fingerless" /> — EOF's Fingerless status
///     (<c>EOF_PRO_GUITAR_NOTE_EFLAG_FINGERLESS</c>, <c>song.h:137</c>, #609): the grip
///     intentionally has no fingering. EOF calls such a chord's fingering VALID
///     (<c>eof_pro_guitar_note_fingering_valid</c>, <c>rs.c:3593-3596</c>), so L06/L07/L08
///     do not judge it; Generate fingering does not fill it; the <c>.rscp</c> keeps it;
///     and a project saved before the field existed loads with every template judged,
///     as it always was.
/// </summary>
public sealed class FingerlessTemplateTests : IDisposable
{
    private static readonly (int, int)[] G5 = [(0, 3), (1, 5), (2, 5)];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-fingerless-" + Guid.NewGuid().ToString("N"));

    public FingerlessTemplateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static List<ArrangementIssue> Run(Arrangement arrangement)
    {
        var issues = new List<ArrangementIssue>();
        new TemplateAndOverlapRule().Validate(ArrangementValidationContext.Build(arrangement, Beats(), null), issues);
        return issues;
    }

    /// <summary>An unfingered G5 the chord plays — and, when asked, a finger on its
    /// OPEN string too, which L06 reports.</summary>
    private static (Arrangement Arrangement, ChordTemplate Template) G5Chord(bool fingerless, bool fingerOnOpen = false)
    {
        var arrangement = ChordHandShapePlannerTests.Arr();
        var chord = ChordHandShapePlannerTests.Chord(1, 0, G5);
        var template = new ChordTemplate { Id = 0, Name = "G5", Fingerless = fingerless };
        foreach (var cn in chord.ChordNotes) template.Frets[cn.StringIndex] = cn.Fret;
        if (fingerOnOpen) template.Frets[3] = 0;
        if (fingerOnOpen) template.Fingers[3] = 2;
        arrangement.ChordTemplates.Add(template);
        chord.TemplateId = 0;
        arrangement.Levels[0].Chords.Add(chord);
        return (arrangement, template);
    }

    private static readonly ArrangementIssueType[] FingeringFindings =
    [
        ArrangementIssueType.FingerSetForOpenString,
        ArrangementIssueType.FingerMissingOnFrettedString,
        ArrangementIssueType.FirstFingerNotOnLowestFret
    ];

    [Fact]
    public void AnUnfingeredTemplate_RaisesL07_AFingerlessOneRaisesNoFingeringFinding()
    {
        var (ordinary, _) = G5Chord(fingerless: false, fingerOnOpen: true);
        var (fingerless, _) = G5Chord(fingerless: true, fingerOnOpen: true);

        var onOrdinary = Run(ordinary);
        Assert.Equal(3, onOrdinary.Count(i => i.Type == ArrangementIssueType.FingerMissingOnFrettedString));
        Assert.Single(onOrdinary, i => i.Type == ArrangementIssueType.FingerSetForOpenString);
        Assert.DoesNotContain(Run(fingerless), i => FingeringFindings.Contains(i.Type));
    }

    [Fact]
    public void GenerateFingering_LeavesAFingerlessTemplateAlone()
    {
        var (ordinary, ordinaryTemplate) = G5Chord(fingerless: false);
        var (fingerless, _) = G5Chord(fingerless: true);
        var service = new FingeringGenerationService();

        Assert.Same(ordinaryTemplate, Assert.Single(service.FindTemplatesNeedingFingering(ordinary)));
        Assert.Empty(service.FindTemplatesNeedingFingering(fingerless));

        // Nor is it a target when a fingered grip on the same frets is copied around.
        var source = ordinaryTemplate.Clone();
        source.Id = 1;
        source.Fingers[0] = 1;
        source.Fingers[1] = 3;
        source.Fingers[2] = 4;
        fingerless.ChordTemplates.Add(source);
        Assert.Empty(service.FindMatchingTemplatesNeedingFingering(fingerless, source));
    }

    [Fact]
    public void TheFlag_IsPartOfTheGrip_AndSurvivesAClone()
    {
        var (_, template) = G5Chord(fingerless: true);
        var clone = template.Clone();
        Assert.True(clone.Fingerless);
        Assert.Equal(template, clone);

        clone.Fingerless = false;
        Assert.NotEqual(template, clone);
    }

    [Fact]
    public void Rscp_WritesTheFlagOnlyWhenSet_AndAnOlderProjectReadsAsNotFingerless()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        var (arrangement, flagged) = G5Chord(fingerless: true);
        arrangement.Name = "Lead";
        var plain = flagged.Clone();
        plain.Id = 1;
        plain.Fingerless = false;
        arrangement.ChordTemplates.Add(plain);
        project.Arrangements.Add(arrangement);

        var path = Path.Combine(_dir, "p.rscp");
        new RscpChartSerializer().Save(path, project);
        var written = XDocument.Load(path).Descendants("chordTemplate").ToList();
        Assert.Equal("T", written[0].Attribute("fingerless")?.Value);
        Assert.Null(written[1].Attribute("fingerless"));

        var reloaded = new RscpChartSerializer().Load(path).Arrangements[0].ChordTemplates;
        Assert.True(reloaded.Single(t => t.Id == 0).Fingerless);
        Assert.False(reloaded.Single(t => t.Id == 1).Fingerless);

        // A project saved before #609 has no attribute at all.
        var doc = XDocument.Load(path);
        doc.Descendants("chordTemplate").Attributes("fingerless").Remove();
        doc.Save(path);
        Assert.All(new RscpChartSerializer().Load(path).Arrangements[0].ChordTemplates, t => Assert.False(t.Fingerless));
    }
}
