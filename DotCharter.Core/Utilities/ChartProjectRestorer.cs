using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Restores a <see cref="ChartProject" /> snapshot INTO an existing live
///     graph instead of swapping a fresh clone in for it — a positional
///     reconcile that copies the snapshot's field values onto the live
///     instances and only adds/removes at the tail.
///     <para>
///         This exists because undo history holds LIVE REFERENCES. An
///         <c>AddNotesAction</c> captures the <see cref="ObservableCollection{T}" />
///         it inserted into and <c>MutateNotesAction</c> captures the individual
///         <see cref="GuitarNote" /> INSTANCES it mutated. A project-level tool
///         (Refret, level generation, the tempo-map tools) must not apply its
///         result by cloning the snapshot and swapping the whole graph, which would
///         leave every earlier action on the stack pointing into an orphaned
///         graph: Ctrl+Z after such a tool would silently do nothing, and a redo of
///         the tool would throw away every edit made after it. Preserving only the
///         COLLECTIONS is not enough — that fixes add/remove but leaves every
///         mutate action detached — so the reconcile goes all the way down to
///         the leaf elements.
///     </para>
///     <para>
///         It is sound because a snapshot action always restores the state as of
///         immediately before/after itself, i.e. a state the earlier actions were
///         built against: an index-wise copy therefore puts the original
///         instances back holding their original values, and the captured
///         references are live again. The one residual gap is the TAIL — elements
///         a growing snapshot appends are freshly minted, so an action recorded
///         AFTER the tool that captured a tail element detaches if the tool is
///         undone and redone. That is strictly smaller than the whole-graph swap
///         it replaces.
///     </para>
///     <para>
///         <b>This is the same field-copy code the cloner runs.</b>
///         <see cref="ChartProjectCloner" /> is documented as the single
///         snapshot source of truth, and a second hand-written copier would
///         silently stop restoring any newly added model field — the same class
///         of bug as a `.dcproj` field with a reader but no writer. So the cloner
///         delegates here: <c>Clone</c> is "restore into a brand-new empty
///         object", the tail path, and there is exactly ONE place that knows
///         which fields a project has.
///     </para>
/// </summary>
public static class ChartProjectRestorer
{
    public static void Restore(ChartProject live, ChartProject snapshot) => Restore(live, snapshot, null);

    /// <summary>
    ///     <see cref="Restore(ChartProject, ChartProject)" />, leaving alone every arrangement of
    ///     <paramref name="live" /> that <paramref name="isCurrent" /> says already holds its
    ///     <paramref name="snapshot" /> counterpart (the live arrangement, then the snapshot's, at
    ///     the same index). Only a caller that KNOWS the two match may say so — the quality report's
    /// spare copy, by <see cref="Arrangement.GetRevision" />; everything else is restored.
    /// </summary>
    public static void Restore(ChartProject live, ChartProject snapshot, Func<Arrangement, Arrangement, bool>? isCurrent)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(snapshot);

        live.ProjectId = snapshot.ProjectId;
        live.AudioFileName = snapshot.AudioFileName;
        live.Bpm = snapshot.Bpm;
        live.TextContent = snapshot.TextContent;
        live.IsBeatMapLocked = snapshot.IsBeatMapLocked;
        live.OriginalMixMuted = snapshot.OriginalMixMuted;
        live.GuitarRolesSplit = snapshot.GuitarRolesSplit;
        live.Provenance = snapshot.Provenance;
        live.SynthPlayStyle.CopyFrom(snapshot.SynthPlayStyle);

        RestoreChartData(live.Chart, snapshot.Chart);

        Reconcile(live.Stems, snapshot.Stems, RestoreStem);
        ReconcileValues(live.ArrangementFiles, snapshot.ArrangementFiles);
        Reconcile(live.Beats, snapshot.Beats, RestoreBeat);
        Reconcile(live.Showlights, snapshot.Showlights, RestoreShowlight);
        Reconcile(live.VocalTracks, snapshot.VocalTracks, RestoreVocalTrack);
        if (isCurrent == null)
        {
            Reconcile(live.Arrangements, snapshot.Arrangements, RestoreArrangement);
        }
        else
        {
            Reconcile(live.Arrangements, snapshot.Arrangements, (t, s) =>
            {
                if (!isCurrent(t, s)) RestoreArrangement(t, s);
            });
        }

