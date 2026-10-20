using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Domain.Project;

public class ChartLevel
{
    public int DifficultyIndex { get; set; }

    public ObservableCollection<GuitarNote> Notes { get; set; } = [];
    public ObservableCollection<Chord> Chords { get; set; } = [];
    public ObservableCollection<FretAnchor> Anchors { get; set; } = [];
    public ObservableCollection<HandShape> HandShapes { get; set; } = [];

    /// <summary>Position-only fret-hand-mute markers (RS-XML <c>fretHandMute</c>).</summary>
    public ObservableCollection<FretHandMute> FretHandMutes { get; set; } = [];
}
