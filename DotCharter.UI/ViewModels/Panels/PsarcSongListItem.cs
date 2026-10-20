using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Shared.Library;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.UI.ViewModels.Panels;

public sealed partial class PsarcSongListItem : ObservableObject, ILibrarySong
{
    [ObservableProperty] private Bitmap? _coverImage;
    private int _coverLoadStarted;

    /// <summary>1 while some realized container shows this row. A release
    /// (<see cref="SongCoverCache.Park" /> → <see cref="MarkCoverUnwanted" />)
    /// zeroes it, which aborts a dwell-pending load before it ever touches the
    /// disk — the row that merely scrolled past.</summary>
    private int _coverWanted;

    internal void MarkCoverUnwanted() => Volatile.Write(ref _coverWanted, 0);

    /// <summary>Artist heading for the first row of each artist run, stamped by
    /// <see cref="SongArtistGrouping" /> (the SAME grouper Play Mode's library
    /// uses) and empty on every other row. Empty in every non-artist sort — a
    /// list ordered by title must not sprout artist headers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupHeader))]
    private string _groupHeader = "";

    /// <summary>Bumped by <see cref="DisposeCoverImage"/> (i.e. every time the list
    /// container is recycled out of view). A decode in flight captures it and
    /// throws its bitmap away if it moved — otherwise a load started just before a
    /// scroll lands its bitmap on the item AFTER it was released, which is exactly
    /// the accumulation of decoded bitmaps the release exists to prevent.</summary>
    private int _coverGeneration;

    /// <summary>How many times the cover was released (<see cref="DisposeCoverImage" />): a test's
    /// window onto whether a Library refresh threw an unchanged song's cover away.</summary>
    internal int CoverGeneration => Volatile.Read(ref _coverGeneration);

    /// <summary>The row's words; a Play Mode row built over this one
    /// (<c>PlaySongItem.FromPsarc</c>) speaks through the same.</summary>
    internal ILocalizationService Localization { get; }

    // No decoding here: the constructor runs on the UI thread inside the
    // scan's Progress<T> callback, so a synchronous Bitmap decode per song
    // micro-stutters the browser while a large library streams in. Covers
    // (pre-extracted or in-archive) load on demand via LoadCoverImageAsync.
    /// <param name="song">The scanned entry.</param>
    /// <param name="localization">The row's words.</param>
    /// <param name="block">Why this computer cannot play the song (an Opus
    /// <c>.dcsong</c> without ffmpeg), already probed off the UI thread; the row then
    /// shows disabled with its reason.</param>
    public PsarcSongListItem(LibrarySongEntry song, ILocalizationService localization,
        LibrarySongBlock block = LibrarySongBlock.None)
    {
        Localization = localization;
        Song = song;
        Block = block;
        ArrangementSummary = song.ArrangementSummary;
        CanLoad = song.CanLoad && block == LibrarySongBlock.None;
        FileName = Path.GetFileName(song.PackagePath);
        Subtitle = string.IsNullOrWhiteSpace(song.Album) ? song.Artist : $"{song.Artist} | {song.Album}";
        SongLengthFormatted = TimeSpan.FromSeconds(song.SongLength).ToString(@"m\:ss");
        TuningDisplay = TuningNameResolver.Describe(song.TuningSummary);
        CapoSummary = SongRowFacts.CapoLabel(song.CapoSummary, localization);
        HasCapo = song.CapoSummary > 0;
        TuningCellLabel = SongRowFacts.TuningCell(song.TuningSummary, song.CapoSummary, localization);
        TuningNeedsAttention = SongRowFacts.NeedsAttention(song.TuningSummary, song.CapoSummary);
        TuningName = SongRowFacts.TuningDisplay(song.TuningSummary);
        ArrangementBadges = SongRowFacts.ParseArrangementBadges(ArrangementSummary);
        ArrangementChips = SongRowFacts.Chips(ArrangementBadges);
        ArrangementInitials = ArrangementChips.Select(c => c.Initial).ToArray();
        // An absent number prints NOTHING, never 0 (house rule — a printed 0
        // would be a claim the data never made): 0 is "the manifest carried no
        // year" / "never rated", not 1970-adjacent or trivially easy.
        YearLabel = song.Year > 0 ? song.Year.ToString(CultureInfo.InvariantCulture) : "";
        DifficultyLabel = song.Difficulty > 0 ? song.Difficulty.ToString("0.0", CultureInfo.InvariantCulture) : "";

        HasCoverImageEntry = !string.IsNullOrWhiteSpace(song.CoverImageEntryPath);
        HasCoverSource = HasCoverImageEntry || !string.IsNullOrWhiteSpace(song.CoverImagePath);
        IsLesson = song.SongKey.StartsWith("GE_", StringComparison.OrdinalIgnoreCase);
    }

