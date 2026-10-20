using System.Text.RegularExpressions;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     <b>Two flags spell ARPEGGIO and each import path sets only one of them.</b>
///     <c>RsXmlSongReader.ReadChordTemplates</c> sets
///     <see cref="ChordTemplate.ForceArpeggioInRs" /> (from a <c>-arp</c>
///     displayName) and never <see cref="ChordTemplate.Arpeggio" />;
///     the package importer's chord-template conversion sets
///     <see cref="ChordTemplate.Arpeggio" /> (from chord mask 0x1) and never the
///     other. Every editing path that read ONE flag therefore worked on psarc
///     imports and was silently wrong on every DLC-Builder / EOF chart.
///     <para>
///         The two answers are <see cref="ChordTemplate.IsArpeggio" /> ("is this
///         an arpeggio?", either flag) and <see cref="ArpeggioFlavour.Matches" />
///         ("is this the same flavour?", both flags exactly — the non-fret half
///         of every find-or-create key).
///     </para>
/// </summary>
public sealed class ArpeggioFlavourTests
{
    // ── the primitive ────────────────────────────────────────────────────────

    [Fact]
    public void EitherSpelling_IsAnArpeggio_AndTheyAreNotTheSameFlavour()
    {
        var sng = new ChordTemplate { Arpeggio = true };
        var rsXml = new ChordTemplate { ForceArpeggioInRs = true };
        var plain = new ChordTemplate();

        Assert.True(sng.IsArpeggio);
        Assert.True(rsXml.IsArpeggio);
        Assert.False(plain.IsArpeggio);

        // Same KIND, different FLAVOUR: they export under different names, so
        // find-or-create must never collapse one onto the other.
        Assert.False(ArpeggioFlavour.Of(sng).Matches(rsXml));
        Assert.False(ArpeggioFlavour.Of(rsXml).Matches(sng));
        Assert.True(ArpeggioFlavour.Of(rsXml).Matches(new ChordTemplate { ForceArpeggioInRs = true }));

        // A dangling / -1 TemplateId is not an arpeggio.
        Assert.Equal(ArpeggioFlavour.Plain, ArpeggioFlavour.Of(null));
        Assert.False(ArpeggioFlavour.Plain.IsArpeggio);
    }

    [Fact]
    public void ApplyTo_StampsBothFlags_SoAMintCannotCarryHalfAFlavour()
    {
        var template = new ChordTemplate { Arpeggio = true, ForceArpeggioInRs = true };
        ArpeggioFlavour.Plain.ApplyTo(template);
        Assert.False(template.Arpeggio);
        Assert.False(template.ForceArpeggioInRs);

        new ArpeggioFlavour(false, true).ApplyTo(template);
        Assert.False(template.Arpeggio);
        Assert.True(template.ForceArpeggioInRs);
    }

    [Fact]
    public void TheDisplayName_SaysArpeggio_ForEitherSpelling()
    {
        Assert.Equal("A5(arp)", new ChordTemplate { Name = "A5", ForceArpeggioInRs = true }.GetDisplayName());
        Assert.Equal("A5(arp)", new ChordTemplate { Name = "A5", Arpeggio = true }.GetDisplayName());
        Assert.Equal("A5", new ChordTemplate { Name = "A5" }.GetDisplayName());
    }

    // ── ChordTemplateResolver: the find-or-create key ────────────────────────

