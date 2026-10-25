using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>A grip low string to high (<c>-1</c> unused) with its fingers (<c>-1</c> none).</summary>
internal sealed record FakeShape(string Name, int[] Frets, int[] Fingers, bool Arpeggio = false)
{
    public ChordTemplate ToTemplate()
    {
        var template = new ChordTemplate { Name = Name, Arpeggio = Arpeggio };
        for (var s = 0; s < Frets.Length; s++)
        {
            if (Frets[s] >= 0) template.Frets[s] = Frets[s];
            if (Fingers[s] >= 0) template.Fingers[s] = Fingers[s];
        }

        return template;
    }

    public Chord ToChord()
    {
        var chord = new Chord();
        for (var s = 0; s < Frets.Length; s++)
        {
            if (Frets[s] >= 0) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = Frets[s] });
        }

        return chord;
    }
}
