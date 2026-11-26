using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>#847: the song-list name warning points at Song metadata, the file name follows the title, vocals show their state.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    private static ChartProject JapaneseTitleProject()
    {
        var project = CreateValidProject();
        project.Chart.Artist = "SYU";
        project.Chart.SongName = "愛傷";
        return project;
    }

    /// <summary>Stands in for the host: opening Song metadata runs <paramref name="edit" /> as the user's typing and saving there.</summary>
    private sealed class MetadataDialogs(Action edit) : IDialogs
    {
        public int Opened { get; private set; }
        public bool HasOpenDialog => false;
        public event EventHandler<DialogChange>? Changed { add { } remove { } }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult>
        {
            if (typeof(TContract) == typeof(ISongMetadataScreen)) Opened++;
            edit();
            return Task.FromResult(default(TResult));
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(true);
        public bool CloseTop() => false;
    }

    [Fact]
    public void A_missing_album_year_note_links_to_Song_metadata_and_a_stray_space_note_does_not()
    {
        var project = CreateValidProject();
        project.Chart.AlbumYear = "";
        project.Chart.SongName = "Dialog  Song";
        using var vm = new Harness(project) { Dialogs = new MetadataDialogs(() => { }) }.Open();

        Assert.Single(vm.ValidationItems, f => f.HasSongMetadataLink);
        Assert.Contains(vm.ValidationItems, f => !f.HasSongMetadataLink && f.HasFix);
    }

    [Fact]
    public void A_dlc_key_a_library_package_uses_is_flagged_in_the_dialog_with_a_fix()
    {
        var project = CreateValidProject();
        var key = DotCharter.Services.Export.RocksmithDlcKey.Create(project.Chart.Artist, project.Chart.SongName);
        using var vm = new Harness(project) { LibraryIndex = new DotCharter.Tests.Services.Export.PackageKeyIndex(key) }.Open();

        var item = Assert.Single(vm.ValidationItems, f => f.Message.Contains(key, StringComparison.Ordinal));

        Assert.True(item.HasFix);
    }

    [Fact]
    public void The_glyph_warning_names_Song_metadata_and_has_a_button_that_opens_it()
    {
        var harness = new Harness(JapaneseTitleProject()) { Dialogs = new MetadataDialogs(() => { }) };
        using var vm = harness.Open();

        var warning = Assert.Single(vm.ValidationItems, f => f.HasSongMetadataLink);
        Assert.Equal("", warning.Location);
        Assert.Contains("Song metadata", FakeLocalization.English()[ChartExportValidator.KeyDisplayNameGlyphs], StringComparison.Ordinal);

        warning.OpenSongMetadataCommand.Execute(null);

        Assert.Equal(1, ((MetadataDialogs)harness.Dialogs).Opened);
    }

    [Fact]
    public void A_finding_that_is_not_about_a_name_has_no_Song_metadata_button()
    {
        using var vm = new Harness(CreateValidProject()).Open();

        Assert.DoesNotContain(vm.ValidationItems, f => f.HasSongMetadataLink);
    }

    [Fact]
    public void A_title_saved_in_Song_metadata_renames_the_derived_file_and_clears_the_warning()
    {
        var project = JapaneseTitleProject();
        var harness = new Harness(project) { Dialogs = new MetadataDialogs(() => project.Chart.SongName = "Aishou") };
        using var vm = harness.Open();
        Assert.Matches(@"SYU_Song-[0-9A-F]{4}_v1_p\.psarc$", vm.OutputPath);
        var folder = Path.GetDirectoryName(vm.OutputPath);

        vm.ValidationItems.Single(f => f.HasSongMetadataLink).OpenSongMetadataCommand.Execute(null);

        Assert.Equal(Path.Combine(folder!, "SYU_Aishou_v1_p.psarc"), vm.OutputPath);
        Assert.DoesNotContain(vm.ValidationItems, f => f.HasSongMetadataLink);
        Assert.EndsWith(DotCharter.Services.Export.RocksmithDlcKey.Create(project.Chart.Artist, "Aishou"), vm.DlcKeyWatermark);
    }

    [Fact]
    public void A_path_typed_by_hand_keeps_its_name_when_the_title_changes()
    {
        var project = JapaneseTitleProject();
        var harness = new Harness(project) { Dialogs = new MetadataDialogs(() => project.Chart.SongName = "Aishou") };
        using var vm = harness.Open();
        var typed = Path.Combine(_tempDir, "my-own_p.psarc");
        vm.OutputPath = typed;

        vm.ValidationItems.Single(f => f.HasSongMetadataLink).OpenSongMetadataCommand.Execute(null);

        Assert.Equal(typed, vm.OutputPath);
    }

    [Fact]
    public void Reopening_the_dialog_after_a_title_change_names_the_file_after_the_new_title()
    {
        var project = CreateValidProject();
        project.Chart.Artist = "SYU";
        project.Chart.SongName = "SYU-old";
        var harness = new Harness(project);
        var first = harness.Open();
        Assert.EndsWith("SYU_SYU-old_v1_p.psarc", first.OutputPath);
        first.TryCancel();
        project.Chart.SongName = "Aishou";

        using var second = harness.Open();

        Assert.EndsWith("SYU_Aishou_v1_p.psarc", second.OutputPath);
    }

    [Fact]
    public void Reopening_the_dialog_keeps_a_path_the_user_picked()
    {
        var project = CreateValidProject();
        var harness = new Harness(project);
        var first = harness.Open();
        var picked = Path.Combine(_tempDir, "picked_p.psarc");
        first.OutputPath = picked;
        first.TryCancel();
        project.Chart.SongName = "Another Title";

        using var second = harness.Open();

        Assert.Equal(picked, second.OutputPath);
    }

    [Fact]
    public void Include_vocals_shows_unticked_and_disabled_without_a_vocal_track_and_keeps_the_choice()
    {
        var project = CreateValidProject();
        project.VocalTracks.Clear();
        using var vm = new Harness(project).Open();
        IRocksmithExportScreen screen = vm;

        Assert.False(screen.HasVocals);
        Assert.True(vm.IncludeVocals);
        Assert.False(screen.VocalsTicked);

        vm.HasVocals = true;
        Assert.True(screen.VocalsTicked);
    }
}
