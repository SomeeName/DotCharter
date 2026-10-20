using System.Text;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     The one real backing source for the practice planner's genre preference:
///     the <c>dcGenre</c> element in an own chart's <c>.rscp</c> HEADER.
///     <para>
///         What these pin: the value actually comes back, the chart BODY is never
///         walked, a broken/absent file fails soft instead of throwing out of a
///         library scan, and the read happens ONCE per scan rather than per plan
///         generation (the planner regenerates on every knob turn, so a per-
///         generation read would be file I/O on the UI thread at pointer rate).
///     </para>
/// </summary>
public sealed class ChartGenreIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public ChartGenreIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // ── the reader ────────────────────────────────────────────────────────────

    [Fact]
    public void ReadsTheAuthoredGenreOutOfTheHeader()
    {
        var path = WriteProject("Blues", extraHeader: "<dcCharter>Someone</dcCharter>");
        Assert.Equal("Blues", RscpGenreReader.ReadGenre(path));
    }

    /// <summary>A chart that never had the field, and one whose metadata dialog
    /// was opened and left blank, are the SAME answer: unknown. Anything else
    /// would make an empty string a genre nothing can ever match.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingOrBlankGenreReadsAsUnknown(string? genre)
        => Assert.Null(RscpGenreReader.ReadGenre(WriteProject(genre)));

    [Fact]
    public void TheGenreIsTrimmed()
        => Assert.Equal("Post Rock", RscpGenreReader.ReadGenre(WriteProject("  Post Rock  ")));

    /// <summary>
    ///     HEADER ONLY — the whole reason this type exists rather than
    ///     <c>RscpChartSerializer.Load</c>. The stream is deliberately TRUNCATED
    ///     part-way through the chart body: a reader that materialised the
    ///     document would throw on the unterminated XML, and one that walked into
    ///     <c>&lt;arrangements&gt;</c> would too. Returning the genre proves the
    ///     note data was never touched.
    /// </summary>
    [Fact]
    public void TheChartBodyIsNeverWalked()
    {
        var xml = new StringBuilder()
            .Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><project>")
            .Append("<chartFormatVersion>3</chartFormatVersion>")
            .Append("<title>Song</title>")
            .Append("<dcGenre>Metal</dcGenre>")
            .Append("<musicFileName>song.ogg</musicFileName>")
            .Append("<beats>");
        for (var i = 0; i < 500; i++) xml.Append("<beat p=\"").Append(i).Append("\" />");
        // …and then the file simply stops. No </beats>, no </project>.
        var path = Path.Combine(_dir, "truncated.rscp");
        File.WriteAllText(path, xml.ToString());

        Assert.Equal("Metal", RscpGenreReader.ReadGenre(path));
    }

    /// <summary>The stop condition is the first BULK node, so a chart with no
    /// genre still costs a header walk and not a document load — same truncation
    /// trick, and this time the honest answer is "unknown".</summary>
    [Fact]
    public void AGenrelessChartStopsAtTheFirstBulkNode()
    {
        var path = Path.Combine(_dir, "genreless.rscp");
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><project>" +
            "<title>Song</title><musicFileName>song.ogg</musicFileName>" +
            "<arrangements><arrangement><notes>");

        Assert.Null(RscpGenreReader.ReadGenre(path));
    }

    /// <summary>A <c>dcGenre</c> nested inside some other element is NOT the
    /// header's genre — only a direct child of the root counts, or a chord named
    /// in a sidecar could donate one.</summary>
    [Fact]
    public void OnlyARootChildCounts()
    {
        var path = Path.Combine(_dir, "nested.rscp");
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><project>" +
            "<stems><stem name=\"song\"><dcGenre>Jazz</dcGenre></stem></stems>" +
            "<beats />" +
            "</project>");

        Assert.Null(RscpGenreReader.ReadGenre(path));
    }

    // ── the index ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     ONCE PER SCAN. Warming an unchanged set a second time must do NO file
    ///     reads — the planner's preview regenerates on every slider tick and its
    ///     candidate list is assembled on the UI thread, so a per-generation read
    ///     would be disk I/O between a pointer move and the next frame.
    /// </summary>
    [Fact]
    public void TheHeaderIsReadOncePerScan_NotPerLookup()
    {
        var source = new CountingSource { ["a.rscp"] = "Blues", ["b.rscp"] = "Metal" };
        var index = new ChartGenreIndex(source);
        string[] paths = ["a.rscp", "b.rscp"];

        Assert.Equal(2, index.Warm(paths));
        Assert.Equal(2, source.Reads);

        // A thousand lookups — what a drag on the session-length slider costs.
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal("Blues", index.GenreFor("a.rscp"));
            Assert.Equal("Metal", index.GenreFor("b.rscp"));
        }

        Assert.Equal(2, source.Reads);

        // A whole second scan of an unchanged library reads nothing either.
        Assert.Equal(0, index.Warm(paths));
        Assert.Equal(2, source.Reads);
    }

    /// <summary>…but an EDITED chart re-reads on the next scan, or re-tagging a
    /// song in the metadata dialog would never reach the planner.</summary>
    [Fact]
    public void AnEditedChartIsReReadOnTheNextScan()
    {
        var source = new CountingSource { ["a.rscp"] = "Blues" };
        var index = new ChartGenreIndex(source);

        Assert.Equal(1, index.Warm(["a.rscp"]));
        Assert.Equal("Blues", index.GenreFor("a.rscp"));

        source["a.rscp"] = "Jazz";
        source.Bump("a.rscp");

        Assert.Equal(1, index.Warm(["a.rscp"]));
        Assert.Equal("Jazz", index.GenreFor("a.rscp"));
    }

    /// <summary>A malformed chart, a chart that vanished, a locked file — none of
    /// them may throw out of a library scan, and all of them read as unknown.
    /// A genre nobody can read must never become an EXCLUSION.</summary>
    [Fact]
    public void ABrokenOrMissingChartFailsSoft()
    {
        var source = new CountingSource { ["good.rscp"] = "Blues" };
        source.Throwers.Add("broken.rscp");
        source.Missing.Add("gone.rscp");
        var index = new ChartGenreIndex(source);

        var reads = index.Warm(["good.rscp", "broken.rscp", "gone.rscp"]);

        Assert.Equal("Blues", index.GenreFor("good.rscp"));
        Assert.Null(index.GenreFor("broken.rscp"));
        Assert.Null(index.GenreFor("gone.rscp"));
        // The broken one WAS read (and is cached as a failure, so it is not
        // retried every scan); the missing one never got as far as a read.
        Assert.Equal(2, reads);
        Assert.Equal(0, index.Warm(["good.rscp", "broken.rscp"]));
    }

    /// <summary>A real malformed file on disk, through the real source — the
    /// fake above proves the catch, this proves the catch is reachable.</summary>
    [Fact]
    public void ARealMalformedFileFailsSoftThroughTheRealSource()
    {
        var broken = Path.Combine(_dir, "broken.rscp");
        File.WriteAllText(broken, "this is not xml at all <<<>>>");
        var absent = Path.Combine(_dir, "does-not-exist.rscp");
        var good = WriteProject("Folk");

        var index = new ChartGenreIndex();
        var reads = index.Warm([good, broken, absent]);

        Assert.Equal("Folk", index.GenreFor(good));
        Assert.Null(index.GenreFor(broken));
        Assert.Null(index.GenreFor(absent));
        Assert.Equal(2, reads);
    }

    /// <summary>A path never warmed is unknown, not an exception.</summary>
    [Fact]
    public void AnUnwarmedPathIsUnknown()
    {
        var index = new ChartGenreIndex(new CountingSource());
        Assert.Null(index.GenreFor("never-seen.rscp"));
        Assert.Null(index.GenreFor(""));
    }

    // ── the vocabulary the Song Metadata dialog offers ────────────────────────

    /// <summary>
    ///     The genre quick-picks are the genres this user has ACTUALLY authored,
    ///     most-used first — a Rocksmith package carries no genre anywhere, so
    ///     their own charts are the only honest source. Reading, not I/O: the same
    ///     warmed cache the planner uses.
    /// </summary>
    [Fact]
    public void KnownGenres_ReportsWhatTheUserActuallyUses_MostUsedFirst()
    {
        var source = new CountingSource
        {
            ["a.rscp"] = "Rock", ["b.rscp"] = "Metal", ["c.rscp"] = "Rock",
            ["d.rscp"] = "Blues", ["e.rscp"] = "Metal", ["f.rscp"] = "Rock"
        };
        var index = new ChartGenreIndex(source);
        index.Warm(["a.rscp", "b.rscp", "c.rscp", "d.rscp", "e.rscp", "f.rscp"]);
        var readsAfterWarm = source.Reads;

        Assert.Equal(["Rock", "Metal", "Blues"], index.KnownGenres(10));
        // The cap keeps the strip on one line beside the field.
        Assert.Equal(["Rock", "Metal"], index.KnownGenres(2));
        Assert.Empty(index.KnownGenres(0));
        // …and none of that touched a file.
        Assert.Equal(readsAfterWarm, source.Reads);
    }

    /// <summary>
    ///     Two spellings of one genre are ONE chip — offering both would invite
    ///     the user to split their own library — and the spelling offered is the
    ///     one they typed most often, never a normalised invention.
    /// </summary>
    [Fact]
    public void KnownGenres_FoldsCaseButKeepsTheSpellingTheUserPrefers()
    {
        var source = new CountingSource
        {
            ["a.rscp"] = "Rock", ["b.rscp"] = "rock", ["c.rscp"] = "Rock", ["d.rscp"] = "  Rock  "
        };
        var index = new ChartGenreIndex(source);
        index.Warm(["a.rscp", "b.rscp", "c.rscp", "d.rscp"]);

        Assert.Equal(["Rock"], index.KnownGenres(10));
    }

    /// <summary>Nothing scanned, or nobody ever typed one, is an EMPTY answer —
    /// the dialog then shows no chips at all rather than inventing four.</summary>
    [Fact]
    public void KnownGenres_IsEmptyWhenNothingHasBeenAuthored()
    {
        var source = new CountingSource { ["a.rscp"] = null, ["b.rscp"] = "  " };
        var index = new ChartGenreIndex(source);

        Assert.Empty(index.KnownGenres(5));       // never warmed
        index.Warm(["a.rscp", "b.rscp"]);
        Assert.Empty(index.KnownGenres(5));       // warmed, but no genre anywhere
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string WriteProject(string? genre, string extraHeader = "")
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.rscp");
        var genreNode = genre == null ? "" : $"<dcGenre>{genre}</dcGenre>";
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><project>" +
            "<chartFormatVersion>3</chartFormatVersion><editMode>GUITAR</editMode>" +
            "<title>Song</title><artistName>Artist</artistName>" +
            extraHeader + genreNode +
            "<musicFileName>song.ogg</musicFileName>" +
            "<stems><stem name=\"song\" path=\"song.ogg\" /></stems>" +
            "<beats /><arrangements /><text />" +
            "</project>");
        return path;
    }

    /// <summary>A genre source that counts its reads and lets a test move a
    /// file's content stamp without touching a disk.</summary>
    private sealed class CountingSource : IChartGenreSource
    {
        private readonly Dictionary<string, string?> _genres = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _stamps = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Throwers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Reads { get; private set; }

        public string? this[string path]
        {
            set
            {
                _genres[path] = value;
                _stamps.TryAdd(path, 1);
            }
        }

        public string? StampOf(string projectPath)
            => Missing.Contains(projectPath) ? null : $"v{_stamps.GetValueOrDefault(projectPath, 1)}";

        public string? ReadGenre(string projectPath)
        {
            Reads++;
            if (Throwers.Contains(projectPath)) throw new InvalidDataException("malformed");
            return _genres.GetValueOrDefault(projectPath);
        }

        public void Bump(string path) => _stamps[path] = _stamps.GetValueOrDefault(path, 1) + 1;
    }
}
