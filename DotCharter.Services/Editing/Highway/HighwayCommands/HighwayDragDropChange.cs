using Chord = DotCharter.Core.Domain.Guitar.Chord;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     What a highway drag/drop replaces: for each element kind, the elements
///     it lands on (<c>Target*</c>) and the elements it puts down (<c>New*</c>).
/// </summary>
internal sealed record HighwayDragDropChange(
    IEnumerable<GuitarNote> TargetNotes,
    IEnumerable<GuitarNote> NewNotes,
    IEnumerable<Chord> TargetChords,
    IEnumerable<Chord> NewChords,
    IEnumerable<HandShape> TargetHandShapes,
    IEnumerable<HandShape> NewHandShapes,
    IEnumerable<FretAnchor> TargetAnchors,
    IEnumerable<FretAnchor> NewAnchors);
