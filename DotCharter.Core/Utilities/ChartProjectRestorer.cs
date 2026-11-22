using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Restores a <see cref="ChartProject" /> snapshot INTO an existing live
///     graph instead of swapping a fresh clone in for it. Identity-retaining snapshots
///     retain the identities belonging to the live collections, including removed
///     elements; unrelated graphs use a positional field copy.
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
///         Clone-related collections share identity tokens through
///         <see cref="ChartSnapshotIdentities" />. A shrinking snapshot can detach an
///         element, but restoring its counterpart reattaches that same instance.
///         New elements are minted once per live collection and reused on redo.
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
        live.NewerWriterVersion = snapshot.NewerWriterVersion;
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
        ReconcileValues(live.GpSourceBars, snapshot.GpSourceBars.Select(bar => bar with { }).ToList());
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
        RestoreLinkGroups(live.LinkGroups, snapshot.LinkGroups);
        for (var g = 0; g < live.LinkGroups.Count; g++)
        {
            var group = live.LinkGroups[g];
            var source = snapshot.LinkGroups[g];
            for (var i = group.Placements.Count - 1; i >= 0; i--)
            {
                var arrangement = source.Placements[i].Arrangement;
                var index = arrangement == null ? -1 : snapshot.Arrangements.IndexOf(arrangement);
                if (index < 0 || index >= live.Arrangements.Count) group.Placements.RemoveAt(i);
                else group.Placements[i].Arrangement = live.Arrangements[index];
            }
        }
    }

    /// <summary>Restores group/placement owners while keeping their arrangement references live.</summary>
    public static void RestoreLinkGroups(IList<LinkGroup> live, IList<LinkGroup> snapshot)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(snapshot);
        Reconcile(live, snapshot, (t, s) =>
        {
            t.Id = s.Id;
            t.Letter = s.Letter;
            t.Name = s.Name;
            t.Rule = s.Rule;
            Reconcile(t.Placements, s.Placements, (lp, sp) => lp.CopyFrom(sp));
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

        Reconcile(live.Notes, snapshot.Notes, RestoreNote);
        Reconcile(live.Chords, snapshot.Chords, RestoreChord);
        Reconcile(live.Anchors, snapshot.Anchors, RestoreAnchor);
        Reconcile(live.HandShapes, snapshot.HandShapes, (t, s) => t.CopyFrom(s));
        Reconcile(live.FretHandMutes, snapshot.FretHandMutes, (t, s) => t.Position = s.Position);
    }

    internal static void RestoreNote(GuitarNote live, GuitarNote snapshot)
    {
        live.CopyFieldsFrom(snapshot);
        Reconcile(live.BendValues, snapshot.BendValues, RestoreBend);
    }

    internal static void RestoreChord(Chord live, Chord snapshot)
    {
        live.CopyFieldsFrom(snapshot);
        RestoreChordNotes(live, snapshot);
    }

    internal static void RestoreChordNotes(Chord live, Chord snapshot) =>
        Reconcile(live.ChordNotes, snapshot.ChordNotes, RestoreChordNote);

    internal static void RestoreChordNote(ChordNote live, ChordNote snapshot)
    {
        live.CopyFieldsFrom(snapshot);
        Reconcile(live.BendValues, snapshot.BendValues, RestoreBend);
    }

    private static void RestoreBend(BendValue live, BendValue snapshot)
    {
        live.Position = snapshot.Position;
        live.Step = snapshot.Step;
    }

    private static void RestoreNotationVoice(NotationVoice live, NotationVoice snapshot)
    {
        live.VoiceIndex = snapshot.VoiceIndex;

        // Deliberately NOT NotationVoice.CopyFrom: that rebuilds both
        // collections from clones, and the sheet editor records ordinary
        // MutateNotesAction/MutateChordsAction over these sidecars.
        Reconcile(live.Notes, snapshot.Notes, RestoreNote);
        Reconcile(live.Chords, snapshot.Chords, RestoreChord);
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
        live.Unnamed = snapshot.Unnamed;
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
        live.TitleSort = snapshot.TitleSort;
        live.AlbumSort = snapshot.AlbumSort;
        live.DlcKey = snapshot.DlcKey;
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
        live.AudioVolume = snapshot.AudioVolume;
        live.PreviewVolume = snapshot.PreviewVolume;
        live.AudioVolumeSignature = snapshot.AudioVolumeSignature;
        live.PreviewVolumeSignature = snapshot.PreviewVolumeSignature;
        live.AudioVolumeManual = snapshot.AudioVolumeManual;
        live.PreviewVolumeManual = snapshot.PreviewVolumeManual;
        live.PreviewStartMs = snapshot.PreviewStartMs;
        live.CoverArtPath = snapshot.CoverArtPath;
        live.Time = snapshot.Time;
        live.KeyAccidentals = snapshot.KeyAccidentals;
        live.KeyIsMinor = snapshot.KeyIsMinor;
        live.KeyLocked = snapshot.KeyLocked;
        // No tuning here — see the note on ChartData. The arrangement's tuning is
        // restored by RestoreArrangement, which is the only copy there is.

        Reconcile(live.SingleNotes, snapshot.SingleNotes, RestoreNote);
        Reconcile(live.Chords, snapshot.Chords, RestoreChord);
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
        live.AlignmentConfidence = snapshot.AlignmentConfidence;
        live.IsSustainShortened = snapshot.IsSustainShortened;
        live.ReviewReasons = snapshot.ReviewReasons;
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
    ///     Reattaches the original instances for clone-related collections. Unrelated
    ///     graphs retain the positional field-copy contract onto the existing prefix.
    /// </summary>
    private static void Reconcile<T>(IList<T> live, IList<T> snapshot, Action<T, T> restoreInto)
        where T : class, new()
    {
        if (ChartSnapshotIdentities.Reconcile(live, snapshot, restoreInto)) return;

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
