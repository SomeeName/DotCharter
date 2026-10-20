using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Core.Domain.Project;

public class ChartProject
{
    /// <summary>
    ///     The project's stable identity (<see cref="ProjectIdentity" />): minted when
    ///     the project is CREATED (so a new, never-saved chart already has one), saved
    ///     as <c>&lt;dcProjectId&gt;</c> on every write, and carried by a move, a copy
    /// or a Save As. It is what the AI chat history keys a conversation by,
    ///     so two projects never see each other's chats. An older <c>.rscp</c> without
    ///     it is given one on open (<c>RscpChartSerializer.Load</c>: derived from the
    ///     file's bytes, so reopening an unsaved legacy file finds the same id), and
    ///     the next save writes it. Not chart content: nothing edits it, nothing undoes
    ///     it, and the restorer copies it only so a clone is the same project.
    /// </summary>
    public string ProjectId { get; set; } = ProjectIdentity.NewId();

    public string AudioFileName { get; set; } = "";
    public double Bpm { get; set; } = 120.0;
    public ChartData Chart { get; set; } = new();
    public string TextContent { get; set; } = "";
    public bool IsBeatMapLocked { get; set; }

    public ObservableCollection<Stem> Stems { get; set; } = [];

    /// <summary>
    ///     Whether the ORIGINAL mix is silenced while the stems play in its place.
    /// </summary>
    /// <remarks>
    ///     The other half of <see cref="Stem.Muted" />, and it has to be saved with
    ///     it. Saving only the stem half would make the mixer re-derive this one on every
    ///     load as "muted, because stems exist", so the state "I want the original,
    ///     not the stems" — which the mixer's own cross-track exclusion produces by
    ///     muting all six stems — would come back as EVERY channel muted, i.e. a project
    ///     that opens completely silent with a healthy engine and nothing in the log.
    ///     Absent from an older <c>.rscp</c>, it is derived as "muted iff any stem is
    ///     audible", which reads those files the way they were meant.
    /// </remarks>
    public bool OriginalMixMuted { get; set; } = true;

    /// <summary>
    /// Guitar roles: the stem mixer plays the guitar stem as two derived
    ///     stems, Lead (what sits in the centre) and Rhythm (what is panned to the
    ///     sides), in its place. A way of LISTENING, like the stems' own mute and
    ///     solo: the guitar stem itself is untouched and stays in <see cref="Stems" />.
    ///     Saved as <c>&lt;stems guitarRoles="true"&gt;</c>, absent when off.
    /// </summary>
    public bool GuitarRolesSplit { get; set; }

    /// <summary>
    /// Where this project's content came from. Set on import by
    ///     a source that marks its content play-only; <see cref="ChartProvenance.PlayOnly" />
    ///     makes the whole project play-only (the beats, lyrics and showlights are
    ///     the source's too). Saved as <c>&lt;dcProvenance&gt;</c>, absent when
    ///     unrestricted.
    /// </summary>
    public ChartProvenance Provenance { get; set; }
    public ObservableCollection<string> ArrangementFiles { get; set; } = [];
    public ObservableCollection<BeatMarker> Beats { get; set; } = [];
    public ObservableCollection<Showlight> Showlights { get; set; } = [];
    public ObservableCollection<VocalTrack> VocalTracks { get; set; } = [];
    public ObservableCollection<Arrangement> Arrangements { get; set; } = [];
    public ObservableCollection<Bookmark> Bookmarks { get; set; } = [];
    public ObservableCollection<ChartAnnotation> Annotations { get; set; } = [];

    /// <summary>Full Rocksmith tone definitions the project owns (authored in
    /// the tone editor or imported from an existing package), keyed by
    /// <see cref="ToneDefinition.Key"/>. Arrangements reference them by name
    /// via their tone slots / tone changes; the exporter embeds the referenced
    /// bodies into the package manifest.</summary>
    public ObservableCollection<ToneDefinition> ToneDefinitions { get; set; } = [];

    /// <summary>How the built-in chart synth plays this song: one rule set for
    /// guitar arrangements, one for bass. Saved as <c>dcSynthPlayStyle</c> only when it
    /// differs from the defaults.</summary>
    public ChartSynthPlayStyle SynthPlayStyle { get; set; } = new();

    /// <summary>
    ///     Top-level project nodes a NEWER build wrote that this build does not
    ///     understand, each as one normalized XML fragment, in the order they
    ///     appeared in the file. Carried through untouched so the next save puts
    ///     them back instead of deleting them — the forward-compatibility half of
    ///     <c>docs/RSCP-FORMAT-GUARANTEES.md</c>.
    ///     <para>
    ///         Empty for every project this build's writer produced, for every
    ///         import, and for a brand-new chart: it only ever holds bytes that
    ///         came off disk. It is deliberately NOT a general extension point —
    ///         nothing in the app may read, write or interpret its contents. The
    ///         `.rscp` reader is the only producer and the `.rscp` writer the
    ///         only consumer.
    ///     </para>
    ///     <para>
    ///         Only the ROOT's own children are captured. Unknown attributes, and
    ///         unknown nodes nested deeper (on a note, a chord, an arrangement),
    ///         are dropped — re-attaching those needs an identity the format does
    ///         not give them, and mis-attaching a newer build's hint to the wrong
    ///         note is worse than losing it. The guarantees doc states that split
    ///         as a promise; `RscpForwardCompatibilityTests` pins it.
    ///     </para>
    /// </summary>
    public ObservableCollection<string> UnknownRscpNodes { get; set; } = [];
}
