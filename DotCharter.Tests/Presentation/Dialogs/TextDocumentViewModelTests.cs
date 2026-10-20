using System.Text.RegularExpressions;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Help ▸ Usage statistics and the licence are read inside the app. Both used to hand a
///     file (.txt, .md) to its default app, which on a machine with none raised Windows'
///     "choose an app" box and showed nothing (2026-09-24).
/// </summary>
public sealed class TextDocumentViewModelTests
{
    private sealed class Shell : IShellLauncher
    {
        public List<string> Revealed { get; } = [];
        public void OpenUrl(string url) { }
        public bool OpenFolder(string path, bool create = false) => true;
        public bool RevealInFileManager(string path) { Revealed.Add(path); return true; }
    }

    private sealed class Clipboard : ISystemClipboard
    {
        public string? Text { get; private set; }
        public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    [Fact]
    public async Task It_shows_the_text_copies_it_and_selects_the_file_without_opening_it()
    {
        var shell = new Shell();
        var clipboard = new Clipboard();
        var vm = new TextDocumentViewModel(clipboard, shell, new FakeAppLogger());
        vm.Configure(new TextDocumentRequest("Usage", "Local", "=== report ===\n  3  Save", @"C:\cfg\usage-report.txt"));

        Assert.Equal("Usage", vm.Title);
        Assert.True(vm.HasText);
        Assert.True(vm.CanShowFile);

        vm.CopyCommand.Execute(null);
        Assert.Equal("=== report ===\n  3  Save", clipboard.Text);

        vm.ShowFileCommand.Execute(null);
        Assert.Equal([@"C:\cfg\usage-report.txt"], shell.Revealed);

        vm.CloseCommand.Execute(null);
        Assert.True(await vm.Result);
    }

    [Fact]
    public void A_document_without_a_file_or_text_says_so()
    {
        var vm = new TextDocumentViewModel(new Clipboard(), new Shell(), new FakeAppLogger());
        vm.Configure(new TextDocumentRequest("T", "", "", "", "Nothing yet."));

        Assert.False(vm.CanShowFile);
        Assert.False(vm.HasText);
        Assert.Equal("Nothing yet.", vm.EmptyText);
    }

    [Theory]
    [InlineData("ShowUsageStatistics")]
    [InlineData("OpenLicenseFile")]
    public void The_Help_menu_shows_its_documents_and_never_hands_them_to_the_system(string command)
    {
        var help = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "HelpMenuViewModel.cs")));
        var body = Regex.Match(help, @"private void " + command + @"\(\)(?<b>.*?)\n    }", RegexOptions.Singleline).Groups["b"].Value;

        Assert.False(string.IsNullOrEmpty(body), $"{command} was not found");
        Assert.Contains("_hostedDialogs.ShowAsync(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFile(", body, StringComparison.Ordinal);
    }
}
