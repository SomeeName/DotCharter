namespace DotCharter.Presentation.Navigation;

/// <summary>Moves one screen host between screens: one navigator and one bounded back stack per host scope.</summary>
public interface INavigator
{
    Type? Current { get; }

    bool CanGoBack { get; }

    void Show<TContract>(Action<TContract>? init = null) where TContract : class;

    void Show(ScreenRequest request);

    bool Back();
}
