namespace DotCharter.Presentation.Navigation;

/// <summary>The host side of navigation: shows the screen for a contract and returns its view model.</summary>
public interface IScreenPresenter
{
    object Present(Type contract);
}

/// <summary>Optional on a screen view model: timers start in <see cref="OnShown" /> and stop in <see cref="OnHidden" />.</summary>
public interface IScreenLifecycle
{
    void OnShown();

    void OnHidden();
}
