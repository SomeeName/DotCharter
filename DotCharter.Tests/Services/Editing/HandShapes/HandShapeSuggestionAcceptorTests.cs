using DotCharter.Services.Editing.HandShapes;
using DotCharter.Tests.Services.Editing.Highway;
using static DotCharter.Tests.Services.Editing.HandShapes.ChordHandShapePlannerTests;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     Accepting suggested hand shapes (#491): ONE undo node per gesture, through the same actions
///     as Generate hand shapes (arrangement) — the minted templates first, then the bars — so one
///     Ctrl+Z takes back the bars AND every template they minted (the
///     <see cref="HandShapeTemplateMintTests" /> rule). Plain by default; arpeggio resolves the
///     arpeggio flavour and never flags a shared template.
/// </summary>
public sealed class HandShapeSuggestionAcceptorTests
{
    private static readonly (int String, int Fret)[] COpenArpeggio =
        [(1, 3), (2, 2), (3, 0), (4, 1), (5, 0), (4, 1), (3, 0), (2, 2)];

    private static readonly (int String, int Fret)[] AmAt5 = [(1, 7), (2, 7), (3, 5), (4, 5)];

    private static void Pick(ChartLevel level, double startBeat, params (int String, int Fret)[] notes)
    {
        for (var i = 0; i < notes.Length; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = Pos(startBeat + i * 0.5), StringIndex = notes[i].String, Fret = notes[i].Fret
            });
        }
    }

    private static (ChordKeyboardMutationTests.Harness H, ChartLevel Level, HandShapeSuggestionAcceptor Acceptor) Chart()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var level = h.Arrangement.Levels[0];
        Pick(level, 0, COpenArpeggio);
        Pick(level, 6, AmAt5);
        return (h, level, new HandShapeSuggestionAcceptor(h.History, new FakeAppLogger()));
    }

    [Fact]
    public void AcceptingEverySuggestion_IsOneUndoNode_ThatTakesBackTheBarsAndTheirMints()
    {
        var (h, level, acceptor) = Chart();
        var suggestions = HandShapeSuggester.Suggest(h.Arrangement, level);
        Assert.Equal(2, suggestions.Count);

        Assert.Equal(2, acceptor.Accept(h.Arrangement, level, suggestions, arpeggio: false));

        Assert.Equal(2, level.HandShapes.Count);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.All(level.HandShapes,
            bar => Assert.Contains(h.Arrangement.ChordTemplates, t => t.Id == bar.TemplateId));
        Assert.Empty(HandShapeSuggester.Suggest(h.Arrangement, level)); // accepted runs are covered now

        h.History.Undo();

        Assert.Empty(level.HandShapes);
        Assert.Empty(h.Arrangement.ChordTemplates);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void APlainAccept_ReusesTheDeclaredShape_AndMintsNothing()
    {
        var (h, level, acceptor) = Chart();
        var c = new ChordTemplate
        {
            Id = 7, Name = "C", Frets = new Dictionary<int, int> { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 }
        };
        h.Arrangement.ChordTemplates.Add(c);
        var first = HandShapeSuggester.Suggest(h.Arrangement, level)[0];

        acceptor.Accept(h.Arrangement, level, [first], arpeggio: false);

        Assert.Equal(7, Assert.Single(level.HandShapes).TemplateId);
        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.False(c.IsArpeggio);
    }

    [Fact]
    public void AnArpeggioAccept_ResolvesTheArpeggioFlavour_AndNeverFlagsTheSharedTemplate()
    {
        var (h, level, acceptor) = Chart();
        var c = new ChordTemplate
        {
            Id = 7, Name = "C", Frets = new Dictionary<int, int> { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 }
        };
        h.Arrangement.ChordTemplates.Add(c);
        var first = HandShapeSuggester.Suggest(h.Arrangement, level)[0];

        acceptor.Accept(h.Arrangement, level, [first], arpeggio: true);

        var bar = Assert.Single(level.HandShapes);
        var template = h.Arrangement.ChordTemplates.Single(t => t.Id == bar.TemplateId);
        Assert.True(template.IsArpeggio);
        Assert.True(template.HasFrets(c.Frets));
        Assert.False(c.IsArpeggio); // the plain C every chord shares is untouched

        h.History.Undo();
        Assert.Same(c, Assert.Single(h.Arrangement.ChordTemplates));
    }

    [Fact]
    public void InSelection_PicksOnlyTheSuggestionsHoldingASelectedSound()
    {
        var (h, level, _) = Chart();
        var suggestions = HandShapeSuggester.Suggest(h.Arrangement, level);

        var picked = HandShapeSuggestionAcceptor.Holding(suggestions, [Pos(6.5)]);

        Assert.Same(suggestions[1], Assert.Single(picked));
        Assert.Empty(HandShapeSuggestionAcceptor.Holding(suggestions, [Pos(5)]));
    }

    [Fact]
    public void AcceptingNothing_PushesNoNode()
    {
        var (h, level, acceptor) = Chart();

        Assert.Equal(0, acceptor.Accept(h.Arrangement, level, [], arpeggio: false));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void ASuggestedBar_IsLabelledWithItsTemplate_ElseItsSuggestedName_ElseItsFrets()
    {
        var (h, level, _) = Chart();
        var first = HandShapeSuggester.Suggest(h.Arrangement, level)[0];

        Assert.False(string.IsNullOrEmpty(HandShapeSuggestionAcceptor.ToPreview(h.Arrangement, first).Name));

        h.Arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 3, Name = "Cadd", Frets = new Dictionary<int, int> { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 }
        });
        var reused = HandShapeSuggester.Suggest(h.Arrangement, level)[0];
        var preview = HandShapeSuggestionAcceptor.ToPreview(h.Arrangement, reused);
        Assert.Equal(("Cadd", 3), (preview.Name, preview.TemplateId));
        Assert.Equal((first.Position, first.EndPosition), (preview.Position, preview.EndPosition));
    }
}
