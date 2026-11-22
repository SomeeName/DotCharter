using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.State;

public sealed class LyricEditInvariantTests
{
    public static IEnumerable<object[]> Seeds => Enumerable.Range(0, 300).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SeededEditsKeepCanonicalBoundariesOwnershipAndHistoryIdentity(int seed)
    {
        var random = new Random(seed);
        var rig = Build(random);
        var model = new Model(rig.Track.Phrases.Select(p => new Line(p, p.Lyrics.ToList())).ToList(), [], 0);
        var undo = new Stack<(Model Before, Model After)>();
        var redo = new Stack<(Model Before, Model After)>();
        rig.Session.Load(string.Join("-", Enumerable.Range(0, 256).Select(i => "tap" + i)));

        for (var step = 0; step < 96; step++)
        {
            var operation = step < 8 ? new[] { 5, 5, 0, 1, 3, 2, 6, 7 }[step] : random.Next(8);
            try
            {
                if (operation == 6)
                {
                    if (undo.TryPop(out var entry))
                    {
                        rig.History.Undo();
                        model = entry.Before.Copy();
                        redo.Push(entry);
                    }
                }
                else if (operation == 7)
                {
                    if (redo.TryPop(out var entry))
                    {
                        rig.History.Redo();
                        model = entry.After.Copy();
                        undo.Push(entry);
                    }
                }
                else
                {
                    var before = model.Copy();
                    if (Apply(rig, model, random, operation, step))
                    {
                        undo.Push((before, model.Copy()));
                        redo.Clear();
                    }
                }

                AssertModel(rig, model);
                AssertInsertionOwners(rig);
                var image = Image.Capture(rig);
                if (!rig.History.CanUndo) continue;
                rig.History.Undo();
                rig.History.Redo();
                image.AssertRestored(rig);
                AssertModel(rig, model);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"seed {seed}, step {step}, operation {operation}", exception);
            }
        }
    }

    private static LyricEditTestRig Build(Random random)
    {
        var rig = new LyricEditTestRig();
        var lines = random.Next(2, 6);
        for (var line = 0; line < lines; line++)
        {
            var phrase = new LyricPhrase
            {
                Position = new FractionalPosition(line * 20), EndPosition = new FractionalPosition(line * 20 + 8)
            };
            var words = Enumerable.Range(0, random.Next(1, 7)).Select(i => new LyricEvent
            {
                Text = $"line{line}word{i}", IsWordPart = random.Next(2) == 0,
                Position = new FractionalPosition(line * 20 + i), EndPosition = new FractionalPosition(line * 20 + i + 1)
            }).ToArray();
            random.Shuffle(words);
            foreach (var word in words) phrase.Lyrics.Add(word);
            phrase.Lyrics[^1].IsPhraseEnd = true;
            rig.Track.Phrases.Add(phrase);
        }
        var unordered = rig.Track.Phrases.ToArray();
        random.Shuffle(unordered);
        rig.Track.Phrases.Clear();
        foreach (var phrase in unordered) rig.Track.Phrases.Add(phrase);
        return rig;
    }

    private static bool Apply(LyricEditTestRig rig, Model model, Random random, int operation, int step)
    {
        if (operation == 5 || model.Lines.Count == 0) return Tap(rig, model, random.Next(1, 100) * 500);
        if (operation == 2) return Insert(rig, model, random.Next(100));
        var lineIndex = random.Next(model.Lines.Count);
        var line = model.Lines[lineIndex];
        var index = random.Next(line.Words.Count);
        var word = line.Words[index];
        switch (operation)
        {
            case 0:
                if (!rig.Boundary(line.Phrase, word, true)) return false;
                var split = rig.Track.Phrases[lineIndex + 1];
                Assert.DoesNotContain(model.Lines, l => ReferenceEquals(l.Phrase, split));
                model.Lines.Insert(lineIndex + 1, new Line(split, line.Words.Skip(index + 1).ToList()));
                line.Words.RemoveRange(index + 1, line.Words.Count - index - 1);
                return true;
            case 1:
                word = line.Words[^1];
                if (!rig.Boundary(line.Phrase, word, false)) return false;
                line.Words.AddRange(model.Lines[lineIndex + 1].Words);
                model.Lines.RemoveAt(lineIndex + 1);
                return true;
            case 3:
                Assert.True(rig.History.Do(new RemoveLyricAction(rig.Track.Phrases, line.Phrase, word)));
                line.Words.RemoveAt(index);
                if (line.Words.Count == 0) model.Lines.RemoveAt(lineIndex);
                return true;
            case 4:
                var text = "renamed" + step;
                Assert.True(rig.History.Do(new RenameLyricAction(word, word.Text, text,
                    word.IsWordPart, word.IsWordPart, word.IsPhraseEnd, word.IsPhraseEnd)));
                Assert.Equal(text, word.Text);
                return true;
            default:
                throw new InvalidOperationException("Unknown edit");
        }
    }

    private static bool Tap(LyricEditTestRig rig, Model model, double ms)
    {
        var line = model.Anchors.Select(anchor => model.Lines.FirstOrDefault(l => l.Words.Contains(anchor)))
            .FirstOrDefault(owner => owner != null);
        var later = rig.Members().Where(l => l.Position.GetPositionMs(rig.Beats) >= ms)
            .Select(l => (Word: l, End: l.EndPosition)).ToArray();
        var word = rig.Tap(ms);
        foreach (var (syllable, end) in later) Assert.Equal(end, syllable.EndPosition);
        if (line == null)
        {
            var phrase = rig.Track.Phrases.Single(p => p.Lyrics.Contains(word));
            Assert.DoesNotContain(model.Lines, l => ReferenceEquals(l.Phrase, phrase));
            var index = model.Lines.FindIndex(l => l.Phrase.Position > phrase.Position);
            model.Lines.Insert(index < 0 ? model.Lines.Count : index, new Line(phrase, [word]));
        }
        else
        {
            line.Words.Add(word);
        }
        model.Anchors.Insert(0, word);
        model.Taps++;
        return true;
    }

    private static bool Insert(LyricEditTestRig rig, Model model, int beat)
    {
        var position = new FractionalPosition(beat);
        var line = model.Lines.FirstOrDefault(l => l.Words.Any(w => w.Position == position))
            ?? model.Lines.FirstOrDefault(l => l.Phrase.Position <= position && l.Phrase.EndPosition >= position);
        var word = rig.Insert(beat);
        if (line == null)
        {
            var phrase = rig.Track.Phrases.Single(p => p.Lyrics.Contains(word));
            Assert.DoesNotContain(model.Lines, l => ReferenceEquals(l.Phrase, phrase));
            var index = model.Lines.FindIndex(l => l.Phrase.Position >= position);
            model.Lines.Insert(index < 0 ? model.Lines.Count : index, new Line(phrase, [word]));
        }
        else
        {
            var index = line.Words.FindIndex(l => l.Position >= position);
            line.Words.Insert(index < 0 ? line.Words.Count : index, word);
        }
        return true;
    }

    private static void AssertModel(LyricEditTestRig rig, Model model)
    {
        rig.AssertEnds();
        Assert.Equal(model.Lines.Count, rig.Track.Phrases.Count);
        for (var i = 0; i < model.Lines.Count; i++)
        {
            var expected = model.Lines[i];
            var actual = rig.Track.Phrases[i];
            Assert.Same(expected.Phrase, actual);
            Assert.Equal(expected.Words.Count, actual.Lyrics.Count);
            for (var j = 0; j < expected.Words.Count; j++)
            {
                var word = actual.Lyrics[j];
                Assert.Same(expected.Words[j], word);
                Assert.True(actual.Position <= word.Position && actual.EndPosition >= word.EndPosition,
                    $"span does not contain {word.Text}: {actual.Position}-{actual.EndPosition}, {word.Position}-{word.EndPosition}");
                Assert.True(word.EndPosition >= word.Position, $"negative duration: {word.Text}");
            }
        }
        var expectedMembers = new HashSet<LyricEvent>(model.Lines.SelectMany(l => l.Words), ReferenceEqualityComparer.Instance);
        Assert.True(expectedMembers.SetEquals(rig.Members()), "ownership: total syllable set differs from model");
        Assert.Equal(expectedMembers.Count, rig.Track.Phrases.Sum(p => p.Lyrics.Count));
        Assert.Equal(256 - model.Taps, rig.Session.Count);
    }

    private static void AssertInsertionOwners(LyricEditTestRig rig)
    {
        // Equal starts in multiple lines resolve to the first owner in collection order.
        foreach (var time in rig.Members().Select(l => l.Position).Distinct().ToArray())
        {
            var owner = rig.Track.Phrases.First(p => p.Lyrics.Any(l => l.Position == time));
            var image = Image.Capture(rig);
            var before = rig.Members();
            var action = VocalInsertion.Create(rig.Track, time, time + new FractionalPosition(new Fraction(1, 2)));
            action.Execute();
            var inserted = Assert.Single(rig.Members(), l => !before.Contains(l));
            Assert.Contains(inserted, owner.Lyrics);
            action.Undo();
            image.AssertRestored(rig);
        }
    }

    private sealed record Line(LyricPhrase Phrase, List<LyricEvent> Words);

    private sealed class Model(List<Line> lines, List<LyricEvent> anchors, int taps)
    {
        internal List<Line> Lines { get; } = lines;
        internal List<LyricEvent> Anchors { get; } = anchors;
        internal int Taps { get; set; } = taps;
        internal Model Copy() => new(Lines.Select(l => new Line(l.Phrase, l.Words.ToList())).ToList(), Anchors.ToList(), Taps);
    }

    private sealed record WordImage(LyricEvent Word, FractionalPosition Start, FractionalPosition End,
        string Text, bool WordPart, bool PhraseEnd, int Note)
    {
        internal static WordImage Capture(LyricEvent word) =>
            new(word, word.Position, word.EndPosition, word.Text, word.IsWordPart, word.IsPhraseEnd, word.Note);
        internal void AssertRestored(LyricEvent actual)
        {
            Assert.Same(Word, actual);
            Assert.Equal((Start, End, Text, WordPart, PhraseEnd, Note),
                (actual.Position, actual.EndPosition, actual.Text, actual.IsWordPart, actual.IsPhraseEnd, actual.Note));
        }
    }

    private sealed record LineImage(LyricPhrase Phrase, ObservableCollection<LyricEvent> Collection,
        FractionalPosition Start, FractionalPosition End, WordImage[] Words)
    {
        internal static LineImage Capture(LyricPhrase phrase) => new(phrase, phrase.Lyrics, phrase.Position,
            phrase.EndPosition, phrase.Lyrics.Select(WordImage.Capture).ToArray());
        internal void AssertRestored(LyricPhrase actual)
        {
            Assert.Same(Phrase, actual);
            Assert.Same(Collection, actual.Lyrics);
            Assert.Equal((Start, End), (actual.Position, actual.EndPosition));
            Assert.Equal(Words.Length, actual.Lyrics.Count);
            for (var i = 0; i < Words.Length; i++) Words[i].AssertRestored(actual.Lyrics[i]);
        }
    }

    private sealed record Image(ObservableCollection<LyricPhrase> Collection, LineImage[] Lines, int Node,
        int Remaining, string Preview)
    {
        internal static Image Capture(LyricEditTestRig rig) => new(rig.Track.Phrases,
            rig.Track.Phrases.Select(LineImage.Capture).ToArray(), rig.History.CurrentNodeId, rig.Session.Count, rig.Session.Preview);
        internal void AssertRestored(LyricEditTestRig rig)
        {
            Assert.Same(Collection, rig.Track.Phrases);
            Assert.Equal(Node, rig.History.CurrentNodeId);
            Assert.Equal((Remaining, Preview), (rig.Session.Count, rig.Session.Preview));
            Assert.Equal(Lines.Length, rig.Track.Phrases.Count);
            for (var i = 0; i < Lines.Length; i++) Lines[i].AssertRestored(rig.Track.Phrases[i]);
        }
    }
}
