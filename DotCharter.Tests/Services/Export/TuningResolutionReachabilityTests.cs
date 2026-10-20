namespace DotCharter.Tests.Services.Export;

/// <summary>
///     <b>A chart states its tuning TWICE</b> — note LETTERS
///     (<see cref="Arrangement.Tuning" />) and per-string OFFSETS
///     (<see cref="Arrangement.TuningPitches" />) — so anything that WRITES a
///     tuning has to reconcile them through
///     <see cref="TuningOffsetResolution" />. Reading the offsets directly with a
///     <c>0</c> default exports an arrangement that only has letters as E
///     STANDARD, silently, with every note at a different sounding pitch.
///     <para>
///         That bug was found and fixed FOUR times at once, and then twice more:
///         the `.eof` writer (which runs on every save) and the `.rscp` writer
///         (which IS the project) both still read the offsets directly, because
///         neither lives under <c>Export/</c> and the original sweep was scoped by
///         folder. A folder is not the boundary — WRITING a tuning is. This lint
///         is scoped by what a file DOES.
///     </para>
/// </summary>
public sealed class TuningResolutionReachabilityTests
{
    /// <summary>
    ///     Files that may legitimately touch <c>TuningPitches</c> even though
    ///     their name matches: the ones that FILL it (an importer/parser is the
    ///     authority on what a file said) and the domain type itself.
    /// </summary>
    private static readonly string[] Fillers =
    [
        "EofProGuitarNoteWriter.cs" // per-note fields only; never a tuning
    ];

    [Fact]
    public void NoWriterOrExporter_ReadsTuningPitchesInsteadOfResolvingIt()
    {
        var offenders = new List<string>();

        foreach (var path in WriterSources())
        {
            if (Fillers.Contains(Path.GetFileName(path), StringComparer.Ordinal)) continue;

            var source = WithoutComments(path);
            if (!source.Contains("TuningPitches", StringComparison.Ordinal)) continue;

            // Filling the collection is a parser's job and legal here (the EOF
            // and SNG readers share a file layout with their writers); READING
            // it to write a tuning out is what this forbids.
            if (source.Contains(".TuningPitches.Add", StringComparison.Ordinal) &&
                !source.Contains(".TuningPitches[", StringComparison.Ordinal) &&
                !source.Contains(".TuningPitches.Select", StringComparison.Ordinal) &&
                !source.Contains(".TuningPitches.Count", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.Add(Path.GetFileName(path));
        }

        Assert.True(offenders.Count == 0,
            "These writers read Arrangement.TuningPitches directly instead of going through " +
            "TuningOffsetResolution.Resolve, so an arrangement stating its tuning only as note " +
            "letters ships as E standard: " + string.Join(", ", offenders));
    }

    /// <summary>The lint is worthless if it matches nothing — this pins that it
    /// really is looking at the writers that carry the rule.</summary>
    [Fact]
    public void TheLintActuallyScansTheWritersItClaimsTo()
    {
        var scanned = WriterSources().Select(Path.GetFileName).ToList();

        Assert.Contains("EofExporter.cs", scanned);
        Assert.Contains("RscpWriter.cs", scanned);
        Assert.Contains("SngFromDomainMapper.cs", scanned);
        Assert.Contains("RsXmlSongWriter.cs", scanned);
        Assert.Contains("GpExporter.cs", scanned);
        Assert.Contains("Rs2DlcProjectBuilder.cs", scanned);
    }

    private static List<string> WriterSources()
    {
        var services = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services");
        string[] patterns = ["*Writer.cs", "*Exporter.cs", "*FromDomainMapper.cs", "*ProjectBuilder.cs"];
        return patterns
            .SelectMany(p => new[] { services, Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone") }
                .SelectMany(r => Directory.EnumerateFiles(r, p, SearchOption.AllDirectories)))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal) &&
                        !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Comments are stripped first — a <c>see cref</c> or a rationale
    /// paragraph naming the field is exactly as inert as a dead call.</summary>
    private static string WithoutComments(string path)
    {
        var lines = File.ReadAllLines(path).Select(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("*", StringComparison.Ordinal) ||
                   trimmed.StartsWith("/*", StringComparison.Ordinal)
                ? string.Empty
                : line;
        });
        return string.Join('\n', lines);
    }
}
