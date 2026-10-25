using System.Security.Cryptography;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Images;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Byte pins over REAL artifacts for the four hot paths optimised in v0.49:
///     <c>RsChordDensity</c>'s compact-repeat decision (SNG), <c>DdsEncoder</c>'s
///     album art, <c>RscpHeaderParser</c>'s header lookups (.rscp load) and
///     <c>GpNoteParser</c>'s per-note property reads (Guitar Pro import).
/// </summary>
/// <remarks>
///     <para>
///         <b>Every hash below was captured from the code as it stood BEFORE those
///         changes</b> (commit <c>9cc50f87</c>), by running each path against the
///         artifact named in the row and hashing the result. They are not
///         "whatever it does now" snapshots: they are the previous
///         implementation's output, and the whole point of the optimisations was
///         that the bytes must not move. A failure here means an export changed
///         what it emits — investigate the change, do not re-bless the hash.
///     </para>
///     <para>
///         <b>Two things in this pipeline are deliberately NOT deterministic, and
///         both had to be excluded to make a byte pin possible at all.</b>
///         <c>RocksmithSngWriter.WritePackedPc</c> generates a random 16-byte
///         AES-CTR IV per call, so the PACKED SNG differs on every single run —
///         the pin is over <c>WriteUnpacked</c>, the body the IV encrypts. And
///         <c>SngFromDomainMapper.BuildInstrumental</c> stamps
///         <c>LastConversionDateTime</c> from <c>DateTime.Now</c> at
///         minute resolution unless a time is passed in, so the pin passes a
///         fixed one. (A whole <c>.psarc</c> is a third: the manifest carries a
///         fresh <c>Guid</c> and a random <c>MasterId</c> per build.) Anyone
///         adding a byte pin over an export will hit all three.
///     </para>
///     <para>
///         <b>And a fourth trap, which cost three of these pins a re-capture:
///         <c>RscpChartSerializer.Save</c> MUTATES the project it is handed.</b>
///         <c>RscpWriter</c> runs <c>RscpTemplateManager.RegenerateTemplates</c>
///         per arrangement, which renumbers every <c>ChordTemplate.Id</c> and
///         repoints chords at them — so "load, save, then build the SNG"
///         produces DIFFERENT SNG bytes from "load, then build the SNG", the bass
///         arrangements first. The real save path never notices because
///         <c>ProjectSessionService</c> hands it a <c>ChartProjectCloner</c>
///         snapshot. Each test here loads its OWN project and does ONE thing with
///         it, for exactly that reason.
///     </para>
///     <para>
///         <c>TestData/</c> is git-ignored, so these SKIP rather than fail in an
///         agent worktree or on CI. The equivalence they check at a level that
///         needs no fixture is <c>RsChordDensityLookupEquivalenceTests</c>, which
///         runs everywhere.
///     </para>
/// </remarks>
public sealed class ExportBytePinTests
{
    /// <summary>The conversion stamp the pins were captured with.</summary>
    private static readonly DateTime PinnedConversionTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private const string NoCorpus =
        "No TestData corpus: TestData/ is git-ignored and absent from this checkout.";

    /// <summary>Walks up from the test binary looking for the repo's TestData/.</summary>
    private static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static string? Resolve(string relative)
    {
        var root = FindTestDataRoot();
        if (root == null) return null;
        var path = Path.Combine(root, relative);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    ///     The first 128 bits of the SHA-256, lowercase hex. Truncated because
    ///     xunit ELIDES a long string in its failure message ("abc..."), which
    ///     makes re-capturing a pin after a deliberate format change impossible
    ///     from the output alone. 128 bits is still far beyond any collision
    ///     these fixtures could produce.
    /// </summary>
    private static string Sha(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes))[..32];

