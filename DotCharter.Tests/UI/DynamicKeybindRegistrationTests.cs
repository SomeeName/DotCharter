using Avalonia.Input;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>IKeybindRegistry.RegisterDynamic</c> — the seam the user-authored match
///     patterns need, because they are the one family whose SIZE is data rather
///     than code. Everything else in the registry reserves exactly one chord
///     decided at startup.
///     <para>
///         Also covers <c>DescribeBinding</c>, which the pattern builder asks
///         before letting a chord be assigned. That matters more than it looks:
///         <c>RefreshBinds</c> resolves a collision last-registration-wins with
///         nothing but a log line, so without a warning a new pattern can kill a
///         shipped editing key and the only evidence is in the log file.
///     </para>
/// </summary>
public sealed class DynamicKeybindRegistrationTests
{
    private static ChartKeybindHandler NewHandler(bool blocked = false) =>
        new(() => blocked, new FakeAppLogger());

    [Fact]
    public void ADynamicSourceIsReEnumeratedOnEveryRefresh()
    {
        var handler = NewHandler();
        var bindings = new List<KeybindRegistration>();
        handler.RegisterDynamic(() => bindings);

        handler.RefreshBinds();
        Assert.False(handler.HandleKeyPress(Key.F1, false, false, false));

        // The user adds a pattern and saves: the SAME registration must now
        // produce a bind that did not exist when it was registered.
        var fired = 0;
        bindings.Add(new KeybindRegistration(new KeyChord(Key.F1), () => fired++, "pattern:a:select"));
        handler.RefreshBinds();

        Assert.True(handler.HandleKeyPress(Key.F1, false, false, false));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ADynamicBindDisappearsWhenItsSourceDropsIt()
    {
        var handler = NewHandler();
        var bindings = new List<KeybindRegistration>
        {
            new(new KeyChord(Key.F1), () => { }, "pattern:a:select")
        };
        handler.RegisterDynamic(() => bindings);
        handler.RefreshBinds();
        Assert.True(handler.HandleKeyPress(Key.F1, false, false, false));

        bindings.Clear();
        handler.RefreshBinds();

        Assert.False(handler.HandleKeyPress(Key.F1, false, false, false));
    }

    [Fact]
    public void AnUnboundChordIsSkippedRatherThanClaimingKeyNone()
    {
        var handler = NewHandler();
        handler.RegisterDynamic(() => [new KeybindRegistration(KeyChord.None, () => { }, "pattern:a:next")]);
        handler.RefreshBinds();

        Assert.Null(handler.DescribeBinding(KeyChord.None));
        Assert.False(handler.HandleKeyPress(Key.None, false, false, false));
    }

    [Fact]
    public void DescribeBinding_NamesTheOwnerOfAClaimedChord()
    {
        var handler = NewHandler();
        handler.Register(new KeyChord(Key.A, KeyModifiers.Control), SomeShippedAction);
        handler.RefreshBinds();

        Assert.Equal(nameof(SomeShippedAction),
            handler.DescribeBinding(new KeyChord(Key.A, KeyModifiers.Control)));
        Assert.Null(handler.DescribeBinding(new KeyChord(Key.B, KeyModifiers.Control)));
    }

    /// <summary>The builder filters its OWN binds out of the collision warning by
    ///     this label prefix, so the label has to survive registration.</summary>
    [Fact]
    public void DescribeBinding_ReportsADynamicLabelVerbatim()
    {
        var handler = NewHandler();
        handler.RegisterDynamic(() =>
            [new KeybindRegistration(new KeyChord(Key.F5), () => { }, "pattern:abc123:select")]);
        handler.RefreshBinds();

        Assert.Equal("pattern:abc123:select", handler.DescribeBinding(new KeyChord(Key.F5)));
    }

    /// <summary>Last registration wins — unchanged behaviour, pinned because the
    ///     builder's whole conflict warning is built on top of it.</summary>
    [Fact]
    public void ACollisionResolvesToTheLastRegistration()
    {
        var handler = NewHandler();
        handler.Register(new KeyChord(Key.F6), SomeShippedAction);
        handler.RegisterDynamic(() =>
            [new KeybindRegistration(new KeyChord(Key.F6), () => { }, "pattern:a:select")]);
        handler.RefreshBinds();

        Assert.Equal("pattern:a:select", handler.DescribeBinding(new KeyChord(Key.F6)));
    }

    [Fact]
    public void ADynamicBindIsBlockedWhileInputIsBlocked()
    {
        var handler = NewHandler(true);
        var fired = 0;
        handler.RegisterDynamic(() =>
            [new KeybindRegistration(new KeyChord(Key.F7), () => fired++, "pattern:a:select")]);
        handler.RefreshBinds();

        Assert.False(handler.HandleKeyPress(Key.F7, false, false, false));
        Assert.Equal(0, fired);
    }

    private static void SomeShippedAction()
    {
    }
}
