using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Domain.Project;

/// <summary>
///     A NOTATION-ONLY voice from a multi-voice score import (Guitar Pro voices
///     2–4). The playable chart stays single-voice — <see cref="ChartLevel"/>
///     holds voice 1 and is what gameplay, judging, validation and Rocksmith
///     export consume. These sounds exist purely so the sheet view can render
///     the authored polyphony; they are never fed to any gameplay path.
///     <para>Per-ARRANGEMENT (not per-level) because the authored score has no
///     difficulty dimension — score imports produce exactly one level, and the
///     sheet renders the sidecar next to whichever level is active.</para>
///     <para>Serialized as the backward-tolerant root-level
///     <c>dcNotationVoices</c> rscp extension node (same pattern as
///     <c>dcAnnotations</c>); absent in every pre-voice file.</para>
/// </summary>
public class NotationVoice
{
    /// <summary>
    ///     Sidecar voice index: 1..3 correspond to Guitar Pro voices 2..4.
    ///     Voice 0 IS the playable chart and never appears here.
    /// </summary>
    public int VoiceIndex { get; set; } = 1;

    public ObservableCollection<GuitarNote> Notes { get; set; } = [];
    public ObservableCollection<Chord> Chords { get; set; } = [];

    public NotationVoice Clone()
    {
        var clone = new NotationVoice();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(NotationVoice other)
    {
        VoiceIndex = other.VoiceIndex;

        Notes.Clear();
        foreach (var note in other.Notes) Notes.Add(note.Clone());

        Chords.Clear();
        foreach (var chord in other.Chords) Chords.Add(chord.Clone());
    }
}