    public LibrarySongEntry Song { get; }
    public string Album => Song.Album;

    /// <summary>PROJECTIONS ARE COMPUTED ONCE, in the constructor.
    /// <para>
    ///     <see cref="LibrarySongEntry" /> is init-only, so every one of these is a
    ///     pure function of data that cannot change — so none of them is
    ///     an expression-bodied property (re-evaluated on EVERY binding read).
    ///     <c>LibrarySongEntry.ArrangementSummary</c> alone is a
    ///     <c>Select</c>+<c>Distinct</c>+<c>string.Join</c>, and
    ///     <see cref="ArrangementChips" /> would return a FRESH list every get, which
    ///     would make the row's chips <c>ItemsControl</c> throw its children
    ///     away and rebuild them on every re-bind. The list re-realizes ~15 rows
    ///     on every filter Reset, so this is per-keystroke work.
    /// </para></summary>
    public string ArrangementSummary { get; }

    public string Artist => Song.Artist;

    /// <summary>"artist title album charter", the one string free-text search
    /// matches against — built ONCE, per the projection rule above. Every field
    /// folded in comes from the init-only <see cref="LibrarySongEntry" />, so it
    /// cannot go stale; the alternative was interpolating a fresh combined string
    /// (and a fresh token array) for every one of the owner's 2,160 rows on every
    /// keystroke.
    /// <para>The CHARTER is in it for the same reason Play Mode's
    /// <c>PlaySongItem.SearchHaystack</c> has it — a real library holds several
    /// charters' cuts of one song. It was added THERE only, so typing a charter's
    /// name found their whole catalogue in Play Mode's Song Library and nothing at
    /// all in this browser, over one library. Both predicates require EVERY token
    /// to hit the haystack, so the omission was a hard exclusion, not a ranking
    /// difference. It goes LAST, like Play Mode's.</para></summary>
    /// <para>FOLDED (<see cref="LibraryQuery.Fold" />), like Play Mode's, so
    /// "Motorhead" finds "Mot&#246;rhead" here too. Both sides of the compare have to
    /// move together — see <see cref="LibraryQuery.Matches" />.</para>
    public string SearchHaystack =>
        _searchHaystack ??= LibraryQuery.Fold($"{Artist} {Title} {Album} {Song.Charter}");

    private string? _searchHaystack;

    public bool CanLoad { get; }

    /// <summary>Why the song cannot play here, beyond what the file holds; see the constructor.</summary>
    public LibrarySongBlock Block { get; }

    /// <summary>The Library row's tag for a <c>.dcsong</c> ("DotCharter song"); empty for a Rocksmith archive.</summary>
    public string SourceTag => Song.SourceKind == LibrarySourceKind.DcSong ? Localization["Library_Row_SourceDcSong"] : "";

    public string FileName { get; }
    public string Subtitle { get; }
    public string Title => Song.Title;

    public string SongLengthFormatted { get; }
    public string TuningSummary => Song.TuningSummary;

    /// <summary>Tuning as the list shows it: the common-tuning NAME with the
    /// note letters beside it ("Drop C (C G C F A D)"), or the bare letters
    /// when it has no recognised name. The filter still matches
    /// <see cref="TuningSummary" />, which stays raw.</summary>
    public string TuningDisplay { get; }

