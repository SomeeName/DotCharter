namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     Puts the tone preview toggle back as the owner left it: applied ONCE, the
///     first time the preview becomes available (a project with tones is open), and
///     superseded by any click before that, so the restore never fights the user.
/// </summary>
internal sealed class TonePreviewRestore((bool On, bool Rocksmith) saved)
{
    private bool _pending = saved.On || saved.Rocksmith;

    public void Cancel() => _pending = false;

    /// <returns>True when it acted.</returns>
    public bool Apply(bool available, bool rocksmithAvailable, Action chooseRocksmith, Action turnOn)
    {
        if (!_pending || !available) return false;
        _pending = false;
        if (saved.Rocksmith && rocksmithAvailable) chooseRocksmith();
        if (saved.On) turnOn();
        return true;
    }
}
