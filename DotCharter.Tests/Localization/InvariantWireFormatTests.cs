using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Localization;

/// <summary>
///     <para>
///         Everything DotCharter writes to a FILE or a WIRE is invariant text.
///         The `.rscp` project format, the XML attributes inside it and the two
///         Core primitives those attributes are made of
///         (<see cref="FractionalPosition" />, <see cref="Fraction" />) must
///         render and re-read identically on every machine, whatever the user's
///         Windows language is.
///     </para>
///     <para>
///         It did not, until 2026-08-29. <c>FractionalPosition.ToString</c>,
///         <c>Fraction.ToString</c> and both <c>TryParse</c>s used the AMBIENT
///         culture, and <c>RscpWriter.BuildTuningNode</c> joins its offsets with
///         <c>string.Join</c>, which does the same. Under sv-SE, fi-FI, nb-NO,
///         lt-LT and sv-FI <see cref="NumberFormatInfo.NegativeSign" /> is
///         U+2212 MINUS SIGN, not U+002D HYPHEN-MINUS — so a Drop-D tuning was
///         written <c>tuningValues="−2,0,0,0,0,0"</c> and a negative position
///         <c>p="−3 1/2"</c>. Reading those back on ANY other machine fails
///         <c>int.TryParse</c>, and <c>RscpParsingUtilities.ParseFraction</c>
///         returns <see cref="FractionalPosition.Zero" /> on a miss rather than
///         throwing: the note moves to beat 0 and the guitar re-tunes itself to
///         E standard, with nothing logged.
///     </para>
/// </summary>
/// <remarks>
///     In the <c>LocalizationSingleton</c> collection because every case here
///     moves <see cref="CultureInfo.CurrentCulture" />, which is process-wide —
///     required by <c>Infrastructure/CultureCollectionLintTests</c>.
/// </remarks>
[Collection("LocalizationSingleton")]
public sealed class InvariantWireFormatTests
{
    /// <summary>
    ///     Three cultures whose negative sign is NOT the ASCII hyphen, one that
    ///     merely moves the decimal separator, and the baseline. The first group
    ///     is what makes this file worth having;
    ///     <see cref="TheHostileCultures_ActuallyRenderANonAsciiMinus" /> proves
    ///     they still do rather than assuming it.
    /// </summary>
    private static readonly string[] Cultures = ["sv-SE", "fi-FI", "nb-NO", "de-DE", "en-US"];

    /// <summary>Positions worth round-tripping, negative ones first.</summary>
    private static readonly FractionalPosition[] Positions =
    [
        new(-3, new Fraction(1, 2)),
        new(-1, Fraction.Zero),
        new(-120, new Fraction(5, 96)),
        FractionalPosition.Zero,
        new(7, new Fraction(3, 4)),
        new(1024, new Fraction(95, 96))
    ];

    public static TheoryData<string> CultureCases()
    {
        var data = new TheoryData<string>();
        foreach (var culture in Cultures) data.Add(culture);
        return data;
    }

    private static void InCulture(string name, Action body)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    /// <summary>
    ///     The non-vacuity pin. If ICU ever normalises these to the ASCII hyphen,
    ///     every case below still passes while testing nothing — this one fails
    ///     instead and says so.
    /// </summary>
    [Fact]
    public void TheHostileCultures_ActuallyRenderANonAsciiMinus()
    {
        var hostile = new[] { "sv-SE", "fi-FI", "nb-NO" }
            .Where(name => CultureInfo.GetCultureInfo(name).NumberFormat.NegativeSign != "-")
            .ToList();

        Assert.True(hostile.Count == 3,
            "sv-SE / fi-FI / nb-NO no longer render U+2212 for a minus sign, so nothing in this file " +
            "exercises the fault it was written for. Find cultures that still do, or delete the file. " +
            "Non-ASCII today: " + (hostile.Count == 0 ? "(none)" : string.Join(", ", hostile)));

        // And the ambient-culture rendering these cases stand in for.
        InCulture("sv-SE", () => Assert.Equal("−2", (-2).ToString()));
    }

    [Theory]
    [MemberData(nameof(CultureCases))]
    public void AFractionalPosition_RoundTripsThroughItsOwnText(string culture)
    {
        InCulture(culture, () =>
        {
            foreach (var position in Positions)
            {
                var text = position.ToString();
                Assert.True(FractionalPosition.TryParse(text, out var parsed),
                    $"'{text}' (written by {culture}) did not parse back.");
                Assert.Equal(position, parsed);
            }
        });
    }

    [Theory]
    [MemberData(nameof(CultureCases))]
    public void AFraction_RoundTripsThroughItsOwnText(string culture)
    {
        InCulture(culture, () =>
        {
            foreach (var fraction in new[] { new Fraction(-5, 96), new Fraction(1, 4), Fraction.Zero })
            {
                var text = fraction.ToString();
                Assert.True(Fraction.TryParse(text, out var parsed), $"'{text}' ({culture}) did not parse back.");
                Assert.Equal(fraction, parsed);
            }
        });
    }