    public string CapoSummary { get; }
    public bool HasCapo { get; }

    public bool HasGroupHeader => GroupHeader.Length > 0;

    /// <summary>The row's ONE fixed-width tuning cell — tuning WITH its capo, the
    /// identical projection Play Mode's library prints, so one song reads the same
    /// way whichever library you opened.</summary>
    public string TuningCellLabel { get; }

    /// <summary>Amber-worthy: a non-standard tuning or a capo — i.e. something
    /// has to happen to the guitar before this chart is playable.</summary>
    public bool TuningNeedsAttention { get; }

    /// <summary>The tuning name alone ("Drop C"), for the detail pane's chip.
    /// The raw letters stay the tooltip.</summary>
    public string TuningName { get; }

    /// <summary>One single-letter chip per arrangement (`L R B V`) — the row's
    /// PATHS column. Four word-chips are ~200 px on every row of a 2,000-song
    /// list and push the tuning column out of alignment.
    /// <para>ONE instance per song, for the lifetime of the row (projections are
    /// computed once — see the class note). The ROW does not bind this
    /// directly: it draws <see cref="ArrangementInitials" /> through the
    /// single-element <c>FireChipStrip</c> (a per-chip
    /// <c>ItemsControl</c> would rebuild its children on every recycled re-bind);
    /// this remains the tested projection the initials derive from.</para></summary>
    public IReadOnlyList<ArrangementChip> ArrangementChips { get; }

    /// <summary>The distinct path labels the chips are drawn from, parsed once;
    /// the PATHS facet matches and counts them (<see cref="LibraryQuery" />).</summary>
    public IReadOnlyList<string> ArrangementBadges { get; }

    /// <summary>The chips' bare initials, for the row's drawn
    /// <c>FireChipStrip</c> — one string per chip, same order as
    /// <see cref="ArrangementChips" />, one instance for the row's lifetime.
    /// The full words live on the strip's tooltip via
    /// <see cref="ArrangementSummary" />.</summary>
    public IReadOnlyList<string> ArrangementInitials { get; }

    /// <summary>The row's YEAR column — the release year, or empty when the
    /// manifest carried none (0). Ctor-computed like every row projection.</summary>
    public string YearLabel { get; }

    /// <summary>The row's DIFF column — the cached 1–10 chart rating
    /// (<c>ChartDifficultyEstimator</c> via the library index; written the first
    /// time a song is loaded), or empty while never rated (0). Same figure Play
    /// Mode's difficulty chip prints for unplayed songs.</summary>
    public string DifficultyLabel { get; }

    /// <summary>The row's STATUS column: whether this archive can be loaded at
    /// all. "No audio" is the one real failure a scan can report per song.</summary>
    public string StatusLabel => Localization[
        CanLoad ? "SongBrowser_Status_Loadable"
        : Block == LibrarySongBlock.AudioDecoderMissing ? "SongBrowser_Status_NeedsDecoder"
        : "SongBrowser_Status_NoAudio"];
    public bool HasCoverImageEntry { get; }

    /// <summary>Where covers are read from when a caller passes no
    /// <see cref="IArtworkSource" />: the extracted-cover files on disk.</summary>
    private static readonly IArtworkSource DiskArtwork = new ArtworkSource();
    public bool HasCoverSource { get; }

    /// <summary>Lesson/exercise entry (GE_* song keys) a stock
    /// package can carry — not an actual song.</summary>
    public bool IsLesson { get; }

