using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Infrastructure.Shell;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Help ▸ About. The version names the build in a bug report, and the licence link
///     shows the bundled THIRD-PARTY-NOTICES.md and LICENSE in the app, over About. It used to hand the .md to the
///     system, which has no viewer for it on many machines and raised Windows' "choose an
///     app" box instead.
/// </summary>
public sealed class AboutViewModelTests
{
    [Fact]
    public void About_and_home_show_the_public_release()
    {
        var vm = new AboutViewModel(new Docs("x"), new CapturingDialogs(), new FakeAppLogger(), FakeLocalization.English());
        Assert.Equal("v1.2.0", vm.Version);
        Assert.Equal(vm.Version, DotCharter.UI.ViewModels.PlayMode.FrontDoorFacts.ResolveAppVersion());
    }

    [Fact]
    public void The_version_is_prefixed_and_falls_back_to_dev()
    {
        Assert.Equal("v0.62.1.0", AboutViewModel.FormatVersion(new Version(0, 62, 1, 0)));
        Assert.Equal("vdev", AboutViewModel.FormatVersion(null));
    }

    [Fact]
    public async Task Opening_the_licence_shows_its_text_in_the_app_and_leaves_About_open()
    {
        var dialogs = new CapturingDialogs();
        var vm = new AboutViewModel(new Docs("# Notices\nGPL v3", "GNU GENERAL PUBLIC LICENSE\n"), dialogs, new FakeAppLogger(),
            FakeLocalization.English());

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.OpenLicenseCommand).ExecuteAsync(null);

        var shown = Assert.IsType<TextDocumentRequest>(dialogs.Request);
        Assert.Equal("Notices\nGPL v3\n\nGNU GENERAL PUBLIC LICENSE", shown.Text);
        Assert.Equal(@"C:\app\THIRD-PARTY-NOTICES.md", shown.FilePath);
        Assert.False(vm.Result.IsCompleted, "opening the licence must leave About open");
    }

    [Fact]
    public void A_missing_licence_file_is_said_rather_than_shown_empty()
    {
        var request = LicenseDocument.Request(new Docs("", ""), FakeLocalization.English());

        Assert.Equal("", request.Text);
        Assert.Equal("", request.FilePath);
        Assert.False(string.IsNullOrWhiteSpace(request.EmptyText));
    }

    [Fact]
    public void The_licence_reads_as_prose_not_markup()
    {
        var plain = LicenseDocument.Plain(
            "# License\n\nDotCharter is **free software** — see [§1 — License](#1--dotcharter-license).\n" +
            "```\nCopyright (C) 2026\n```\nIt links `juce_sharp.dll` under the **GNU General\nPublic License**.");

        Assert.Equal("License\n\nDotCharter is free software — see §1 — License.\nCopyright (C) 2026\nIt links juce_sharp.dll under the GNU General\nPublic License.", plain);
    }

    [Fact]
    public async Task Close_answers_true()
    {
        var vm = new AboutViewModel(new Docs("x"), new CapturingDialogs(), new FakeAppLogger(), FakeLocalization.English());

        vm.CloseCommand.Execute(null);

        Assert.True(vm.Result.IsCompleted);
        Assert.True(await vm.Result);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var vm = new AboutViewModel(new Docs("x"), new CapturingDialogs(), new FakeAppLogger(), FakeLocalization.English());
        Assert.Empty(ContractConformanceTests.Violations(typeof(IAboutScreen), vm));
    }

    [Fact]
    public void The_GPL_text_still_shows_when_the_notices_are_missing()
    {
        var request = LicenseDocument.Request(new Docs("", "GNU GENERAL PUBLIC LICENSE"), FakeLocalization.English());

        Assert.Equal("GNU GENERAL PUBLIC LICENSE", request.Text);
        Assert.Equal(@"C:\app\LICENSE", request.FilePath);
    }

    /// <summary>The notices and the GPL text, under the file names the app ships them as.</summary>
    private sealed class Docs(string notices, string licence = "GPL") : IBundledDocuments
    {
        public string Resolve(string fileName) => Path.Combine(@"C:\app", fileName);

        public string ReadText(string fileName) => fileName switch
        {
            LicenseDocument.NoticesFileName => notices,
            LicenseDocument.LicenseFileName => licence,
            _ => throw new ArgumentException($"Unexpected document '{fileName}'.", nameof(fileName))
        };
    }

    /// <summary>Applies the dialog's setup to a real <see cref="TextDocumentViewModel" /> to read the request back.</summary>
    private sealed class CapturingDialogs : IDialogs
    {
        public TextDocumentRequest? Request { get; private set; }

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed { add { } remove { } }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult>
        {
            var probe = new Probe();
            (init as Action<ITextDocumentScreen>)?.Invoke(probe);
            Request = probe.Request;
            return Task.FromResult<TResult?>(default);
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

        public bool CloseTop() => false;

        private sealed class Probe : DialogViewModel<bool>, ITextDocumentScreen, IDialogSetup<TextDocumentRequest>
        {
            public TextDocumentRequest? Request { get; private set; }
            public string Title => "";
            public string Subtitle => "";
            public string Text => "";
            public bool HasText => false;
            public string EmptyText => "";
            public System.Windows.Input.ICommand CopyCommand => null!;
            public bool CanShowFile => false;
            public System.Windows.Input.ICommand ShowFileCommand => null!;
            public System.Windows.Input.ICommand CloseCommand => null!;
            public void Configure(TextDocumentRequest request) => Request = request;
        }
    }

    /// <summary>The About link opens the licence in the app, as Help ▸ License &amp; notices… does,
    /// so it carries that label; it said "Open LICENSE.md", the file it used to hand to Windows.</summary>
    [Fact]
    public void TheLicenceLink_IsLabelledLikeTheHelpMenuItem()
    {
        var view = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs", "AboutDialogView.axaml")));
        var menu = MainMenuSource.ReadAllText();

        Assert.Contains("{localization:Loc Menu_License}", menu);
        Assert.Contains("Content=\"{localization:Loc Menu_License}\"", view);
    }
}
