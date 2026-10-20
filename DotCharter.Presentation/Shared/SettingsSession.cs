using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Presentation.Shared;

/// <summary>The Settings area's shared state: the app shell writes <see cref="IsOpen" /> as its tab is entered or left, the settings shell clears it on Save or Cancel, and <see cref="RequestReload" /> is bumped when a backup import replaced the stores behind the page.</summary>
public sealed partial class SettingsSession : ObservableObject
{
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] private int _reloadRequests;

    public void RequestReload() => ReloadRequests++;
}
