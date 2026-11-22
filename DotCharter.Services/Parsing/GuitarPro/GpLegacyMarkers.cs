using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Services.Parsing.GuitarPro;

internal static class GpLegacyMarkers
{
    public static void Apply(Arrangement arrangement, IReadOnlyList<GpLegacyMeasureHeader> headers,
        IReadOnlyList<GpMasterBarSequence.UnfoldedIndex> bars, IReadOnlyList<FractionalPosition> starts)
    {
        for (var i = 0; i < bars.Count; i++)
            GpArrangementParser.AddMarker(arrangement, GpMarkerImport.CreateMarkerEvent(starts[i], headers[bars[i].OriginalIndex].Marker));
    }
}
