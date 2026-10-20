using Avalonia.Input;
using DotCharter.Services.Infrastructure.Input;
using DotCharter.UI.Platform;

namespace DotCharter.Tests.UI.Platform;

/// <summary>The one translation from Avalonia keys to <see cref="ScreenKey" />,
/// and the offer-to-a-screen round trip.</summary>
public sealed class AvaloniaKeyGestureSourceTests
{
    [Theory]
    [InlineData(Key.Enter, ScreenKey.Enter)]
    [InlineData(Key.Escape, ScreenKey.Escape)]
    [InlineData(Key.Space, ScreenKey.Space)]
    [InlineData(Key.Back, ScreenKey.Backspace)]
    [InlineData(Key.A, ScreenKey.A)]
    [InlineData(Key.T, ScreenKey.T)]
    [InlineData(Key.Z, ScreenKey.Z)]
    [InlineData(Key.D0, ScreenKey.D0)]
    [InlineData(Key.D9, ScreenKey.D9)]
    [InlineData(Key.F5, ScreenKey.None)]
    [InlineData(Key.LeftShift, ScreenKey.None)]
    public void Map_TranslatesTheKeysAScreenCanBind(Key key, ScreenKey expected)
        => Assert.Equal(expected, AvaloniaKeyGestureSource.Map(key));

    [Fact]
    public void Publish_ReportsWhetherAScreenHandledIt()
    {
        var source = new AvaloniaKeyGestureSource();
        ScreenKeyEventArgs? seen = null;
        source.KeyPressed += (_, e) =>
        {
            seen = e;
            e.Handled = e.Key == ScreenKey.T;
        };

        Assert.True(source.Publish(Key.T, textInputFocused: false));
        Assert.Equal(ScreenKey.T, seen!.Key);
        Assert.False(seen.TextInputFocused);

        Assert.False(source.Publish(Key.Enter, textInputFocused: true));
        Assert.True(seen.TextInputFocused);
    }

    [Fact]
    public void Publish_DoesNotOfferAKeyNoScreenCanBind()
    {
        var source = new AvaloniaKeyGestureSource();
        var offered = 0;
        source.KeyPressed += (_, _) => offered++;

        Assert.False(source.Publish(Key.F5, textInputFocused: false));
        Assert.Equal(0, offered);
    }
}