    /// <summary>
    ///     The single-culture round trip above is SELF-CONSISTENT even when
    ///     broken — sv-SE writes U+2212 and sv-SE reads it back. The fault only
    ///     appears when the file crosses machines, which is the ordinary case:
    ///     a shared chart, a LAN session transfer, a `TestData/` fixture.
    /// </summary>
    [Theory]
    [MemberData(nameof(CultureCases))]
    public void APosition_WrittenInOneCulture_ReadsBackInAnother(string writer)
    {
        var written = new List<string>();
        InCulture(writer, () => written.AddRange(Positions.Select(p => p.ToString())));

        foreach (var reader in Cultures)
        {
            InCulture(reader, () =>
            {
                for (var i = 0; i < Positions.Length; i++)
                {
                    Assert.True(FractionalPosition.TryParse(written[i], out var parsed),
                        $"'{written[i]}' written by {writer} did not parse on {reader}.");
                    Assert.Equal(Positions[i], parsed);
                }
            });
        }
    }

    /// <summary>
    ///     The round trip above can hold inside ONE culture while the file stays
    ///     unreadable everywhere else — sv-SE writes U+2212 and sv-SE reads it.
    ///     What matters for a format is that the TEXT does not move.
    /// </summary>
    [Fact]
    public void ThePositionText_IsTheSameOnEveryMachine()
    {
        var reference = new Dictionary<FractionalPosition, string>();
        InCulture("en-US", () =>
        {
            foreach (var position in Positions) reference[position] = position.ToString();
        });

        foreach (var culture in Cultures)
        {
            InCulture(culture, () =>
            {
                foreach (var position in Positions)
                    Assert.Equal(reference[position], position.ToString());
            });
        }

        // The values under test really are the ones that can differ.
        Assert.Contains(reference.Values, text => text.StartsWith('-'));
    }

    /// <summary>
    ///     The whole-document half, which is what catches a leak the two
    ///     primitives above cannot see — <c>string.Join(",", offsets)</c> in
    ///     <c>RscpWriter.BuildTuningNode</c>, an interpolation, a
    ///     <c>{value:0.00}</c> without a provider. A `.rscp` written on a Swedish
    ///     machine must be the same bytes as one written on an American one.
    /// </summary>
    [Fact]
    public void AnRscpDocument_IsIdenticalWhicheverCultureWroteIt()
    {
        var rendered = new Dictionary<string, string>();
        foreach (var culture in Cultures)
        {
            InCulture(culture, () =>
            {
                var doc = new XDocument();
                new RscpWriter().WriteDocument(doc, BuildDropDProject());
                rendered[culture] = doc.ToString();
            });
        }

        var baseline = rendered["en-US"];

        // Non-vacuous: the document really does carry the values that move.
        Assert.Contains("tuningValues=\"-2,0,0,0,0,0\"", baseline, StringComparison.Ordinal);
        Assert.Contains("p=\"-4 1/2\"", baseline, StringComparison.Ordinal);

        foreach (var (culture, text) in rendered)
        {
            Assert.True(text == baseline,
                $"An .rscp written under {culture} differs from the en-US one. " +
                "Something on the write path is following the ambient culture.");
        }
    }

    /// <summary>
    ///     …and the whole pipeline, across machines: saved by a user on
    ///     <paramref name="culture" />, opened by anyone else. The tuning and the
    ///     note position must both survive. Before the fix a Swedish save came
    ///     back E standard with the note on beat 0, silently — the parser's
    ///     <c>ParseFraction</c> returns <see cref="FractionalPosition.Zero" /> on
    ///     a miss and <c>ParseTuning</c> substitutes 0 per string.
    /// </summary>
    [Theory]
    [MemberData(nameof(CultureCases))]
    public void AnRscpDocument_SavedInOneCulture_ReloadsIntactInAnother(string culture)
    {
        var path = Path.Combine(Path.GetTempPath(), "DotCharterTests",
            Guid.NewGuid().ToString("N"), "invariant.rscp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var serializer = new RscpChartSerializer();
            InCulture(culture, () => serializer.Save(path, BuildDropDProject()));

            ChartProject reloaded = null!;
            InCulture("en-US", () => reloaded = serializer.Load(path));

            var arrangement = Assert.Single(reloaded.Arrangements);
            Assert.Equal([-2, 0, 0, 0, 0, 0], arrangement.TuningPitches.Select(t => t.Step));
            Assert.Equal(new FractionalPosition(-4, new Fraction(1, 2)),
                arrangement.Levels[0].Notes[0].Position);
        }
        finally
        {
            BestEffortDelete.Directory(Path.GetDirectoryName(path)!);
        }
    }

    /// <summary>
    ///     Drop D (a NEGATIVE offset on the low string) plus a note before beat
    ///     zero (a negative <c>BeatId</c>) — the two shapes whose sign is the
    ///     whole fault. Both are ordinary: half the metal catalogue is Drop D,
    ///     and a note dragged left of the first beat is one gesture away.
    /// </summary>
    private static ChartProject BuildDropDProject()
    {
        // One fixed id: each culture builds the SAME project, and a fresh one would mint its own.
        var project = new ChartProject
        {
            ProjectId = "5ed1ab1e5ed1ab1e5ed1ab1e5ed1ab1e", AudioFileName = "song.wav", Bpm = 132.0
        };
        project.Chart.SongName = "Invariant";
        project.Chart.Artist = "Test";

        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement
        {
            Name = "Lead Guitar",
            ArrangementType = ArrangementType.Lead,
            TuningType = "CUSTOM"
        };
        foreach (var step in new[] { -2, 0, 0, 0, 0, 0 })
            arrangement.TuningPitches.Add(new TuningOffset(step));

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(-4, new Fraction(1, 2)),
            Fret = 5,
            StringIndex = 2
        });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }
}
