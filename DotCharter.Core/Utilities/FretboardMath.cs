using System.Numerics;

namespace DotCharter.Core.Utilities;

public static class FretboardMath
{
    public const float FirstFretDistance = 1.2f;
    private static readonly float[] FretPositions;

    static FretboardMath()
    {
        FretPositions = new float[25];
        var length = FirstFretDistance;
        FretPositions[0] = 0;
        for (var i = 1; i <= 24; i++)
        {
            FretPositions[i] = FretPositions[i - 1] + length;
            length *= 0.94387f;
        }
    }

    public static float GetFretPosition(int fret)
    {
        if (fret <= 0) return FretPositions[0];
        if (fret >= 24) return FretPositions[24];
        return FretPositions[fret];
    }

    public static float GetFretMiddle(int fret)
    {
        if (fret <= 0) return GetFretPosition(0) / 2f;
        if (fret > 24) fret = 24;
        return (GetFretPosition(fret - 1) + GetFretPosition(fret)) / 2f;
    }

    public static Matrix4x4 GetFrettedNoteRotatedModelMatrix(Vector3 position, double timeMs, double objectTime)
    {
        var translation = Matrix4x4.CreateTranslation(position);
        var rotation = Math.Max(-Math.PI / 2, Math.Min(0, -Math.PI * (objectTime - timeMs - 100) / 1000.0));
        return Matrix4x4.CreateRotationZ((float)rotation) * translation;
    }

    public static Matrix4x4 GetFlatNoteModelMatrix(Vector3 position) => Matrix4x4.CreateTranslation(position);
}