    /// <param name="libraryService">Resolves in-archive covers to a cached file.</param>
    /// <param name="dwell">Test seam for the dwell gate; production passes null
    /// and dwells <see cref="SongCoverCache.DwellMs" />.</param>
    /// <param name="artwork">Opens the cover file; production passes none and
    /// reads from disk.</param>
    public async Task LoadCoverImageAsync(ILibraryService libraryService, Task? dwell = null,
        IArtworkSource? artwork = null)
    {
        artwork ??= DiskArtwork;
        if (CoverImage != null || !HasCoverSource)
            return;

        Volatile.Write(ref _coverWanted, 1);

        if (Interlocked.Exchange(ref _coverLoadStarted, 1) == 1)
            return;

        var generation = Volatile.Read(ref _coverGeneration);

        Bitmap? bitmap = null;
        try
        {
            // DWELL: a container prepared by a scroll fling is gone again in a
            // frame or two. Only a row still wanted after the dwell touches the
            // disk at all — this is what keeps a fast fling from queueing a
            // decode per passed row (the felt scroll lag on a real library).
            await (dwell ?? Task.Delay(SongCoverCache.DwellMs));
            if (Volatile.Read(ref _coverWanted) == 0 ||
                Volatile.Read(ref _coverGeneration) != generation)
            {
                Interlocked.Exchange(ref _coverLoadStarted, 0); // retry on re-want
                return;
            }

            // ONE global throttle across both lists — never a decode storm.
            await SongCoverCache.DecodeThrottle.WaitAsync();
            try
            {
                if (Volatile.Read(ref _coverWanted) == 0 ||
                    Volatile.Read(ref _coverGeneration) != generation)
                {
                    Interlocked.Exchange(ref _coverLoadStarted, 0);
                    return;
                }

                // Prefer the pre-extracted cover on disk; fall back to pulling
                // the entry out of the archive. EVERY step is off the UI thread —
                // the continuations above resume on the caller's context, and the
                // two existence checks are disk reads on a library drive that may
                // be asleep. The decode is size-capped — see SongCoverImage.
                var song = Song;
                bitmap = await Task.Run(async () =>
                {
                    var coverPath = song.CoverImagePath;
                    if (string.IsNullOrWhiteSpace(coverPath) || !artwork.Exists(ArtworkKey.File(coverPath)))
                        coverPath = await libraryService.LoadCoverImageAsync(song);
                    return !string.IsNullOrWhiteSpace(coverPath) && artwork.Exists(ArtworkKey.File(coverPath))
                        ? SongCoverImage.Load(artwork, ArtworkKey.File(coverPath))
                        : null;
                });
            }
            finally
            {
                SongCoverCache.DecodeThrottle.Release();
            }
        }
        catch
        {
            // Ignore cover failures; the song remains loadable without artwork.
        }

        if (bitmap == null)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Released while we were decoding (scrolled out of view): drop the
            // bitmap instead of resurrecting a cover on a recycled item — nobody
            // would ever free it, and the list would accumulate exactly the
            // decoded bitmaps ReleaseCover is there to shed.
            if (Volatile.Read(ref _coverGeneration) != generation)
            {
                bitmap.Dispose();
                return;
            }

            CoverImage?.Dispose();
            CoverImage = bitmap;
        });
    }

    public void DisposeCoverImage()
    {
        // A parked bitmap being disposed must leave the LRU too, or the policy
        // keeps budget for (and a reference to) a dead cover. No-op when the
        // caller IS the eviction path — the policy removes before disposing.
        SongCoverCache.Shared.Unpark(this);
        Interlocked.Increment(ref _coverGeneration); // invalidate any in-flight decode
        CoverImage?.Dispose();
        CoverImage = null;
        // Reset so EnsureCoverLoaded can reload if the user scrolls back to
        // this item later. Without this, recycled list items would stay
        // imageless after their bitmap was freed.
        Interlocked.Exchange(ref _coverLoadStarted, 0);
    }

    // The library entry's own facts, under the names the shared filter and sort read.
    string ILibrarySong.PackName => FileName;
    int ILibrarySong.Year => Song.Year;
    double ILibrarySong.LengthSeconds => Song.SongLength;
    double ILibrarySong.Difficulty => Song.Difficulty;
    int? ILibrarySong.Capo => Song.CapoSummary;
    LibraryOrigin? ILibrarySong.Origin => Song.Origin;
}
