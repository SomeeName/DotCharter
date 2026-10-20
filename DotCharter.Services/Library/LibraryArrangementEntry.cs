namespace DotCharter.Services.Library;

public sealed class LibraryArrangementEntry
{
    public string ArrangementName { get; init; } = "";
    public string PersistentId { get; init; } = "";
    public string SngPath { get; init; } = "";
    public IReadOnlyDictionary<int, string> Tones { get; init; } = new Dictionary<int, string>();

    /// <summary>Base/starting tone key (manifest <c>Tone_Base</c>) — the tone
    /// active before the first tone change. Empty when the package carries
    /// none. Threaded into <c>Arrangement.ToneBase</c>/<c>StartingTone</c> so
    /// the in-game-tone coordinator can arm the intro tone.</summary>
    public string ToneBase { get; init; } = "";

    /// <summary>Rocksmith path bit mask from the manifest: 1 = Lead,
    /// 2 = Rhythm, 4 = Bass; 0 = unknown. Authoritative over the display
    /// name — the base game names arrangements of BOTH paths "Combo".</summary>
    public int RouteMask { get; init; }

    /// <summary>False marks a non-representative (alternate) arrangement.</summary>
    public bool Represent { get; init; } = true;

    /// <summary>Manifest <c>MasterID_RDV</c>. Carried so a plugin's
    /// <c>ILibraryOriginClassifier</c> can tell a stock release from a custom build
    /// without re-opening the archive. 0 when absent.</summary>
    public int MasterId { get; init; }

    /// <summary>Manifest <c>SKU</c>; null when the attribute is absent, which is
    /// NOT the same as present-but-empty.</summary>
    public string? Sku { get; init; }

    /// <summary>Bonus arrangement flag from the manifest.</summary>
    public bool BonusArr { get; init; }

    /// <summary>
    ///     How many notes this arrangement holds at its HARDEST level — the
    ///     manifest's <c>NotesHard</c> attribute, which
    ///     <c>AttributesCreation.initSongCommon</c> writes into the per-song
    ///     <c>.json</c> AND the <c>.hsan</c> header the library scan already
    ///     reads. So it is free: no SNG parse, no second archive pass.
    ///     <para>
    ///         0 = the manifest carried none (a vocals arrangement, a
    ///         hand-built package). Distinguish it from a real count before
    ///         printing one — an arrangement with genuinely zero notes does not
    ///         ship.
    ///     </para>
    ///     <para>DotCharter's own exporter writes the same attribute
    ///     (<c>RsManifestGenerator</c>), so a package this app built reads back
    ///     its real count too.</para>
    /// </summary>
    public int NoteCount { get; init; }

    /// <summary>The bass is played with a pick (manifest <c>bassPick</c>). A charter
    /// setting no note data encodes, so the importer reads it instead of guessing —
    /// it is not fabricated as "every bass arrangement".
    /// <para>An index row written by an older build has no such column and reads back
    /// <c>false</c>, which is the conservative answer; a rescan or an
    /// arrangement-manifest backfill fills it in.</para></summary>
    public bool BassPick { get; init; }

    /// <summary>Highway scroll speed the charter authored, decoded from the
    /// per-arrangement manifest's <c>DynamicVisualDensity</c> ramp (there is no
    /// ScrollSpeed attribute in a Rocksmith manifest). Rocksmith units — LOWER IS
    /// FASTER. Null when the package carries none, which is deliberately distinct
    /// from the 1.3 default.
    /// <para>A library scan alone canNOT populate this: the .hsan it reads has no
    /// density array. the package source
    /// is what fills it in.</para></summary>
    public double? ScrollSpeed { get; init; }

    /// <summary>
    ///     THIS arrangement's tuning summary ("E Standard", "Drop D", or the
    ///     raw letters), from the manifest's per-arrangement <c>Tuning</c>
    ///     block — schema v8. Rocksmith writes every tuning into six slots
    ///     regardless of instrument, so a six-slot vector is not always a
    ///     guitar: a Lead in E standard and a Bass in drop D are two DIFFERENT
    ///     tunings in one package, and the song-level
    ///     <see cref="LibrarySongEntry.TuningSummary" /> (which prefers a guitar
    ///     arrangement, see the package source) can
    ///     only carry one of them. Empty when the manifest carries none.
    /// </summary>
    public string Tuning { get; init; } = "";

    /// <summary>THIS arrangement's capo fret (manifest <c>CapoFret</c>);
    /// 0 = none. Per arrangement for the same reason as <see cref="Tuning" />.</summary>
    public int Capo { get; init; }

    /// <summary>
    /// Display label derived from <see cref="RouteMask"/> — "Combo" only when
    /// the mask really carries both guitar paths. Falls back to the raw
    /// manifest name when no mask is present (vocals, exotic CDLC).
    /// Not serialized: it is derived state, not cache payload.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PathLabel => (RouteMask & 4) != 0
        ? "Bass"
        : (RouteMask & 3) switch
        {
            3 => "Combo",
            1 => "Lead",
            2 => "Rhythm",
            _ => ArrangementName
        };
}