    private static Arrangement WithTemplate(ChordTemplate template)
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        arrangement.ChordTemplates.Add(template);
        return arrangement;
    }

    private static ChordTemplate RsXmlArpeggio(int id = 0)
    {
        var template = new ChordTemplate { Id = id, Name = "A5", ForceArpeggioInRs = true };
        template.Frets[0] = 5;
        template.Frets[1] = 7;
        return template;
    }

    [Fact]
    public void ReshapingAnRsXmlArpeggioChord_KeepsItAnArpeggio()
    {
        var arrangement = WithTemplate(RsXmlArpeggio());
        var chord = new Chord { TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 9 }); // moved
        var created = new List<ChordTemplate>();

        var resolved = ChordTemplateResolver.ResolveForChord(arrangement, chord, created);

        // Before the fix this minted a template with BOTH flags false, so the
        // chord silently stopped exporting as `-arp` / ChordMask.Arpeggio.
        Assert.Single(created);
        Assert.True(resolved.IsArpeggio);
        Assert.True(resolved.ForceArpeggioInRs);
        Assert.False(resolved.Arpeggio);
    }

    [Fact]
    public void APlainChord_NeverResolvesOntoAnRsXmlArpeggioTemplate()
    {
        var arpeggio = RsXmlArpeggio();
        var arrangement = WithTemplate(arpeggio);
        var created = new List<ChordTemplate>();

        var resolved = ChordTemplateResolver.Resolve(
            arrangement, new Dictionary<int, int> { [0] = 5, [1] = 7 },
            ArpeggioFlavour.Plain, preferredName: null, created);

        // The other direction of the same bug: a plain chord reshaped onto those
        // frets adopted the arpeggio template and started exporting as one.
        Assert.NotSame(arpeggio, resolved);
        Assert.Single(created);
        Assert.False(resolved.IsArpeggio);
    }

    [Fact]
    public void TheSameFlavour_StillReusesTheExistingTemplate()
    {
        var arpeggio = RsXmlArpeggio();
        var arrangement = WithTemplate(arpeggio);
        var created = new List<ChordTemplate>();

        var resolved = ChordTemplateResolver.Resolve(
            arrangement, new Dictionary<int, int> { [0] = 5, [1] = 7 },
            ArpeggioFlavour.Of(arpeggio), preferredName: null, created);

        // Vacuity keeper: the guard above must not degrade into "always mint".
        Assert.Same(arpeggio, resolved);
        Assert.Empty(created);
    }

    // ── ChartSpacingEnforcer: runs on EVERY save ─────────────────────────────

    [Fact]
    public void APartiallyMutedArpeggioChord_IsNotRepointedAtAPlainReducedTemplate()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };

        var arpeggio = new ChordTemplate { Id = 0, Name = "Shape", ForceArpeggioInRs = true };
        arpeggio.Frets[0] = 3;
        arpeggio.Frets[1] = 5;
        arpeggio.Frets[2] = 5;
        arpeggio.Fingers[0] = 1;
        arpeggio.Fingers[1] = 3;
        arpeggio.Fingers[2] = 4;
        arrangement.ChordTemplates.Add(arpeggio);

        // A PLAIN template that already carries exactly the reduced shape.
        var plainReduced = new ChordTemplate { Id = 1, Name = "Shape" };
        plainReduced.Frets[0] = 3;
        plainReduced.Frets[1] = 5;
        plainReduced.Fingers[0] = 1;
        plainReduced.Fingers[1] = 3;
        arrangement.ChordTemplates.Add(plainReduced);

        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5, Mute = MuteType.Full });
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arrangement, BuildBeats(12), SpacingConfig());

        // The pass runs on every save, so a one-flag comparison silently
        // downgraded this arpeggio the first time the file was written.
        Assert.NotEqual(plainReduced.Id, chord.TemplateId);
        var resolved = arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.True(resolved.ForceArpeggioInRs);
        Assert.Equal(2, resolved.Frets.Count);
    }

    private static AppConfig SpacingConfig()
    {
        return new AppConfig
        {
            MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction,
            MinNoteDistanceDivisor = 64
        };
    }

    private static List<BeatMarker> BuildBeats(int count)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        return beats;
    }

    // ── the "same kind of shape" predicates ──────────────────────────────────

    private static ChordTemplate Shape(int baseFret, bool arpeggio = false, bool forceArpeggio = false)
    {
        var template = new ChordTemplate { Arpeggio = arpeggio, ForceArpeggioInRs = forceArpeggio };
        template.Frets[0] = baseFret;
        template.Frets[1] = baseFret + 2;
        return template;
    }

    [Fact]
    public void AnRsXmlArpeggio_IsNotATranspositionOfThePlainShape()
    {
        Assert.False(ChordShapeTransposition.IsTransposition(
            Shape(5, forceArpeggio: true), Shape(7), capo: 0, out _));

        // Vacuity keeper: the same-kind pair still transposes.
        Assert.True(ChordShapeTransposition.IsTransposition(
            Shape(5, forceArpeggio: true), Shape(7, forceArpeggio: true), capo: 0, out var semitones));
        Assert.Equal(2, semitones);
    }

    [Fact]
    public void AnRsXmlArpeggio_IsNotASupersetOfThePlainShape()
    {
        var seed = Shape(5, forceArpeggio: true);
        var bigger = Shape(5);
        bigger.Frets[2] = 7;
        Assert.False(ChordShapeSuperset.Contains(seed, bigger));

        var biggerArpeggio = Shape(5, forceArpeggio: true);
        biggerArpeggio.Frets[2] = 7;
        Assert.True(ChordShapeSuperset.Contains(seed, biggerArpeggio));
    }

    // ── the fingering inspector's resolve keys ───────────────────────────────

    [Fact]
    public void TheFingeringServiceTreatsBothSpellingsAsAnArpeggio()
    {
        var service = new FingeringGenerationService();
        var arrangement = WithTemplate(RsXmlArpeggio());
        var probe = new ChordTemplate { Name = "A5" };
        probe.Frets[0] = 5;
        probe.Frets[1] = 7;

        // "An equivalent PLAIN template" must not find the arpeggio one …
        Assert.Null(service.FindEquivalentTemplate(arrangement, probe));
        // … and "identical" cannot ignore a flag that changes the export name.
        Assert.Null(service.FindIdenticalTemplate(arrangement, probe));

        probe.ForceArpeggioInRs = true;
        Assert.Same(arrangement.ChordTemplates[0], service.FindIdenticalTemplate(arrangement, probe));
    }

    // ── the rule, made mechanical ────────────────────────────────────────────

    /// <summary>
    ///     Every one of the sites above read a single flag, and each was found by
    ///     hand. This is the same rule as a scan: nothing under
    ///     <c>Services/Editing</c> may ASK about <c>Arpeggio</c> or
    ///     <c>ForceArpeggioInRs</c> on its own — the answers are
    ///     <see cref="ChordTemplate.IsArpeggio" /> and <see cref="ArpeggioFlavour" />.
    ///     Assignments are exempt: <c>ChartSpacingEnforcer.StripRsDisplaySuffixes</c>
    ///     deliberately WRITES the RS-specific flag when it strips a <c>-arp</c>
    ///     suffix, mirroring <c>RsXmlSongReader</c>.
    /// </summary>
    [Fact]
    public void NoEditingCode_AsksAboutOneArpeggioFlag()
    {
        var offenders = new List<string>();
        var files = 0;
        var sanctionedAnswers = 0;

        foreach (var file in EditingSourceFiles())
        {
            files++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i]);
                if (Regex.IsMatch(code, @"\b(IsArpeggio|ArpeggioFlavour)\b")) sanctionedAnswers++;

                foreach (Match match in Regex.Matches(code, @"\.(Arpeggio|ForceArpeggioInRs)\b"))
                {
                    // A WRITE is a different act from a question.
                    var after = code[(match.Index + match.Length)..].TrimStart();
                    if (after.StartsWith('=') && !after.StartsWith("==", StringComparison.Ordinal)) continue;
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        // Vacuity keepers: the scan must actually be looking at live code that
        // answers the question the sanctioned way.
        Assert.True(files >= 40, $"only {files} source files scanned — the folder moved.");
        Assert.True(sanctionedAnswers >= 8,
            $"only {sanctionedAnswers} uses of IsArpeggio/ArpeggioFlavour found — they were renamed away.");

        Assert.True(offenders.Count == 0,
            "Editing code reads ONE arpeggio flag. Two flags spell arpeggio and each import path sets " +
            "only one, so a single-flag read works on psarc imports and is silently dead on every " +
            "DLC-Builder / EOF chart. Ask ChordTemplate.IsArpeggio (\"is this an arpeggio?\") or " +
            "ArpeggioFlavour.Matches (\"is this the same flavour?\", the find-or-create key):\n  " +
            string.Join("\n  ", offenders));
    }

    private static IEnumerable<string> EditingSourceFiles()
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Editing");
        Assert.True(Directory.Exists(dir), $"missing source directory {dir}");
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories);
    }

    private static string StripComment(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }
}
