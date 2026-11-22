using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Pen;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Sheet;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class SoundEntryUiPerfSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChordBoxContinuationUsesTheSameWindowAsItsFretValue(bool expired) => HeadlessSessionFixture.Dispatch(() =>
    {
        var edits = new List<ChordBoxFretEdit>();
        var box = new ChordBoxControl
        {
            Role = ChordBoxRole.Shape,
            SetFretCommand = new RelayCommand<ChordBoxFretEdit>(edits.Add),
            Width = 300,
            Height = 200
        };
        var window = new Window { Width = 300, Height = 200, Content = box };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(box.Bounds is { Width: > 0, Height: > 0 });
            SetField(box, "_hoveredString", 0);
            Press(box, Key.D1);
            Assert.Equal(new ChordBoxFretEdit(0, 1, false), Assert.Single(edits));

            // Move the accumulator clock past its deadline without a sleep.
            SetField(box, "_fretDigits", new FretDigitAccumulator(1,
                Environment.TickCount64 - (expired ? FretDigitAccumulator.SheetWindowMs + 1 : 0)));
            Press(box, Key.D2);

            Assert.Equal(new ChordBoxFretEdit(0, expired ? 2 : 12, !expired), edits[1]);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task SheetDigitsRestartWhenTheirSoundTargetChanges(bool chords, bool returnToFirst) => HeadlessSessionFixture.Dispatch(() =>
    {
        var selection = new SelectionService();
        var firstNote = new GuitarNote();
        var secondNote = new GuitarNote { Position = new FractionalPosition(2) };
        var firstChord = new Chord();
        var secondChord = new Chord { Position = new FractionalPosition(2) };
        if (chords) selection.SelectedChords.Add(firstChord);
        else selection.SelectedNotes.Add(firstNote);
        var values = new List<int>();
        var view = new SheetMusicView { Selection = selection };
        view.SetFretRequested += values.Add;
        var window = new Window { Width = 800, Height = 220, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(view.Bounds is { Width: > 0, Height: > 0 });
            Press(view, Key.D1);
            selection.ClearSelections();
            if (chords) selection.SelectedChords.Add(secondChord);
            else selection.SelectedNotes.Add(secondNote);
            if (returnToFirst)
            {
                selection.ClearSelections();
                if (chords) selection.SelectedChords.Add(firstChord);
                else selection.SelectedNotes.Add(firstNote);
            }
            Press(view, Key.D2);

            Assert.Equal(new[] { 1, 2 }, values);

            // Repeated notifications and a fret edit on the same sound must
            // leave its run open: 1 then 2 still means twelve.
            if (chords) (returnToFirst ? firstChord : secondChord).ChordNotes.Add(new ChordNote { Fret = 2 });
            else (returnToFirst ? firstNote : secondNote).Fret = 2;
            Press(view, Key.D1);
            Assert.Equal(21, values[^1]);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    private static void Press(Control control, Key key) => control.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent, Source = control, Key = key
    });

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"{target.GetType().Name}.{name} moved; update the input fixture.");
        field.SetValue(target, value);
    }
}
