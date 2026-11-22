using DotCharter.Core.Linking;

namespace DotCharter.Services.Editing.Linking;

internal sealed class AutoLinkVerification(Arrangement arrangement, IList<BeatMarker> beats, MeasureContentOptions rule)
{
    private readonly MeasureContentIndex _rule = MeasureContentIndex.ForHardestLevel(arrangement, beats, rule);
    private readonly MeasureContentIndex _exact = MeasureContentIndex.ForAutoLinkVerification(arrangement, beats);

    public MeasureContentKey ExactBarKey(int measure) => _exact.KeyFor(measure);

    public (MeasureContentKey Rule, MeasureContentKey Exact) KeyFor(int first, int count) =>
        (_rule.KeyFor(first, count), _exact.KeyFor(first, count));
}
