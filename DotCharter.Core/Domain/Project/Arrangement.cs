using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Domain.Project;

public enum ArrangementType
{
    Combo,
    Rhythm,
    Lead,
    Bass,
    Vocal
}

public enum ArrangementSubtype
{
    Main,
    Bonus,
    Alternate
}

public class TuningOffset
{
    public TuningOffset()
    {
    }

    public TuningOffset(int step) => Step = step;

    public int Step { get; set; }
}

public class Arrangement
{
    private long _revision;

    /// <summary>
    /// A stamp of this arrangement's content, not content itself: equal values mean
    ///     nothing in it changed in between. Every committed edit bumps it — the history's do, a
    ///     merged gesture tick, undo and redo all go through <c>ChartEditRevisions</c>, which marks
    ///     every arrangement an action can have reached — and so does
    ///     <see cref="Utilities.ChartProjectRestorer" /> restoring into it. It is never copied,
    ///     saved or compared, so a restored arrangement keeps its own count and moves on.
    ///     <c>QualityReportCopies</c> skips copying an arrangement whose stamp has not moved since
    ///     its spare copy was filled. A method, not a property, so the reflection walks over the
    ///     model's properties (snapshot coverage, undo diffs) never see it.
    /// </summary>
    public long GetRevision() => Volatile.Read(ref _revision);

    /// <summary>Records an edit to this arrangement: bumps <see cref="GetRevision" />.</summary>
    public void MarkEdited() => Interlocked.Increment(ref _revision);

    /// <summary>
    /// Where this part came from. <see cref="ChartProvenance.PlayOnly" />
    ///     makes it play-only: no exporter writes it, and a paste of its content into an
    ///     unrestricted part is refused. Saved as <c>&lt;dcArrangementProvenance&gt;</c>,
    ///     absent when unrestricted.
    /// </summary>
    public ChartProvenance Provenance { get; set; }

    public ArrangementType ArrangementType { get; set; } = ArrangementType.Lead;
    public int Capo { get; set; }
    public double CentOffset { get; set; }
    public string Name { get; set; } = "Guitar 1";
    public bool PickedBass { get; set; }
    public string StartingTone { get; set; } = "";
    public int StringCount { get; set; } = 6;
    public ArrangementSubtype Subtype { get; set; } = ArrangementSubtype.Main;
    public string[] Tuning { get; set; } = ["E", "A", "D", "G", "B", "e"];
    public string TuningType { get; set; } = "E_STANDARD";

    /// <summary>
    ///     How fast the note highway scrolls for this arrangement, in ROCKSMITH
    ///     UNITS — <b>lower is faster</b> (a CDLC manifest stores it as an int
    ///     ×10, so 13 = 1.3; DLC Builder's own tooltip reads "the lower the
    ///     value, the faster the scroll speed").
    ///     <para><c>null</c> means the chart's author expressed no preference —
    ///     which is NOT the same as 1.3, because the player's "use the chart's
    ///     speed" setting must be able to tell the two apart. The fallback to
    ///     Rocksmith's 1.3 happens at resolve time, not here.</para>
    /// </summary>
    public double? ScrollSpeed { get; set; }

    public ObservableCollection<TuningOffset> TuningPitches { get; set; } = [];
    public ObservableCollection<string> ToneNames { get; set; } = [];

    public ObservableCollection<ChartLevel> Levels { get; set; } = [];

    /// <summary>
    ///     Notation-only sidecar voices from multi-voice score imports (GP
    ///     voices 2–4). The playable chart in <see cref="Levels"/> stays
    ///     single-voice; only the sheet view renders these. Empty for every
    ///     chart without multi-voice score-import provenance.
    /// </summary>
    public ObservableCollection<NotationVoice> NotationVoices { get; set; } = [];

    public ObservableCollection<ChordTemplate> ChordTemplates { get; set; } = [];

    public ObservableCollection<EventPoint> EventPoints { get; set; } = [];
    public ObservableCollection<ToneChange> ToneChanges { get; set; } = [];
    public List<string> Phrases { get; set; } = [];

    /// <summary>
    /// Phrase iterations — each entry points at a phrase by index and carries
    /// a position. Mirrors rscharter's <c>phraseIterations</c> list. Optional;
    /// if empty the Rscp/Xml writers fall back to deriving iterations from
    /// <see cref="EventPoints"/>.
    /// </summary>
    public ObservableCollection<PhraseIteration> PhraseIterations { get; set; } = [];

    /// <summary>
    /// Optional per-phrase metadata (max difficulty / solo flag). Keyed by phrase name.
    /// Phrases not present here use defaults (MaxDifficulty=0, Solo=false).
    /// </summary>
    public Dictionary<string, PhraseInfo> PhraseInfos { get; set; } = [];

    /// <summary>
    /// Sidecar of RS-XML technique/path flags. Created lazily so existing
    /// callers that don't care about RS-XML keep behaving as before.
    /// </summary>
    public ArrangementProperties Properties { get; set; } = new();

    // Tone slot names (RS-XML toneBase, toneA-D). Empty string = unset.
    public string ToneBase { get; set; } = "";
    public string ToneA { get; set; } = "";
    public string ToneB { get; set; } = "";
    public string ToneC { get; set; } = "";
    public string ToneD { get; set; } = "";

    /// <summary>
    /// Fret-hand-mute templates — chord templates flagged as fret-hand-mute
    /// shapes (RS-XML <c>fretHandMuteTemplates</c>). Stored as their own list
    /// to keep the regular <see cref="ChordTemplates"/> set clean.
    /// </summary>
    public ObservableCollection<ChordTemplate> FretHandMuteTemplates { get; set; } = [];

    // Optional song-level metadata propagated to RS-XML when present.
    public string ArtistName { get; set; } = "";
    public string ArtistNameSort { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public int? AlbumYear { get; set; }
}
