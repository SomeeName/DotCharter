namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     Per-arrangement highway scroll speed, in Rocksmith units (LOWER IS FASTER).
///
///     <para>The thing worth pinning here is the ENCODING. Rocksmith has no
///     "ScrollSpeed" manifest attribute — the value lives in the
///     <c>DynamicVisualDensity</c> ramp, whose tail is the authored speed and
///     whose head ramps down from 5.0 across the DD levels. An importer that
///     looks for a ScrollSpeed key finds nothing on every real CDLC and silently
///     falls back to the default, which looks exactly like "the feature works,
///     charts just all use 1.3".</para>
/// </summary>
public sealed partial class ScrollSpeedRoundTripTests
{
    // The curve our exporter writes (RsManifestGenerator.CreateDynamicVisualDensity),
    // which is Rocksmith2014.NET's createDynamicVisualDensity verbatim. Reproduced
    // here so the test proves the DECODER against the real shape rather than
    // against a hand-waved array.
    private static double[] BuildDensity(double scrollSpeed, int levels)
    {
        const double floorLimit = 0.5;
        const double beginSpeed = 5.0;
        var endSpeed = Math.Clamp(scrollSpeed, floorLimit, beginSpeed);
        var maxLevel = Math.Min(levels, 20) - 1;
        var factor = maxLevel > 0 ? Math.Pow(endSpeed / beginSpeed, 1.0 / maxLevel) : 1.0;

        var density = new double[20];
        for (var i = 0; i < 20; i++)
        {
            density[i] = i >= maxLevel
                ? endSpeed
                : Math.Round(beginSpeed * Math.Pow(factor, i), 1);
        }

        return density;
    }
}