    /// <summary>
    ///     A written <c>.rscp</c> without its <c>&lt;dcProjectId&gt;</c> line. The id
    ///     (#562) postdates every pin here and is not what they pin: a freshly built
    ///     project (the Guitar Pro import) gets a RANDOM one, and a corpus file
    ///     without one gets an id derived from its bytes. Everything else must still
    ///     match the pre-optimisation writer byte for byte; the id itself is pinned
    ///     by <c>RscpProjectIdTests</c>.
    ///     <para>
    ///         The <c>&lt;dcWriterVersion&gt;</c> stamp is put back to the
    ///         <see cref="PinnedWriterVersion" /> the pins were captured with. It is
    ///         the app's release version, so without this every release bump moved
    ///         every pin here; the stamp itself is pinned by
    ///         <c>RscpForwardCompatibilityTests</c>.
    ///     </para>
    /// </summary>
    private static byte[] WrittenWithoutTheProjectId(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var start = text.IndexOf("  <dcProjectId>", StringComparison.Ordinal);
        Assert.True(start >= 0, "the writer no longer writes <dcProjectId> — RscpProjectIdTests should say why");
        var end = text.IndexOf('\n', start);
        text = text.Remove(start, end - start + 1);

        const string open = "<" + RscpFormatContract.WriterVersionElement + ">";
        const string close = "</" + RscpFormatContract.WriterVersionElement + ">";
        var stampStart = text.IndexOf(open, StringComparison.Ordinal);
        Assert.True(stampStart >= 0, "the writer no longer stamps <dcWriterVersion> — RscpForwardCompatibilityTests should say why");
        var valueStart = stampStart + open.Length;
        var valueEnd = text.IndexOf(close, valueStart, StringComparison.Ordinal);
        text = text[..valueStart] + PinnedWriterVersion + text[valueEnd..];
        return System.Text.Encoding.UTF8.GetBytes(text);
    }

    /// <summary>The <c>&lt;dcWriterVersion&gt;</c> the .rscp pins were captured with.</summary>
    private const string PinnedWriterVersion = "1.0.0";

    // ── .rscp load → save round trip ─────────────────────────────────────
    // Hashes the re-serialized project, so any field RscpHeaderParser stopped
    // finding when its thirty Descendants() walks became one indexed pass shows
    // up here as different bytes.