        Reconcile(live.Bookmarks, snapshot.Bookmarks, RestoreBookmark);
        Reconcile(live.Annotations, snapshot.Annotations, RestoreAnnotation);
        Reconcile(live.ToneDefinitions, snapshot.ToneDefinitions, (t, s) => t.CopyFrom(s));
        // Opaque strings, carried like ArrangementFiles. They have to survive a
        // snapshot or the very first undo-able edit after opening a newer build's
        // project would delete a newer build's data — the loss this collection
        // exists to prevent (see ChartProject.UnknownRscpNodes).
        ReconcileValues(live.UnknownRscpNodes, snapshot.UnknownRscpNodes);
        RestoreLinkGroups(live, snapshot);
    }

    /// <summary>
    ///     Link groups hold their placements' arrangements by REFERENCE, so a restore maps each
    ///     snapshot arrangement to the live one at the same index (the arrangements were just
    ///     reconciled index-wise). A placement whose arrangement is not in the snapshot project
    ///     is already dead and is not carried.
    /// </summary>
    public static void RestoreLinkGroups(ChartProject live, ChartProject snapshot)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(snapshot);
        Reconcile(live.LinkGroups, snapshot.LinkGroups, (t, s) =>
        {
            t.Id = s.Id;
            t.Letter = s.Letter;
            t.Name = s.Name;
            t.Rule = s.Rule;
            var placements = new List<LinkPlacement>(s.Placements.Count);
            foreach (var placement in s.Placements)
            {
                var index = placement.Arrangement == null ? -1 : snapshot.Arrangements.IndexOf(placement.Arrangement);
                if (index < 0 || index >= live.Arrangements.Count) continue;
                placements.Add(new LinkPlacement
                {
                    Arrangement = live.Arrangements[index],
                    Position = placement.Position,
                    EndPosition = placement.EndPosition
                });
            }

            Reconcile(t.Placements, placements, (lp, sp) => lp.CopyFrom(sp));
        });
    }

    public static void RestoreArrangement(Arrangement live, Arrangement snapshot)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(snapshot);

        // Whatever restores into an arrangement edits it: a project tool applies its
        // result through here, inside the history or not.
        live.MarkEdited();
        live.Provenance = snapshot.Provenance;
        live.ArrangementType = snapshot.ArrangementType;
        live.Capo = snapshot.Capo;
        live.CentOffset = snapshot.CentOffset;
        live.Name = snapshot.Name;
        live.PickedBass = snapshot.PickedBass;
        live.StartingTone = snapshot.StartingTone;
        live.StringCount = snapshot.StringCount;
        live.Subtype = snapshot.Subtype;
        live.Tuning = snapshot.Tuning.ToArray();
        live.TuningType = snapshot.TuningType;
        live.ScrollSpeed = snapshot.ScrollSpeed;
        // A fresh instance rather than a field-wise copy: ArrangementProperties
        // clones memberwise, so it picks up new flags automatically, and it is a
        // leaf sidecar no history action ever captures.
        live.Properties = snapshot.Properties.Clone();
        live.ToneBase = snapshot.ToneBase;
        live.ToneA = snapshot.ToneA;
        live.ToneB = snapshot.ToneB;
        live.ToneC = snapshot.ToneC;
        live.ToneD = snapshot.ToneD;
        live.ArtistName = snapshot.ArtistName;
        live.ArtistNameSort = snapshot.ArtistNameSort;
        live.AlbumName = snapshot.AlbumName;
        live.AlbumYear = snapshot.AlbumYear;

        Reconcile(live.TuningPitches, snapshot.TuningPitches, (t, s) => t.Step = s.Step);
        ReconcileValues(live.ToneNames, snapshot.ToneNames);
        Reconcile(live.Levels, snapshot.Levels, RestoreLevel);
        Reconcile(live.NotationVoices, snapshot.NotationVoices, RestoreNotationVoice);
        Reconcile(live.ChordTemplates, snapshot.ChordTemplates, RestoreChordTemplate);
        Reconcile(live.FretHandMuteTemplates, snapshot.FretHandMuteTemplates, RestoreChordTemplate);
        Reconcile(live.EventPoints, snapshot.EventPoints, RestoreEventPoint);
        Reconcile(live.ToneChanges, snapshot.ToneChanges, RestoreToneChange);
        ReconcileValues(live.Phrases, snapshot.Phrases);
        Reconcile(live.PhraseIterations, snapshot.PhraseIterations, (t, s) =>
        {
            t.Position = s.Position;
            t.PhraseId = s.PhraseId;
        });

        live.PhraseInfos.Clear();
        foreach (var (phraseName, info) in snapshot.PhraseInfos)
            live.PhraseInfos[phraseName] = new PhraseInfo(info.MaxDifficulty, info.Solo);
    }

    public static void RestoreLevel(ChartLevel live, ChartLevel snapshot)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(snapshot);

        live.DifficultyIndex = snapshot.DifficultyIndex;

        Reconcile(live.Notes, snapshot.Notes, (t, s) => t.CopyFrom(s));
        Reconcile(live.Chords, snapshot.Chords, (t, s) => t.CopyFrom(s));
        Reconcile(live.Anchors, snapshot.Anchors, RestoreAnchor);
        Reconcile(live.HandShapes, snapshot.HandShapes, (t, s) => t.CopyFrom(s));
        Reconcile(live.FretHandMutes, snapshot.FretHandMutes, (t, s) => t.Position = s.Position);
    }

    private static void RestoreNotationVoice(NotationVoice live, NotationVoice snapshot)
    {
        live.VoiceIndex = snapshot.VoiceIndex;

        // Deliberately NOT NotationVoice.CopyFrom: that rebuilds both
        // collections from clones, and the sheet editor records ordinary
        // MutateNotesAction/MutateChordsAction over these sidecars.
        Reconcile(live.Notes, snapshot.Notes, (t, s) => t.CopyFrom(s));
        Reconcile(live.Chords, snapshot.Chords, (t, s) => t.CopyFrom(s));
    }

    private static void RestoreChordTemplate(ChordTemplate live, ChordTemplate snapshot)
    {
        live.Id = snapshot.Id;
        live.Name = snapshot.Name;
        live.Arpeggio = snapshot.Arpeggio;
        live.ForceArpeggioInRs = snapshot.ForceArpeggioInRs;
        live.NopInRs = snapshot.NopInRs;
        live.FollowedFingering = snapshot.FollowedFingering;
        live.Fingerless = snapshot.Fingerless;
        live.GhostMask = snapshot.GhostMask;
        live.Pinned = snapshot.Pinned;
        live.Frets = new Dictionary<int, int>(snapshot.Frets);
        live.Fingers = new Dictionary<int, int>(snapshot.Fingers);
    }

    private static void RestoreChartData(ChartData live, ChartData snapshot)
    {
        live.AlbumName = snapshot.AlbumName;
        live.AlbumYear = snapshot.AlbumYear;
        live.ArrangementId = snapshot.ArrangementId;
        live.Artist = snapshot.Artist;
        live.ArtistSort = snapshot.ArtistSort;
        live.JapaneseTitle = snapshot.JapaneseTitle;
        live.JapaneseArtist = snapshot.JapaneseArtist;
        live.BeatsPerMinute = snapshot.BeatsPerMinute;
        live.ChartFormatVersion = snapshot.ChartFormatVersion;
        live.EditMode = snapshot.EditMode;
        live.Level = snapshot.Level;
        live.MainAudioOffsetAdded = snapshot.MainAudioOffsetAdded;
        live.BarsAdded = snapshot.BarsAdded;
        live.SelectedStem = snapshot.SelectedStem;
        live.SongName = snapshot.SongName;
        live.Charter = snapshot.Charter;
        live.Genre = snapshot.Genre;
        live.ReleaseDate = snapshot.ReleaseDate;
        live.Version = snapshot.Version;
        live.CoverArtPath = snapshot.CoverArtPath;
        live.Time = snapshot.Time;
        live.KeyAccidentals = snapshot.KeyAccidentals;
        live.KeyIsMinor = snapshot.KeyIsMinor;
        // No tuning here — see the note on ChartData. The arrangement's tuning is
        // restored by RestoreArrangement, which is the only copy there is.

        Reconcile(live.SingleNotes, snapshot.SingleNotes, (t, s) => t.CopyFrom(s));
        Reconcile(live.Chords, snapshot.Chords, (t, s) => t.CopyFrom(s));
    }

    private static void RestoreStem(Stem live, Stem snapshot)
    {
        live.Local = snapshot.Local;
        live.Name = snapshot.Name;
        live.Offset = snapshot.Offset;
        live.Path = snapshot.Path;
        live.Volume = snapshot.Volume;
        live.Muted = snapshot.Muted;
        live.Solo = snapshot.Solo;
    }

    private static void RestoreBeat(BeatMarker live, BeatMarker snapshot) => live.CopyFrom(snapshot);

    private static void RestoreShowlight(Showlight live, Showlight snapshot)
    {
        live.Position = snapshot.Position;
        live.Type = snapshot.Type;
        live.Note = snapshot.Note;
    }

    private static void RestoreVocalTrack(VocalTrack live, VocalTrack snapshot)
    {
        live.ColorA = snapshot.ColorA;
        live.ColorB = snapshot.ColorB;
        live.ColorG = snapshot.ColorG;
        live.ColorR = snapshot.ColorR;
        live.Name = snapshot.Name;

        Reconcile(live.Phrases, snapshot.Phrases, RestoreLyricPhrase);
    }

    private static void RestoreLyricPhrase(LyricPhrase live, LyricPhrase snapshot)
    {
        live.EndPosition = snapshot.EndPosition;
        live.Position = snapshot.Position;

        Reconcile(live.Lyrics, snapshot.Lyrics, RestoreLyricEvent);
    }

    private static void RestoreLyricEvent(LyricEvent live, LyricEvent snapshot)
    {
        live.EndPosition = snapshot.EndPosition;
        live.IsWordPart = snapshot.IsWordPart;
        live.IsPhraseEnd = snapshot.IsPhraseEnd;
        live.Note = snapshot.Note;
        live.Position = snapshot.Position;
        live.Text = snapshot.Text;
    }

    private static void RestoreEventPoint(EventPoint live, EventPoint snapshot)
    {
        live.Phrase = snapshot.Phrase;
        live.Position = snapshot.Position;
        live.Section = snapshot.Section;
        live.Events = snapshot.Events;
        live.Status = snapshot.Status;
    }

    private static void RestoreToneChange(ToneChange live, ToneChange snapshot)
    {
        live.Position = snapshot.Position;
        live.ToneName = snapshot.ToneName;
    }

    private static void RestoreBookmark(Bookmark live, Bookmark snapshot)
    {
        live.Index = snapshot.Index;
        live.Time = snapshot.Time;
    }

    private static void RestoreAnnotation(ChartAnnotation live, ChartAnnotation snapshot)
    {
        live.TimeMs = snapshot.TimeMs;
        live.Kind = snapshot.Kind;
        live.Text = snapshot.Text;
        live.Resolved = snapshot.Resolved;
    }

    private static void RestoreAnchor(FretAnchor live, FretAnchor snapshot)
    {
        live.Position = snapshot.Position;
        live.Fret = snapshot.Fret;
        live.Width = snapshot.Width;
    }

    /// <summary>
    ///     Index-wise field copy onto the existing instances, then add/remove at
    ///     the tail — the whole point is that <paramref name="live" /> keeps both
    ///     its collection instance and, for the overlapping prefix, its element
    ///     instances, so history actions that captured either stay attached.
    /// </summary>
    private static void Reconcile<T>(IList<T> live, IList<T> snapshot, Action<T, T> restoreInto)
        where T : class, new()
    {
        var shared = Math.Min(live.Count, snapshot.Count);
        for (var i = 0; i < shared; i++) restoreInto(live[i], snapshot[i]);

        for (var i = live.Count - 1; i >= snapshot.Count; i--) live.RemoveAt(i);

        for (var i = live.Count; i < snapshot.Count; i++)
        {
            var item = new T();
            restoreInto(item, snapshot[i]);
            live.Add(item);
        }
    }

    /// <summary>Same shape for immutable element types (strings), where there is
    /// nothing to copy INTO — assigning the slot is the whole restore.</summary>
    private static void ReconcileValues<T>(IList<T> live, IList<T> snapshot)
    {
        var shared = Math.Min(live.Count, snapshot.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(live[i], snapshot[i]))
                live[i] = snapshot[i];
        }

        for (var i = live.Count - 1; i >= snapshot.Count; i--) live.RemoveAt(i);

        for (var i = live.Count; i < snapshot.Count; i++) live.Add(snapshot[i]);
    }
}
