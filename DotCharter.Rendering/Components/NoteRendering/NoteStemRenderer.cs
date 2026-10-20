using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

/// <summary>
///     What ties a SINGLE note to the highway floor, as in Rocksmith: a thin stem in the
///     string's color down to <see cref="FloorY" />. A fretted head stands on one stem and
///     gets a flat floor marker spanning its fret; an open note's bar gets a stem at each
///     end and no marker. Chords get neither: the game frames them with the chord box.
///     Staged BEFORE the head so the head draws over its stems.
/// </summary>
internal static class NoteStemRenderer
{
    /// <summary>The highway floor: the fret wires' bottom end.</summary>
    internal const float FloorY = FretboardConstants.BottomStringPosition - FretboardConstants.StringDistance / 2f;

    /// <summary>Half the stem's width, in world units.</summary>
    internal const float StemHalfWidth = 0.022f;

    /// <summary>Half the floor marker's depth along the time axis, in world units.</summary>
    internal const float FloorMarkerHalfDepth = 0.05f;

    /// <summary>The floor marker sits this far above the floor, over the highway's lane lines.</summary>
    internal const float FloorMarkerLift = 0.0015f;

    internal const byte StemAlpha = 210;
    internal const byte FloorMarkerAlpha = 120;

    /// <summary>The x range of the floor marker of a note on <paramref name="fret" />: the wires either side of it.</summary>
    internal static (float X0, float X1) FloorMarkerSpan(int fret)
        => (FretboardConstants.GetLinearFretPosition(fret - 1), FretboardConstants.GetLinearFretPosition(fret));

    /// <summary>Stages the stem and floor marker of a fretted head at (<paramref name="x" />, <paramref name="headY" />, <paramref name="z" />).</summary>
    internal static void DrawFretted(RenderContext context, float x, float headY, float z, int fret, Color color)
    {
        DrawStem(context, x, headY, z, color);
        var (x0, x1) = FloorMarkerSpan(fret);
        var y = FloorY + FloorMarkerLift;
        var marker = Color.FromArgb(FloorMarkerAlpha, color.R, color.G, color.B);
        context.QuadBatcher.AddQuad(
            new Vector3(x0, y, z - FloorMarkerHalfDepth), new Vector3(x1, y, z - FloorMarkerHalfDepth),
            new Vector3(x1, y, z + FloorMarkerHalfDepth), new Vector3(x0, y, z + FloorMarkerHalfDepth),
            Vector3.UnitY, marker, RenderingConstants.OpenNoteBarVertexType);
    }

    /// <summary>Stages the two stems of an open note's bar, one under each end of <paramref name="x0" />..<paramref name="x1" />.</summary>
    internal static void DrawOpen(RenderContext context, float x0, float x1, float headY, float z, Color color)
    {
        DrawStem(context, x0 + StemHalfWidth, headY, z, color);
        DrawStem(context, x1 - StemHalfWidth, headY, z, color);
    }

    private static void DrawStem(RenderContext context, float x, float headY, float z, Color color)
    {
        if (headY <= FloorY) return;
        var stem = Color.FromArgb(StemAlpha, color.R, color.G, color.B);
        context.QuadBatcher.AddQuad(
            new Vector3(x - StemHalfWidth, headY, z), new Vector3(x + StemHalfWidth, headY, z),
            new Vector3(x + StemHalfWidth, FloorY, z), new Vector3(x - StemHalfWidth, FloorY, z),
            -Vector3.UnitZ, stem, RenderingConstants.OpenNoteBarVertexType);
    }
}
