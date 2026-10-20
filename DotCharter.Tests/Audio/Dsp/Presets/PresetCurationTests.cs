using DotCharter.Tone.Audio.Dsp.Presets;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
/// Guards the <see cref="PresetCuration"/> quality-first ordering seam: featured
/// ids exist + are unique, ranks are well-ordered, and the ordering is stable.
/// </summary>
public sealed class PresetCurationTests
{
    [Fact]
    public void EveryFeaturedId_ExistsInTheLibrary()
    {
        var known = DspTonePresetLibrary.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dangling = PresetCuration.FeaturedIds.Where(id => !known.Contains(id)).ToList();
        Assert.True(dangling.Count == 0,
            $"PresetCuration references preset id(s) not in the library: {string.Join(", ", dangling)}");
    }

    [Fact]
    public void FeaturedIds_AreUnique()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in PresetCuration.FeaturedIds)
            Assert.True(seen.Add(id), $"Duplicate featured id '{id}'.");
    }

    [Fact]
    public void RankOf_IsAscending_ForFeatured_AndMaxForUnknown()
    {
        var ids = PresetCuration.FeaturedIds;
        for (var i = 1; i < ids.Count; i++)
            Assert.True(PresetCuration.RankOf(ids[i]) > PresetCuration.RankOf(ids[i - 1]));

        Assert.Equal(int.MaxValue, PresetCuration.RankOf("no-such-preset"));
        Assert.Equal(int.MaxValue, PresetCuration.RankOf(null));
        Assert.False(PresetCuration.IsFeatured("no-such-preset"));
        Assert.True(PresetCuration.IsFeatured(ids[0]));
    }

    [Fact]
    public void OrderFeaturedFirst_PutsFeaturedInCuratedOrder_ThenTheRestStable()
    {
        var ordered = PresetCuration.OrderFeaturedFirst(DspTonePresetLibrary.All);

        // Same set, no loss/dup.
        Assert.Equal(DspTonePresetLibrary.All.Count, ordered.Count);
        Assert.Equal(
            DspTonePresetLibrary.All.Select(p => p.Id).OrderBy(x => x),
            ordered.Select(p => p.Id).OrderBy(x => x));

        // The featured shelf leads, in curated order.
        var leadFeatured = ordered
            .TakeWhile(p => PresetCuration.IsFeatured(p.Id))
            .Select(p => p.Id)
            .ToList();
        Assert.Equal(PresetCuration.FeaturedIds, leadFeatured);

        // The tail (unranked) keeps the library's original relative order (stable).
        var tail = ordered.Skip(PresetCuration.FeaturedCount).Select(p => p.Id).ToList();
        var originalTailOrder = DspTonePresetLibrary.All
            .Where(p => !PresetCuration.IsFeatured(p.Id))
            .Select(p => p.Id)
            .ToList();
        Assert.Equal(originalTailOrder, tail);
    }
}
