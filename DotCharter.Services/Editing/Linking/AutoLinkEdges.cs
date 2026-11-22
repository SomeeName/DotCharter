using DotCharter.Core.Linking;

namespace DotCharter.Services.Editing.Linking;

/// <summary>Barlines where linked runs can start and end without cutting chart data.</summary>
internal sealed class AutoLinkEdges
{
    private readonly HashSet<FractionalPosition> _unsafe = [];

    public AutoLinkEdges(Arrangement arrangement, IReadOnlyList<MeasureSpan> measures)
    {
        var edges = measures.Select(m => m.Start).Append(measures[^1].End).ToArray();
        foreach (var level in arrangement.Levels)
        {
            foreach (var shape in level.HandShapes) Block(edges, shape.Position, shape.EndPosition, false);
            BlockSounds(edges, level.Notes, level.Chords);
        }

        foreach (var voice in arrangement.NotationVoices) BlockSounds(edges, voice.Notes, voice.Chords);
    }

    public bool IsSafe(FractionalPosition edge) => !_unsafe.Contains(edge);

    private void BlockSounds(FractionalPosition[] edges, IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords)
    {
        var sounds = notes.Select(n => (n.Position, Sound: (IGuitarSound)n))
            .Concat(chords.SelectMany(c => c.ChordNotes.Select(n => (c.Position, Sound: (IGuitarSound)n))));
        foreach (var group in sounds.GroupBy(s => s.Sound.StringIndex))
        {
            FractionalPosition? next = null;
            foreach (var onset in group.GroupBy(s => s.Position).OrderByDescending(g => g.Key))
            {
                foreach (var (position, sound) in onset)
                {
                    var end = position + sound.Sustain;
                    Block(edges, position, end, false);
                    if (sound.LinkNext) Block(edges, position, next ?? edges[^1], true);
                }

                next = onset.Key;
            }
        }
    }

    private void Block(FractionalPosition[] edges, FractionalPosition start, FractionalPosition end, bool includeEnd)
    {
        var at = Array.BinarySearch(edges, start);
        var first = at < 0 ? ~at : at + 1;
        for (var i = first; i < edges.Length && (edges[i] < end || includeEnd && edges[i] == end); i++)
            _unsafe.Add(edges[i]);
    }
}
