using System.Reflection;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Pen;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

public sealed class ChordBoxTypingTests
{
    [Fact]
    public void AnExpiredAccumulatorDoesNotReplaceThePreviousHistoryNode()
    {
        // Bare custom-drawn control: no items, application or dispatcher session.
        var edits = new List<ChordBoxFretEdit>();
        var box = new KeyboardBox
        {
            Role = ChordBoxRole.Shape,
            SetFretCommand = new RelayCommand<ChordBoxFretEdit>(edits.Add)
        };
        SetField(box, "_hoveredString", 0);
        SetField(box, "_fretDigitsString", 0);
        SetField(box, "_fretDigits", new FretDigitAccumulator(1,
            Environment.TickCount64 - FretDigitAccumulator.SheetWindowMs - 1));

        box.Type(Key.D2);

        Assert.Equal(new ChordBoxFretEdit(0, 2, false), Assert.Single(edits));
    }

    private static void SetField(ChordBoxControl target, string name, object value)
    {
        var field = typeof(ChordBoxControl).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"ChordBoxControl.{name} moved; update the input fixture.");
        field.SetValue(target, value);
    }

    private sealed class KeyboardBox : ChordBoxControl
    {
        public void Type(Key key) => OnKeyDown(new KeyEventArgs { Key = key });
    }
}
