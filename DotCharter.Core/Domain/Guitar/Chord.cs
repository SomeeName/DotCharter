using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Guitar;

public class Chord : ITimedElement
{
    public bool Accent { get; set; }
    public bool ForceNoNotes { get; set; }
    public bool Ignore { get; set; }

    /// <summary>True when <see cref="Ignore" /> was DERIVED by
    /// <c>ChartSpacingEnforcer</c> rather than set by the user, so the enforcer
    /// only ever clears a flag it set itself. See
    /// <see cref="GuitarNote.IgnoreDerived" />.</summary>
    public bool IgnoreDerived { get; set; }
    public bool PassOtherNotes { get; set; }
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;
    public bool SplitIntoNotes { get; set; }
    /// <summary>Strum direction of the chord (RS XML <c>strum</c>).
    /// <see cref="PickDirectionType.None" /> = not stated.</summary>
    public PickDirectionType Strum { get; set; } = PickDirectionType.None;

    public int TemplateId { get; set; } = -1; // Defaults to -1 to signify an untemplated custom chord

    /// <summary>
    /// Optional AUTHORED notation duration in quarter-note units for the whole
    /// strum (a GP/Songsterr beat has ONE rhythm), captured by score importers.
    /// Null = derive from sustain/onset gaps. See <see cref="GuitarNote.NotationDuration"/>.
    /// </summary>
    public Fraction? NotationDuration { get; set; }

    public ObservableCollection<ChordNote> ChordNotes { get; set; } = [];

    public FractionalPosition EndPosition => Position + GetMaxSustain();

    public double GetTimeMs(IList<BeatMarker> beats) => Position.GetPositionMs(beats);

    public bool IsFullyMuted() => ChordNotes.Count > 0 && ChordNotes.All(n => n.Mute == MuteType.Full);

    public bool IsPalmMuted() => ChordNotes.Count > 0 && ChordNotes.All(n => n.Mute == MuteType.Palm);

    public bool HasLinkNext()
    {
        var notes = ChordNotes;
        for (var i = 0; i < notes.Count; i++)
        {
            if (notes[i].LinkNext)
                return true;
        }

        return false;
    }

    public FractionalPosition GetMaxSustain()
    {
        if (ChordNotes.Count == 0) return FractionalPosition.Zero;
        // Indexed, not foreach: Collection<T>'s enumerator is a heap object, and the
        // renderers' per-frame look-back calls this for every chord it walks.
        var max = ChordNotes[0].Sustain;
        for (var i = 1; i < ChordNotes.Count; i++)
        {
            if (ChordNotes[i].Sustain > max)
                max = ChordNotes[i].Sustain;
        }

        return max;
    }

    public int? GetSlideTo()
    {
        var slides = ChordNotes.Where(n => n.SlideTo.HasValue).Select(n => n.SlideTo!.Value).ToList();
        if (slides.Count == 0) return null;
        return slides.Min();
    }

    public int GetFretForString(int stringIndex)
    {
        var note = ChordNotes.FirstOrDefault(n => n.StringIndex == stringIndex);
        return note?.Fret ?? -1;
    }

    public void ApplyTemplate(ChordTemplate template)
    {
        TemplateId = template.Id;

        var currentStrings = ChordNotes.Select(n => n.StringIndex).ToList();
        var templateStrings = template.Frets.Keys.ToList();

        foreach (var s in currentStrings.Except(templateStrings).ToList())
        {
            var noteToRemove = ChordNotes.First(n => n.StringIndex == s);
            ChordNotes.Remove(noteToRemove);
        }

        var defaultMute = ChordNotes.FirstOrDefault()?.Mute ?? MuteType.None;
        var defaultHopo = ChordNotes.FirstOrDefault()?.Hopo ?? HopoType.None;

        foreach (var s in templateStrings.Except(currentStrings).ToList())
        {
            ChordNotes.Add(new ChordNote
            {
                StringIndex = s,
                Fret = template.Frets.TryGetValue(s, out var f) ? f : 0,
                Mute = defaultMute,
                Hopo = defaultHopo
            });
        }

        foreach (var cn in ChordNotes)
        {
            if (template.Frets.TryGetValue(cn.StringIndex, out var fret))
                cn.Fret = fret;
        }
    }

    public Chord Clone()
    {
        var clone = new Chord();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(Chord other)
    {
        Position = other.Position;
        TemplateId = other.TemplateId;
        NotationDuration = other.NotationDuration;
        Strum = other.Strum;
        SplitIntoNotes = other.SplitIntoNotes;
        ForceNoNotes = other.ForceNoNotes;
        Accent = other.Accent;
        Ignore = other.Ignore;
        IgnoreDerived = other.IgnoreDerived;
        PassOtherNotes = other.PassOtherNotes;

        ChordNotes.Clear();
        foreach (var note in other.ChordNotes) ChordNotes.Add(note.Clone());
    }
}
