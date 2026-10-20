namespace DotCharter.Presentation.Navigation;

/// <summary>A request to show the screen for <see cref="Contract" />; <see cref="Init" /> runs once, on that show.</summary>
public sealed record ScreenRequest(Type Contract, Action<object>? Init = null)
{
    public static ScreenRequest For<TContract>(Action<TContract>? init = null) where TContract : class =>
        new(typeof(TContract), init is null ? null : screen => init((TContract)screen));
}
