namespace DotCharter.Tests.Core;

/// <summary>
///     <c>Arrangement.FindChordTemplate</c> is the ONE chord-template-by-id lookup
///     (it replaced about 25 hand-written copies). A negative id is never a
///     match, a duplicated id answers with the FIRST template, a dangling id is
///     null — and the id is never read as a list position.
/// </summary>
public sealed class ChordTemplateLookupTests
{
    private static ChordTemplate T(int id, string name) => new() { Id = id, Name = name };

    /// <summary>The semantics of the former <c>ChordTemplateResolver.FindById</c>, verbatim.</summary>
    private static ChordTemplate? Spec(Arrangement arrangement, int id) =>
        id < 0 ? null : arrangement.ChordTemplates.FirstOrDefault(t => t.Id == id);

    private static Arrangement Build(params ChordTemplate[] templates)
    {
        var arrangement = new Arrangement();
        foreach (var template in templates) arrangement.ChordTemplates.Add(template);
        return arrangement;
    }

    [Fact]
    public void Duplicate_ids_answer_with_the_first_template()
    {
        var first = T(2, "first");
        var arrangement = Build(T(0, "a"), T(1, "b"), first, T(2, "second"));

        Assert.Same(first, arrangement.FindChordTemplate(2));
    }

    [Fact]
    public void A_negative_id_is_null_even_when_a_template_carries_it()
    {
        var arrangement = Build(T(-1, "stray"), T(0, "a"));

        Assert.Null(arrangement.FindChordTemplate(-1));
        Assert.Null(arrangement.FindChordTemplate(-7));
    }

    [Fact]
    public void A_dangling_id_is_null_and_ids_are_not_positions()
    {
        var arrangement = Build(T(5, "five"), T(9, "nine"));

        Assert.Null(arrangement.FindChordTemplate(0));
        Assert.Null(arrangement.FindChordTemplate(1));
        Assert.Null(arrangement.FindChordTemplate(6));
        Assert.Equal("nine", arrangement.FindChordTemplate(9)!.Name);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Agrees_with_the_former_FindById_for_every_id(int id)
    {
        var arrangement = Build(T(0, "a"), T(2, "b"), T(2, "dup"), T(-1, "stray"), T(1, "c"));

        Assert.Same(Spec(arrangement, id), arrangement.FindChordTemplate(id));
    }

    [Fact]
    public void An_empty_arrangement_answers_null()
        => Assert.Null(new Arrangement().FindChordTemplate(0));
}
