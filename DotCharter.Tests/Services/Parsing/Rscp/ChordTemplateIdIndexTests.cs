using System.Collections;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     <c>RscpTemplateManager.FindTemplate</c> was
///     <c>arr.ChordTemplates.FirstOrDefault(t =&gt; t.Id == templateId)</c> — a
///     linear scan with a delegate per element — called ONCE PER CHORD (the
///     template-validity check) and TWICE PER HAND SHAPE (the link pass), across
///     every level. <c>RegenerateTemplates</c> runs inside <c>RscpWriter</c>, so
///     that is on every project save: ~16,000 chords over ~200 templates on a
///     DD-generated chart is ~3.2M delegate invocations for what is a dictionary
///     lookup.
///     <para>
///     The EQUIVALENCE cases replay <c>FirstOrDefault</c> as the specification,
///     including the two cases that carry meaning here — the -1 "untemplated"
///     sentinel and a dangling id, both of which must resolve to null and
///     neither of which may fall back to template 0. The COST case counts
///     element reads through an instrumented list.
///     </para>
/// </summary>
public sealed class ChordTemplateIdIndexTests
{
    private static ChordTemplate T(int id) => new() { Id = id, Name = $"t{id}" };

    /// <summary>The scan the index replaced, verbatim.</summary>
    private static ChordTemplate? LinearFind(IReadOnlyList<ChordTemplate> templates, int id) =>
        id < 0 ? null : templates.FirstOrDefault(t => t.Id == id);

    [Fact]
    public void ItResolvesExactlyWhatTheLinearScanResolved()
    {
        // Ids deliberately NOT contiguous and NOT in order — RscpTemplateManager
        // re-numbers them, but the index must not assume it.
        List<ChordTemplate> templates = [T(4), T(0), T(9), T(2)];
        var index = new ChordTemplateIdIndex(templates);

        var sawHit = false;
        var sawMiss = false;
        for (var id = -3; id <= 12; id++)
        {
            var expected = LinearFind(templates, id);
            Assert.Same(expected, index.Find(id));
            if (expected == null) sawMiss = true;
            else sawHit = true;
        }

        Assert.True(sawHit, "no id ever resolved — the fixture proves nothing");
        Assert.True(sawMiss, "no id ever failed to resolve — the sentinel case is untested");

        // The two that carry meaning, stated on their own: 0 is a REAL id and
        // must never be a fallback for either of them.
        Assert.Null(index.Find(-1));
        Assert.Null(index.Find(7));
        Assert.Same(templates[1], index.Find(0));
    }

    [Fact]
    public void ADuplicateIdKeepsTheFirstOneJustAsFirstOrDefaultDid()
    {
        List<ChordTemplate> templates = [T(3), T(3)];
        var index = new ChordTemplateIdIndex(templates);

        Assert.Same(LinearFind(templates, 3), index.Find(3));
        Assert.Same(templates[0], index.Find(3));
    }

    [Fact]
    public void ATemplateMintedDuringThePassIsVisibleToLaterLookups()
    {
        List<ChordTemplate> templates = [T(0)];
        var index = new ChordTemplateIdIndex(templates);
        Assert.Null(index.Find(1));

        var minted = T(1);
        index.Add(minted);
        Assert.Same(minted, index.Find(1));
    }

    [Fact]
    public void ItReadsTheTemplateListOnceAndNotOncePerLookup()
    {
        const int templateCount = 200;
        const int lookupCount = 16_000;

        var templates = new List<ChordTemplate>(templateCount);
        for (var i = 0; i < templateCount; i++) templates.Add(T(i));

        var counting = new CountingList<ChordTemplate>(templates);
        var index = new ChordTemplateIdIndex(counting);

        // The build is allowed exactly one pass.
        Assert.Equal(templateCount, counting.Reads);
        var afterBuild = counting.Reads;

        // …and nothing afterwards may touch the list at all. The scan this
        // replaced touched ~16,000 x 200 / 2 = 1,600,000 elements.
        for (var i = 0; i < lookupCount; i++)
            Assert.NotNull(index.Find(i % templateCount));

        Assert.Equal(afterBuild, counting.Reads);
    }

    /// <summary>An <see cref="IReadOnlyList{T}" /> that counts every element
    /// read, so "one pass" is an assertion rather than a claim.</summary>
    private sealed class CountingList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public int Reads { get; private set; }

        public int Count => inner.Count;

        public T this[int index]
        {
            get
            {
                Reads++;
                return inner[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < inner.Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