    [Theory]
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp",
        "da5de97f69bf8a6aad3317e10bcbce2c")]
    [InlineData(@"RsCharter\Ave Mujica - DIVINE\project.rscp",
        "3f11d2aaec5e46ba8d52b9c4c9001a33")]
    [InlineData(@"RsCharter\Yorushika - Replicant\project.rscp",
        "848d028fad8eb2dc632b34abcd249454")]
    [InlineData(@"RsCharter\Atarayo - Natsu ga Kuru Tabi\project.rscp",
        "fdcdd5a89c1a27d794bc3391d55d962f")]
    public void RscpRoundTrip_IsByteIdenticalToThePreOptimisationWriter(string relative, string expected)
    {
        var path = Resolve(relative);
        Assert.SkipWhen(path == null, NoCorpus);

        var serializer = new RscpChartSerializer();
        var project = serializer.Load(path!);

        var temp = Path.Combine(Path.GetTempPath(), $"bytepin_{Guid.NewGuid():N}.rscp");
        try
        {
            serializer.Save(temp, project);
            Assert.Equal(expected, Sha(WrittenWithoutTheProjectId(temp)));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ── SNG build (RsChordDensity's compact-repeat decision) ─────────────
    // Six of these were re-captured ONCE, for #621, and only after the change was
    // proven to be exactly what was intended: a template that only "Split notes"
    // chords play is no longer exported (EOF leaves it out too, rs.c:219/:2124).
    // Those six arrangements hold RsCharter's nameless split-chord templates, all
    // at the END of the list, so no other chord id moved — and
    // UnpackedSng_WithTheSplitOnlyTemplatesPutBack_IsTheHashBefore621 proves the
    // new bytes plus those templates ARE the old bytes.
    //
    // Ten were re-captured again, for two deliberate export changes, each found by
    // bisecting and each proven to be ALL that moved: with both undone, every one of
    // the ten hashes back to its previous pin exactly.
    //   * "closing noguitar": the SNG mapper adds Editor on Fire's closing
    //     noguitar section after the last note when a chart has none
    //     (EndNoGuitarSection). UnpackedSng_WithoutTheClosingNoGuitarSection_IsTheHashBeforeIt
    //     takes it back out of the arrangements nothing else moved in.
    //   * "accent rule": a whole-chord accent or ignore no longer counts as a
    //     per-string one, so those strums now ship as compact repeats
    //     (RsChordDensity.CompactRepeatVerdict). Checked by reverting that rule.
    //
    // Then twelve (every one with more than two phrases) re-captured for #241, the
    // one phrase reader for both stores (PhraseMarkers.Iterations): a chart that keeps
    // its phrases as markers now numbers them by first appearance instead of by
    // Arrangement.Phrases. The iterations are the same (same times, same names, all
    // fourteen arrangements); with the ids mapped back to Arrangement.Phrases order
    // every SNG pin, put-back and noguitar case hashed to its previous value exactly.

    [Theory]
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp", 0,
        "c47d0ff42801871dd09f931e5592deba")] // #621: 4 split-only templates out; accent rule
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp", 1,
        "653513c3d7e43f188c81a67437dcfbea")] // closing noguitar
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp", 2,
        "85042bf10f4de0ac6ebaebea33c32065")] // #621: 4 split-only templates out; closing noguitar
    // The 1,071-chord / 199-hand-shape arrangement — the worst case for the
    // quadratic scans the binary search replaced.
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp", 3,
        "689db23e64b4aefaebdd8e57cbf7215a")] // #621: 1 split-only template out; closing noguitar, accent rule
    [InlineData(@"RsCharter\Ave Mujica - DIVINE\project.rscp", 0,
        "b0103dd5a30eef6bdc868a28af0388ae")] // #621: 2 split-only templates out; closing noguitar, accent rule
    [InlineData(@"RsCharter\Ave Mujica - DIVINE\project.rscp", 1,
        "b0103dd5a30eef6bdc868a28af0388ae")] // #621: 2 split-only templates out; closing noguitar, accent rule
    [InlineData(@"RsCharter\Ave Mujica - DIVINE\project.rscp", 2,
        "1e5dcc860baac20b7dc530a159e237d3")] // closing noguitar, accent rule
    [InlineData(@"RsCharter\Yorushika - Replicant\project.rscp", 0,
        "9cd633e90755460d8a0f652f95c7ba8d")] // closing noguitar
    [InlineData(@"RsCharter\Yorushika - Replicant\project.rscp", 1,
        "2559eae43199839cfded4e2e5494e251")] // #621: 3 split-only templates out; closing noguitar
    [InlineData(@"RsCharter\Yorushika - Replicant\project.rscp", 2,
        "eec81ee5ed2fedfe3d88b1dcb0eb9677")] // closing noguitar, accent rule
    [InlineData(@"RsCharter\Atarayo - Natsu ga Kuru Tabi\project.rscp", 0,
        "8e6f924559e3cb5f0fb4e660920581dd")]
    [InlineData(@"RsCharter\Atarayo - Natsu ga Kuru Tabi\project.rscp", 1,
        "2c327c0b4bb4d67949894e10eec8fe87")]
    [InlineData(@"RsCharter\Atarayo - Natsu ga Kuru Tabi\project.rscp", 2,
        "ecbd57227cd40f2abe99958edfba5bc5")]
    [InlineData(@"RsCharter\Atarayo - Natsu ga Kuru Tabi\project.rscp", 3,
        "920a93a71a72ffc56a3ff17b799cf125")]
    public void UnpackedSng_IsByteIdenticalToThePreOptimisationBuilder(
        string relative, int arrangementIndex, string expected)
    {
        var path = Resolve(relative);
        Assert.SkipWhen(path == null, NoCorpus);

        var project = new RscpChartSerializer().Load(path!);
        var instrumentals = project.Arrangements
            .Where(a => a.ArrangementType != ArrangementType.Vocal)
            .ToList();
        Assert.True(arrangementIndex < instrumentals.Count,
            $"'{relative}' has {instrumentals.Count} instrumental arrangements; the pin names #{arrangementIndex}. " +
            "The fixture changed — do not re-bless the hash without checking why.");

        var arrangement = instrumentals[arrangementIndex];
        PhraseSectionGenerator.GenerateIfAbsent(arrangement, project.Beats, 0.0);
        var sng = SngFromDomainMapper
            .BuildInstrumental(arrangement, project.Beats, 0.0, PinnedConversionTime).Sng;

        Assert.Equal(expected, Sha(RocksmithSngWriter.WriteUnpacked(sng)));
    }

    /// <summary>
    ///     The SNGs whose only change since their previous pin is EOF's closing
    ///     noguitar section: taken back out, they write the previous pin's bytes
    ///     exactly. So the section is all the mapper added (its notes, phrases and
    ///     every other section are untouched), and it went in where
    ///     <c>EndNoGuitarSection</c> says it does. The hashes are the previous pins
    ///     with #241's first-appearance phrase numbering, the only thing that moved
    ///     them (see the pins above).
    /// </summary>
    [Theory]
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp", 1, "db86cd6596086ea94087752cafe90bfc")]
    [InlineData(@"RsCharter\Yorushika - Replicant\project.rscp", 0, "4b6850c83ff0a8e7194e87b302700b02")]
    [InlineData(@"RsCharter\Yorushika - Replicant\project.rscp", 1, "e4ba5a16dcfb96584a12adc7e77341f0")]
    public void UnpackedSng_WithoutTheClosingNoGuitarSection_IsTheHashBeforeIt(
        string relative, int arrangementIndex, string before)
    {
        var path = Resolve(relative);
        Assert.SkipWhen(path == null, NoCorpus);

        var sng = BuildSng(path!, arrangementIndex, unsplit: false);
        var added = sng.Sections.FindIndex(s => s.Name == EndNoGuitarSection.Name);
        Assert.True(added >= 0, "no closing noguitar section: EndNoGuitarSection no longer adds one here");
        RemoveClosingNoGuitarSection(sng, added);

        Assert.Equal(before, Sha(RocksmithSngWriter.WriteUnpacked(sng)));
    }

    /// <summary>
    ///     Takes the added section out and gives back what adding it changed: the
    ///     section before it ran on to its end, and a leading one (the chart's
    ///     sections all start after the last note) held the notes' string mask.
    /// </summary>
    private static void RemoveClosingNoGuitarSection(RocksmithSng sng, int index)
    {
        var removed = sng.Sections[index];
        sng.Sections.RemoveAt(index);
        if (index == 0)
        {
            sng.Sections[0] = sng.Sections[0] with { StringMask = removed.StringMask };
            return;
        }

        sng.Sections[index - 1] = sng.Sections[index - 1] with
        {
            EndTime = removed.EndTime,
            EndPhraseIterationId = removed.EndPhraseIterationId
        };
    }

    /// <summary>
    ///     The six SNG pins #621 re-captured, with the split-only templates put back:
    ///     the SNG built now, plus the templates a "Split notes"-free build of the same
    ///     arrangement appends after them, writes the pre-#621 bytes exactly. So the
    ///     ONLY thing #621 changed in those SNGs is the dropped templates — no chord id,
    ///     no repeat decision, nothing else. The hashes were re-captured for the
    ///     closing noguitar section, the whole-chord accent rule and #241's phrase
    ///     numbering (see the pins above); the put-back still has to land on them.
    /// </summary>
    [Theory]
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp", 0, 4, "8500346d5b6eb8c6da7806d430fc6657")]
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp", 2, 4, "88b97934946ffea9035b174380c753bf")]
    [InlineData(@"RsCharter\Yorushika - Odorouze\project.rscp", 3, 1, "b87b9554ab5d81bba9ffad43a6dbc037")]
    [InlineData(@"RsCharter\Ave Mujica - DIVINE\project.rscp", 0, 2, "8da0a7e1c23edcd20e699407e8294352")]
    [InlineData(@"RsCharter\Ave Mujica - DIVINE\project.rscp", 1, 2, "8da0a7e1c23edcd20e699407e8294352")]
    [InlineData(@"RsCharter\Yorushika - Replicant\project.rscp", 1, 3, "ea72aec2c2e63b678c4d12bd03332b83")]
    public void UnpackedSng_WithTheSplitOnlyTemplatesPutBack_IsTheHashBefore621(
        string relative, int arrangementIndex, int dropped, string before)
    {
        var path = Resolve(relative);
        Assert.SkipWhen(path == null, NoCorpus);

        var now = BuildSng(path!, arrangementIndex, unsplit: false);
        var unsplit = BuildSng(path!, arrangementIndex, unsplit: true);

        Assert.Equal(now.Chords.Count + dropped, unsplit.Chords.Count);
        Assert.Equal(now.Chords.Select(t => t.Name), unsplit.Chords.Take(now.Chords.Count).Select(t => t.Name));
        now.Chords.AddRange(unsplit.Chords.Skip(now.Chords.Count));
        Assert.Equal(before, Sha(RocksmithSngWriter.WriteUnpacked(now)));
    }

    /// <summary>One instrumental arrangement's SNG, as the pins build it; with
    /// <paramref name="unsplit" />, every chord's "Split notes" cleared first.</summary>
    private static RocksmithSng BuildSng(string path, int arrangementIndex, bool unsplit)
    {
        var project = new RscpChartSerializer().Load(path);
        var arrangement = project.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal).ToList()[arrangementIndex];
        if (unsplit)
        {
            foreach (var chord in arrangement.Levels.SelectMany(l => l.Chords)) chord.SplitIntoNotes = false;
        }

        PhraseSectionGenerator.GenerateIfAbsent(arrangement, project.Beats, 0.0);
        return SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 0.0, PinnedConversionTime).Sng;
    }

    // ── Album art (one decode, three sizes) ──────────────────────────────

    [Theory]
    [InlineData(@"EOF\Yorushika - Parade\Cover.jpg", 64,
        "345409358502b757c80477e4e82b5d55")]
    [InlineData(@"EOF\Yorushika - Parade\Cover.jpg", 128,
        "716571347a989494ffb96c2d4a5c22e3")]
    [InlineData(@"EOF\Yorushika - Parade\Cover.jpg", 256,
        "c4f3d81325cd0aa43210d80f8db7fa9d")]
    [InlineData(@"EOF\Ave Mujica - Crucifix X\Crucifix_X_Digital_Cover.png", 64,
        "32f019a4ad93bf4576bb44b59188c442")]
    [InlineData(@"EOF\Ave Mujica - Crucifix X\Crucifix_X_Digital_Cover.png", 128,
        "1e3f54cd7b68c1f0592292f8135e6b6f")]
    [InlineData(@"EOF\Ave Mujica - Crucifix X\Crucifix_X_Digital_Cover.png", 256,
        "fa7b87cb1f95d3b3c2d12957f8fe7cd9")]
    [InlineData(@"EOF\Yorushika - Fireworks Beneath My Shoes\TheSummerGrassisGettinginMyWayCover.png", 64,
        "41c2d00b21119ef6b068c02c331902c2")]
    [InlineData(@"EOF\Yorushika - Fireworks Beneath My Shoes\TheSummerGrassisGettinginMyWayCover.png", 128,
        "80c53fd61406360db0de70e5bbe9370d")]
    [InlineData(@"EOF\Yorushika - Fireworks Beneath My Shoes\TheSummerGrassisGettinginMyWayCover.png", 256,
        "e0ad709b194caea3989dea4dee9034fc")]
    public void CoverArtDds_IsByteIdenticalToThePreOptimisationEncoder(
        string relative, int size, string expected)
    {
        var path = Resolve(relative);
        Assert.SkipWhen(path == null, NoCorpus);

        Assert.Equal(expected, Sha(DdsEncoder.EncodeCoverArt(path!, size)));
    }

    /// <summary>
    ///     The batch overload is what the package builder calls; it must produce
    ///     the same bytes as three single-size calls. That equivalence is the
    ///     whole optimisation (one decode instead of three), so it is pinned
    ///     directly rather than only through the hashes above.
    /// </summary>
    [Theory]
    [InlineData(@"EOF\Yorushika - Parade\Cover.jpg")]
    [InlineData(@"EOF\Ave Mujica - Crucifix X\Crucifix_X_Digital_Cover.png")]
    [InlineData(@"EOF\Yorushika - Fireworks Beneath My Shoes\TheSummerGrassisGettinginMyWayCover.png")]
    public void EncodeCoverArtSizes_MatchesThreeSeparateEncodeCoverArtCalls(string relative)
    {
        var path = Resolve(relative);
        Assert.SkipWhen(path == null, NoCorpus);

        int[] sizes = [64, 128, 256];
        var batched = DdsEncoder.EncodeCoverArtSizes(path!, sizes).ToList();

        Assert.Equal(sizes.Length, batched.Count);
        for (var i = 0; i < sizes.Length; i++)
        {
            Assert.Equal(sizes[i], batched[i].Size);
            Assert.Equal(DdsEncoder.EncodeCoverArt(path!, sizes[i]), batched[i].Data);
        }
    }

    // ── Guitar Pro import ────────────────────────────────────────────────
    // Every track of each file is imported and the whole resulting graph is
    // written through the .rscp writer, so any per-note property GpNoteParser
    // stopped reading lands here as different bytes. The import also fingers
    // chords through ChordTemplateFingerSetter: two pins moved with #488 by
    // exactly three templates (Em 022000 and Em7 022030 now 1-2, the G7 shell
    // 3x343 now 1x121), checked by diffing the imported templates.
    //
    // RE-CAPTURED for #494 (FHP generation), which changes what the import
    // GENERATES on purpose: the anchors, and the chord templates a free chord is
    // re-fingered onto. Diffed against main @ 538fe0b1d (#488 merged), the .rscp
    // differ ONLY in <anchor>, <chordTemplate> and the template ids on
    // <soundChord>/<handShape> (each still names a template with the same
    // frets) — every note, chord note and timing byte is
    // identical, so the parser half of this pin still holds.
    //
    // RE-CAPTURED for #502 (bass hand reach, learned chord shapes). With the
    // three bass rules switched off and the learned table emptied the old three
    // hashes came back exactly; against them the new .rscp differ ONLY in
    // <anchor> and <chordTemplate> elements.
    //
    // Musica Caelestis RE-CAPTURED for #493 (chord-naming vocabulary): diffed
    // against the pre-#493 suggester, exactly four <chordTemplate> chordName
    // attributes change, all from "" to "Dmin9" (a 5th-less m9 shell the old
    // vocabulary could not name). Nothing else moves.
    //
    // Fireworks, Musica Caelestis and TABS.gp5 RE-CAPTURED for #508: the import's
    // FHP pass now persists which fingerings it minted, as followed="T" on those
    // <chordTemplate>s (3, 2 and 14 of them). With every ` followed="T"` removed
    // the bytes hash to the old pins exactly; Parade mints none and is unchanged.
    // Parade RE-CAPTURED for #799: it opens on open strings, so the FHP pass now puts
    // its first anchor at the first note instead of the first fretted one.
    //
    // Fireworks and TABS.gp5 RE-CAPTURED for the Guitar Pro import fixes (first
    // moved by the ".gpx, slide-ins, x/8 placement, round-trip fidelity" merge,
    // found by bisecting). Diffed against the import before it, the .rscp differ
    // ONLY in what those fixes change on purpose: a grace hammer-on becomes its own
    // note before the target, a hammer-on or pull-off flag moves to the destination
    // note, accents are read, a slide-in no longer imports as a slide out of the
    // note, and one chord template's fingering. Musica Caelestis
    // and Parade are unchanged.

    [Theory]
    [InlineData(@"EOF\Yorushika - Fireworks Beneath My Shoes\Yorushika - Firworks Beneath My Shoes.gp",
        "b0580ffeaeee835d361b81ef0006503f")] // Guitar Pro import fixes
    [InlineData(@"EOF\Ave Mujica - Musica Caelestis\Ave Mujica - Musica Caelestis.gp",
        "8e5c3dff3d9b58aefdd2043444188c76")]
    [InlineData(@"EOF\Yorushika - Parade\Yorushika - Parade.gp",
        "804f9eced05add7e76b43755da51c5ab")]
    // The GP3-5 BINARY reader, which shares none of the gpif property code —
    // here so a change to one importer that silently moves the other is caught.
    [InlineData("TABS.gp5",
        "e7816cb6ba2ea21ed529b844d4541931")] // Guitar Pro import fixes
    public void GuitarProImport_IsByteIdenticalToThePreOptimisationParser(string relative, string expected)
    {
        var path = Resolve(relative);
        Assert.SkipWhen(path == null, NoCorpus);

        var importer = new GpImporter();
        var tracks = importer.GetAvailableTracks(path!);
        Assert.NotEmpty(tracks);

        var result = importer.ImportTracksWithMetadata(path!, tracks.Select(t => t.Id).ToList());
        var imported = new ChartProject();
        foreach (var arrangement in result.Arrangements) imported.Arrangements.Add(arrangement);

        var temp = Path.Combine(Path.GetTempPath(), $"bytepin_gp_{Guid.NewGuid():N}.rscp");
        try
        {
            new RscpChartSerializer().Save(temp, imported);
            Assert.Equal(expected, Sha(WrittenWithoutTheProjectId(temp)));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
