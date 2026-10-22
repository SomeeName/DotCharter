// PlaySongItem: one row of the Play Mode song list (library PSARC or own chart project) and everything a row prints.

using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Shared;
using DotCharter.Presentation.Shared.Library;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Library;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>One row in the Play Mode song select: a PSARC library song or
/// one of the user's own chart projects.</summary>
public sealed partial class PlaySongItem : ObservableObject, IPlayedLibrarySong
{
    private PlaySongItem(PsarcSongListItem? inner, string projectPath, bool projectFileExists,
        ILocalizationService localization)
    {
        Localization = localization;
        Inner = inner;
        ProjectPath = projectPath;

        // Everything below is computed ONCE, here — see the PROJECTIONS ARE
        // COMPUTED ONCE note on Title. Order matters: the later members read the
        // earlier ones, so this block runs top-down through the dependency chain
        // (identity → text → tuning → capo → year → arrangements → playability).
        SongIdentity = IsOwnChart ? projectPath : LibrarySongIdentity.For(inner!.Song.SongKey);

        // Every own chart is a project.dcproj: named by its folder, not "project" — and that
        // folder is "Artist - Title", so the list groups it under its artist rather than
        // "Unknown artist" with the artist folded into the title.
        var (ownArtist, ownTitle) = IsOwnChart
            ? SplitArtistTitle(DotCharter.Services.Session.ProjectDisplayName.For(projectPath))
            : ("", "");
        Title = IsOwnChart ? ownTitle : inner!.Title;
        Artist = IsOwnChart ? ownArtist : inner!.Artist;
        Album = IsOwnChart ? "" : inner!.Album;
        Charter = IsOwnChart ? "" : inner!.Song.Charter;

        // Where the PACKAGE came from. Null for own charts — not
        // "unknown provenance" but "not a Rocksmith package at all"; the filter
        // treats both the null and LibraryOrigin.Unknown as unknown data, which
        // passes.
        Origin = inner?.Song.Origin;

        // The two localized strings are resolved here rather than per read. That
        // does not change what anyone sees: nothing raised PropertyChanged for
        // Subtitle, so a language switch never re-read it either — the list is
        // rebuilt on a culture change, which is what actually re-renders it.
        Subtitle = IsOwnChart
            ? localization["PlayMode_OwnChart"]
            : string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist} | {Album}";

        TuningSummary = inner?.TuningSummary ?? "";
        TuningDisplay = SongRowFacts.TuningDisplay(TuningSummary);
        IsNonStandardTuning = SongRowFacts.IsNonStandardTuning(TuningSummary);

        Capo = inner?.Song.CapoSummary;
        HasCapo = Capo is > 0;
        CapoLabel = SongRowFacts.CapoLabel(Capo, localization);
        TuningCellLabel = SongRowFacts.TuningCell(TuningSummary, Capo, localization);
        TuningNeedsAttention = SongRowFacts.NeedsAttention(TuningSummary, Capo);

        Year = inner?.Song.Year ?? 0;
        HasYear = Year > 0;
        DetailSubtitle = HasYear && !string.IsNullOrWhiteSpace(Artist)
            ? $"{Artist} · {Year}"
            : Subtitle;

        ArrangementSummary = inner?.ArrangementSummary ?? "";
        ArrangementBadges = ParseArrangementBadges(ArrangementSummary);
        ArrangementChips = SongRowFacts.Chips(ArrangementBadges);
        ArrangementInitials = ArrangementChips.Select(c => c.Initial).ToArray();

        // An own chart has no length on record; a dash keeps the Time column's first line
        // a time, where an empty one left the difficulty under it reading as one ("5,6").
        SongLengthFormatted = inner?.SongLengthFormatted is { Length: > 0 } length ? length : "—";
        LengthSeconds = inner?.Song.SongLength ?? 0;

