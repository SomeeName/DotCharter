using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IAboutScreen" />; Close answers true, the licence link does nothing.</summary>
public sealed class FakeAboutScreen : FakeDialog<bool>, IAboutScreen
{
    private FakeAboutScreen(string version)
    {
        Version = version;
        OpenLicenseCommand = new RelayCommand(() => { });
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<IAboutScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    public static FakeAboutScreen Default() => new("v0.62.0.0");

    /// <summary>A development build with no assembly version.</summary>
    public static FakeAboutScreen Empty() => new("vdev");

    public static FakeAboutScreen LongText() => new("v0.62.0.0-preview.20260922+graphite.settings.redesign");

    public static FakeAboutScreen Full() => new("v10.128.4096.65535");

    public string Version { get; }

    public ICommand OpenLicenseCommand { get; }

    public ICommand CloseCommand { get; }
}