        // An own chart's file existence is DISK I/O, so the caller asks once, when it
        // builds the row, and hands the answer in: never a stat per binding read of
        // the play button. The answer is fixed for the row's lifetime the same way
        // the rest of these are — the list is rebuilt when the project library is
        // rescanned.
        CanPlay = IsOwnChart ? projectFileExists : inner!.CanLoad;
    }

    /// <summary>A library row over the browser's item, speaking through the item's localization.</summary>
    public static PlaySongItem FromPsarc(PsarcSongListItem inner) => new(inner, "", false, inner.Localization);

    /// <summary>An own chart's row. <paramref name="fileExists" /> is the caller's
    /// existence check of <paramref name="path" />, made once at build time; it is
    /// what <see cref="CanPlay" /> answers for the row's lifetime.</summary>
    public static PlaySongItem FromProject(string path, bool fileExists, ILocalizationService localization) =>
        new(null, path, fileExists, localization);

    /// <summary>The row's words; <see cref="SongArrangementFacts" /> reads them too.</summary>
    internal ILocalizationService Localization { get; }

    /// <summary>"Artist - Title" split on the FIRST " - " (the new-project folder
    /// convention); without one, or with an empty half, the whole name is the title.</summary>
    internal static (string Artist, string Title) SplitArtistTitle(string name)
    {
        var idx = name.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0)
        {
            var artist = name[..idx].Trim();
            var title = name[(idx + 3)..].Trim();
            if (artist.Length > 0 && title.Length > 0) return (artist, title);
        }

        return ("", name.Trim());
    }

    /// <summary>Browser item (lazy cover loading); null for own charts.</summary>
    public PsarcSongListItem? Inner { get; }

    public string ProjectPath { get; }

    public bool IsOwnChart => Inner == null;

    /// <summary>Score-store identity — matches Live Play's profile keying
    /// (project path for own charts, "lib:&lt;SongKey&gt;" for library songs).</summary>
    public string SongIdentity { get; }

    /// <summary>PROJECTIONS ARE COMPUTED ONCE, in the constructor.
    /// <para>
    ///     Both of a row's inputs are fixed for its lifetime — a library entry
    ///     (<c>LibrarySongEntry</c>) is init-only, an own chart's project path never
    ///     moves — so every member
    ///     from here to <see cref="CanPlay" /> is a pure function of data that
    ///     cannot change. None is expression-bodied (re-evaluated on
    ///     EVERY binding read), and the Song Library re-binds all ~15 realized rows
    ///     on every filter pass, so that would be per-keystroke work.
    ///     <see cref="ArrangementChips" /> is the load-bearing one (see its own
    ///     note) and <see cref="CanPlay" /> was the expensive one — it hit the
    ///     disk. The MUTABLE half of the row (stats, difficulty, update/download
    ///     state, group headers) is <c>[ObservableProperty]</c> below and is
    ///     deliberately untouched by this.
    /// </para></summary>
    public string Title { get; }

    /// <summary>Artist with the SAME own-chart fallback as <see cref="Title"/>.
    /// Empty (never null) for own charts. Filtering/sorting must read this, never <c>Inner.Artist</c>.</summary>
    public string Artist { get; }

    /// <summary>What a screen reader announces for the row (Avalonia names a list item by its
    /// data's ToString, which was the type name).</summary>
    public override string ToString() => Artist.Length == 0 ? Title : $"{Title}, {Artist}";

    /// <summary>Album with the same fallback as <see cref="Artist"/>.</summary>
    public string Album { get; }

    /// <summary>The package author (a CDLC's toolkit.version "Package Author"),
    /// with the same own-chart fallback as <see cref="Artist" />: an own chart has
    /// none. Empty, never null.
    /// <para>Searchable — see <see cref="SearchHaystack" />. It is the one field
    /// that answers "who charted this", which is how a player picks between two
    /// cuts of the same song.</para></summary>
    public string Charter { get; }

    /// <summary>Whether the underlying library entry is stock or custom
    /// — straight off <c>LibrarySongEntry.Origin</c>. NULL for
    /// the player's own charts, which are not library packages at all; the provenance filter treats that (and
    /// <c>LibraryOrigin.Unknown</c>) as unknown data, which always passes.</summary>
    public DotCharter.Services.Library.LibraryOrigin? Origin { get; }

    /// <summary>Authored genre, or null when unknown — which is every song that
    /// is NOT one of the player's own charts. Rocksmith packages carry no genre
    /// (neither <c>LibrarySongEntry</c> nor the RS2014 manifest has the field), so
    /// the only real source is an own
    /// chart's <c>dcGenre</c> header, read once per library scan by
    /// <c>ChartGenreIndex</c> and stamped on here by <c>RebuildFromBrowser</c>.
    /// <para>Consumed by the song browser's genre facet. Unknown is a real
    /// answer, never an exclusion.</para></summary>
    public string? Genre { get; private set; }

    /// <summary>Records the scan's answer for this song. Blank reads as unknown,
    /// so a chart whose metadata dialog was opened and left empty is the same as
    /// one that never had the field.</summary>
    internal void ApplyGenre(string? genre)
        => Genre = string.IsNullOrWhiteSpace(genre) ? null : genre.Trim();

    /// <summary>The list row's second line. Resolved once — the localized own-chart
    /// wording included; see the ctor for why that changes nothing a
    /// user can observe.</summary>
    public string Subtitle { get; }

    /// <summary>"artist title album charter", the one string free-text search
    /// matches against — built ONCE. Every field folded in is itself fixed for the
    /// row's lifetime (a library entry is init-only, an own chart's title is its
    /// file name), so interpolating it afresh for every song on
    /// every keystroke would be wasted work.
    /// <para>The CHARTER is in it because a real library holds several charters'
    /// cuts of the same song, and their names are printed nowhere the old
    /// haystack could reach — typing one found nothing. It goes LAST so the
    /// order of the three metadata fields is untouched (the search is tokenized,
    /// so position does not change a match, but the order is what the test
    /// asserts and what the "artist - title" case reads as).</para></summary>
    /// <para>FOLDED (<see cref="LibraryQuery.Fold" />): lower-cased and
    /// stripped of combining marks, so the predicate's compare is a plain
    /// ordinal <c>Contains</c> and "Motorhead" reaches "Motörhead". Anything
    /// tested against this string must be folded too.</para>
    public string SearchHaystack =>
        _searchHaystack ??= LibraryQuery.Fold($"{Artist} {Title} {Album} {Charter}");

    private string? _searchHaystack;

    public string TuningSummary { get; }

    /// <summary>The tuning shown on the Song Select card chip: the canonical
    /// common-tuning NAME ("E Standard", "Drop C", "DADGAD") when
    /// <see cref="TuningSummary" /> is a recognised tuning, else the raw note
    /// letters. The full letters stay available for the chip's tooltip via
    /// <see cref="TuningSummary" />. DISPLAY-ONLY: the tuning FILTER still matches on
    /// the raw <see cref="TuningSummary" />, so the LibraryQuery purity invariant
    /// (reads PlaySongItem, never Inner) is untouched.</summary>
    public string TuningDisplay { get; }

    /// <summary>The library row's ONE fixed-width tuning cell: the tuning name
    /// with the capo folded in ("E STANDARD · CAPO 2"). Tuning and capo answer
    /// the same question — "what do I have to do to the guitar before I press
    /// play" — so they share a cell rather than a column each, which is what
    /// keeps the column alignment down the whole list.</summary>
    public string TuningCellLabel { get; }

    /// <summary>The tuning is not plain E standard. UNKNOWN (own charts — an
    /// empty summary) is NOT "non-standard": the unknown-data policy
    /// says a value we cannot judge never raises an alarm.</summary>
    public bool IsNonStandardTuning { get; }

    /// <summary>Amber-worthy: a non-standard tuning or a capo. Colour is
    /// semantic on the Fire layer — amber means "this needs your attention
    /// before you can play it", which is exactly what a retune or a capo is.</summary>
    public bool TuningNeedsAttention { get; }

    /// <summary>Release year from the library index (0 = unknown). Present on
    /// <c>LibrarySongEntry</c> all along; §8 lists it as "exists but not
    /// surfaced", and this is where it surfaces.</summary>
    public int Year { get; }

    public bool HasYear { get; }

    /// <summary>Detail-rail identity line: "3 Doors Down · 2002" when the year
    /// is known, else the list subtitle.</summary>
    public string DetailSubtitle { get; }

    public string ArrangementSummary { get; }

    /// <summary>The song's arrangements as individual badge labels (Song Select
    /// card chrome) — a PURE projection of <see cref="ArrangementSummary" /> that
    /// replaces the old copy-paste "Lead, Rhythm, Bass, Vocals" text line with one
    /// chip per arrangement the song actually HAS. Reads no other state (the
    /// LibraryQuery purity invariant is unaffected — this is display-only).
    /// Parsed ONCE, in the ctor, by the tested <see cref="ParseArrangementBadges" />.</summary>
    public IReadOnlyList<string> ArrangementBadges { get; }

    /// <summary>Splits a comma-joined arrangement summary into its distinct,
    /// order-preserving, trimmed labels. Empty in ⇒ empty out. Public + static so
    /// it is unit-tested without constructing a <see cref="PlaySongItem" />.</summary>
    public static IReadOnlyList<string> ParseArrangementBadges(string? summary)
        => SongRowFacts.ParseArrangementBadges(summary);

    /// <summary>The LIST-ROW spelling of <see cref="ArrangementBadges" />: one
    /// single-letter chip per arrangement, with the full word kept as the
    /// tooltip. The mockup draws `L R B V` and it is not decoration — four
    /// word-chips are ~200 px on every row of a 2,000-song list, which pushes
    /// the tuning and score columns out of alignment, and column alignment is
    /// this screen's stated acceptance criterion. The detail rail still spells
    /// the paths out in full, where there is room for it.
    /// <para>ONE instance per song, for the lifetime of the row: the chips
    /// <c>ItemsControl</c> compares <c>ItemsSource</c> by reference, so a fresh
    /// list per get made every recycled row throw its chip children away and
    /// rebuild them on every re-bind — and the Song Library re-binds its realized
    /// rows on every filter pass. The identical fix on the sibling
    /// <see cref="PsarcSongListItem.ArrangementChips" /> is the worked
    /// example.</para></summary>
    public IReadOnlyList<ArrangementChip> ArrangementChips { get; }

    /// <summary>The chips' bare initials, for the row's drawn
    /// <c>FireChipStrip</c> — one string per chip, same order as
    /// <see cref="ArrangementChips" />, one instance for the row's lifetime.
    /// The full words live on the strip's tooltip via
    /// <see cref="ArrangementSummary" />.</summary>
    public IReadOnlyList<string> ArrangementInitials { get; }

    /// <summary>Projects each parsed badge to (initial, full label). Pure and
    /// static so it is unit-tested without constructing a
    /// <see cref="PlaySongItem" />. The initial is taken from the label the
    /// CATALOGUE carries (Rocksmith's own path names are English), so it does
    /// not move with the UI language.</summary>
    public static IReadOnlyList<ArrangementChip> BuildArrangementChips(string? summary)
        => SongRowFacts.Chips(summary);

    public string SongLengthFormatted { get; }

    /// <summary>Song duration in seconds for the length filter — from the library
    /// entry. 0 (unknown) for own charts, which the
    /// filter treats as "always passes".</summary>
    public double LengthSeconds { get; }

    /// <summary>Capo fret, or null when unknown (own charts). The length/tuning/capo filters all treat an
    /// unknown value as "passes", so a capo filter never hides a song whose capo
    /// we can't know.</summary>
    public int? Capo { get; }

    /// <summary>Whether a capo chip should show on the Song Select card — a known,
    /// non-zero capo. Pure projection of <see cref="Capo" /> (display-only).</summary>
    public bool HasCapo { get; }

    /// <summary>Compact capo label for the Song Select card chip (e.g. "Capo 2");
    /// empty when no/unknown capo.</summary>
    public string CapoLabel { get; }

    /// <summary>Whether pressing play on this row can do anything. Answered ONCE,
    /// in the ctor — an own chart's answer is the existence check its builder made
    /// through Play Mode's file seam, never a stat per binding read.</summary>
    public bool CanPlay { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRecord))]
    [NotifyPropertyChangedFor(nameof(RecordEmptyLabel))]
    private bool _hasBest;

    private bool _hasMeasuredRun;

    /// <summary>
    ///     Whether the row prints a record (score, grade, accuracy, mastery). A stored
    ///     run with no score and no measured accuracy — a drill that ended at once —
    ///     printed "0 – –" over an empty meter, which reads as broken; it says
    ///     <see cref="RecordEmptyLabel" /> instead. Filters keep reading <see cref="HasBest" />.
    /// </summary>
    public bool ShowsRecord => HasBest && _hasMeasuredRun;

    /// <summary>"Unplayed", or "No full run yet" when a run is stored but measured nothing.</summary>
    public string RecordEmptyLabel => Localization[HasBest ? "PlayMode_SongSelect_NoFullRun" : "PlayMode_SongSelect_Unplayed"];
    [ObservableProperty] private long _bestScore;
    [ObservableProperty] private string _bestLabel = "";
    [ObservableProperty] private string _gradeLabel = "";

    /// <summary>The RAW best grade behind <see cref="GradeLabel" />
    /// (<c>SongPlayStats.Grade</c>). Carried as the enum, not parsed back out of
    /// the label, because the label is a display string: the record filter needs
    /// to ORDER grades ("A or better"), and a letter is not an order.
    /// <para>Meaningless unless <see cref="HasBest" /> is true — an unplayed song
    /// reads <c>LetterGrade.F</c> simply because that is the enum's zero. Every
    /// reader must gate on <see cref="HasBest" /> first, which is exactly what
    /// <c>LibraryQuery</c> does.</para></summary>
    [ObservableProperty] private LetterGrade _grade;
    [ObservableProperty] private DateTime _lastPlayedUtc;

    /// <summary>Sticky "ever full-comboed" flag (<c>SongPlayStats.FullCombo</c>) —
    /// drives the small FC badge on the row, shown only when earned.</summary>
    [ObservableProperty] private bool _isFullCombo;

    /// <summary>Computed 1–10 chart rating for this song
    /// (<c>ChartDifficultyEstimator</c>): the played arrangement's rating stamped
    /// when a run records, else the hardest-arrangement rating the library index
    /// cached the first time the song was loaded; 0 = never rated. Feeds the
    /// song-select difficulty sort, range filter and the row chip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDifficulty))]
    [NotifyPropertyChangedFor(nameof(DifficultyLabel))]
    private double _difficulty;

    public bool HasDifficulty => Difficulty > 0;

    /// <summary>One-decimal rating for the row chip (e.g. "6.8"); empty when unrated.</summary>
    public string DifficultyLabel => Difficulty > 0 ? Difficulty.ToString("0.0") : "";

    /// <summary>Artist header stamped on the FIRST row of each artist run by
    /// <see cref="SongArtistGrouping.Stamp" /> (empty on every other row and on
    /// every row of a list not sorted by artist).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupHeader))]
    private string _groupHeader = "";

    /// <inheritdoc cref="GroupHeader" />
    [ObservableProperty] private string _groupCountLabel = "";

    public bool HasGroupHeader => GroupHeader.Length > 0;

    /// <summary>Best accuracy on this song, 0..1 (<c>SongPlayStats.BestAccuracy</c>
    /// — real and already computed; §8 lists it as "absent on a library row",
    /// which was true only because <see cref="ApplyStats" /> dropped it).</summary>
    [ObservableProperty] private double _bestAccuracy;

    /// <summary>Formatted best accuracy ("97.4%"); empty when unplayed.</summary>
    [ObservableProperty] private string _accuracyLabel = "";

    /// <summary>Per-song mastery as a 0..1 ratio for the meter.
    /// <c>SongPlayStats.MasteryPercent</c> is 0..100 (accuracy × coverage ×
    /// speed × 100), so this divides — handing the raw figure to a
    /// <c>ProgressBar</c> whose Maximum is 1 pins every played song at full.</summary>
    [ObservableProperty] private double _masteryFraction;

    /// <summary>Formatted mastery for the meter's caption ("70%").</summary>
    [ObservableProperty] private string _masteryLabel = "";

    /// <summary>Localized relative last-played string ("4 days ago"); empty when
    /// unplayed. The bucketing is <c>RelativeTime.Between</c> — the same one the
    /// history rows use.</summary>
    [ObservableProperty] private string _lastPlayedLabel = "";

    public void ApplyStats(SongPlayStats? stats)
    {
        _hasMeasuredRun = stats is { BestScore: > 0 } or { HasBestAccuracy: true };
        HasBest = stats != null;
        OnPropertyChanged(nameof(ShowsRecord));
        OnPropertyChanged(nameof(RecordEmptyLabel));
        BestScore = stats?.BestScore ?? -1;
        BestLabel = stats == null ? "" : stats.BestScore.ToString("N0");
        // GradeLabel, not GradeCalculator.Label(stats.Grade): the stored best
        // accuracy is coverage-gated, so a song played only in section drills
        // has sessions and a best accuracy of 0 — and 0 grades F. The row used
        // to print that F (and "0.0%") beside a real score while the Get Ready
        // screen, gating on the same store, showed nothing.
        GradeLabel = stats == null ? "" : stats.GradeLabel;
        Grade = stats?.Grade ?? default;
        IsFullCombo = stats?.FullCombo ?? false;
        LastPlayedUtc = stats?.LastPlayedUtc ?? default;
        BestAccuracy = stats?.BestAccuracy ?? 0;
        AccuracyLabel = stats switch
        {
            null => "",
            { HasBestAccuracy: false } => AccuracyMath.UnmeasuredLabel,
            _ => PercentText.Format(stats.BestAccuracy, 1)
        };
        MasteryFraction = Math.Clamp((stats?.MasteryPercent ?? 0) / 100.0, 0, 1);
        MasteryLabel = stats == null ? "" : PercentText.Format(stats.MasteryPercent / 100.0, 0);
        // The SAME relative-time vocabulary the history rows use — bucketed by
        // the pure RelativeTime.Between, localized by the one formatter.
        LastPlayedLabel = stats == null
            ? ""
            : RelativeTimeText.Format(
                RelativeTime.Between(stats.LastPlayedUtc, DateTime.UtcNow), Localization);
        // Played songs carry a per-run rating in stats; unplayed library songs
        // fall back to the deterministic rating the index cached on first load
        // (0 = never loaded → the chip/sort/filter treat it as unrated).
        Difficulty = stats?.Difficulty ?? Inner?.Song.Difficulty ?? 0;
    }

    // The facts the shared filter and sort read under another name: an own chart has no
    // package, and a lesson never reaches this list.
    string ILibrarySong.PackName => Inner?.FileName ?? "";
    bool ILibrarySong.CanLoad => CanPlay;
    bool ILibrarySong.IsLesson => Inner?.IsLesson ?? false;
}
