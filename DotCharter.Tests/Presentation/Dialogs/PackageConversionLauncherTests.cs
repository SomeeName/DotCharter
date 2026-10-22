using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Plugins;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     File ▸ Import ▸ Convert PSARC to DotCharter song: the picker, the choice of where the songs go,
///     the run under a status-bar pill and the summary, over a converter that records what it was asked.
///     The real conversion is the plugin's and has its own tests there.
/// </summary>
public sealed class PackageConversionLauncherTests
{
    private static readonly string[] Files = [Path.Combine("lib", "a_p.psarc"), Path.Combine("lib", "b_p.psarc")];

    [Fact]
    public async Task Without_a_converter_it_says_so_and_opens_no_picker()
    {
        var rig = new Rig(converter: null);

        var report = await rig.Launcher.PickAsync();

        Assert.Null(report);
        Assert.Empty(rig.Storage.RequestedTitles);
        Assert.Empty(rig.Dialogs.Messages);
        Assert.Contains("cannot convert", rig.State.MainDisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_picker_converts_and_asks_nothing()
    {
        var rig = new Rig(new RecordingConverter());
        rig.Storage.NextOpenFiles = [];

        Assert.Null(await rig.Launcher.PickAsync());

        Assert.Empty(rig.Dialogs.Messages);
        Assert.Empty(rig.Converter!.Calls);
    }

    [Fact]
    public async Task The_picker_offers_the_converters_patterns_and_takes_several_files()
    {
        var rig = new Rig(new RecordingConverter());
        rig.Storage.NextOpenFiles = Files;
        rig.Dialogs.NextAnswer = true;

        var report = await rig.Launcher.PickAsync();

        Assert.NotNull(report);
        var filters = Assert.Single(rig.Storage.RequestedFilters)!;
        Assert.Equal(["*.psarc"], filters[0].Patterns);
        Assert.Equal(Files, Assert.Single(rig.Converter!.Calls).Inputs);
    }

    [Fact]
    public async Task OK_writes_each_song_beside_its_package_and_the_summary_reports_the_counts()
    {
        var rig = new Rig(new RecordingConverter());
        rig.Dialogs.NextAnswer = true;

        var report = await rig.Launcher.ConvertAsync(rig.Converter!, Files);

        Assert.NotNull(report);
        var call = Assert.Single(rig.Converter!.Calls);
        Assert.Null(call.OutputDirectory);
        Assert.Empty(rig.Storage.RequestedTitles); // no folder picker
        Assert.Equal(2, rig.Dialogs.Messages.Count);
        Assert.True(rig.Dialogs.Messages[0].Confirmation);
        Assert.Contains("Packages selected: 2", rig.Dialogs.Messages[0].Message, StringComparison.Ordinal);
        Assert.Equal("Conversion finished", rig.Dialogs.Messages[1].Title);
        Assert.Contains("2 converted, 0 skipped, 0 failed.", rig.Dialogs.Messages[1].Message, StringComparison.Ordinal);
        Assert.Contains($"begin:{PackageConversionLauncher.TaskId}", rig.StatusBar.Log.First(), StringComparison.Ordinal);
        Assert.Equal($"complete:{PackageConversionLauncher.TaskId}", rig.StatusBar.Log.Last());
    }

    [Fact]
    public async Task Cancel_asks_for_a_folder_and_the_songs_go_there()
    {
        var rig = new Rig(new RecordingConverter());
        rig.Dialogs.NextAnswer = false;
        rig.Storage.NextFolder = Path.Combine("out", "songs");

        var report = await rig.Launcher.ConvertAsync(rig.Converter!, Files);

        Assert.NotNull(report);
        Assert.Equal(Path.Combine("out", "songs"), Assert.Single(rig.Converter!.Calls).OutputDirectory);
        Assert.Contains(Path.Combine("out", "songs"), rig.Dialogs.Messages[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_then_no_folder_backs_out_before_anything_runs()
    {
        var rig = new Rig(new RecordingConverter());
        rig.Dialogs.NextAnswer = false;
        rig.Storage.NextFolder = null;

        Assert.Null(await rig.Launcher.ConvertAsync(rig.Converter!, Files));

        Assert.Empty(rig.Converter!.Calls);
        Assert.Single(rig.Dialogs.Messages);
        Assert.Empty(rig.StatusBar.Log);
    }

    [Fact]
    public async Task A_failure_is_named_with_its_reason_and_a_long_list_is_cut()
    {
        var converter = new RecordingConverter { Report = FailedReport(PackageConversionLauncher.FailuresListed + 3) };
        var rig = new Rig(converter);
        rig.Dialogs.NextAnswer = true;

        await rig.Launcher.ConvertAsync(converter, Files);

        var summary = rig.Dialogs.Messages[1];
        Assert.Equal("Conversion finished with problems", summary.Title);
        Assert.Contains("0 converted, 0 skipped, 11 failed.", summary.Message, StringComparison.Ordinal);
        Assert.Contains("• broken0_p.psarc: unreadable package", summary.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("broken10_p.psarc", summary.Message, StringComparison.Ordinal);
        Assert.Contains("…and 3 more.", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pills_cancel_stops_the_run_and_says_so()
    {
        var converter = new RecordingConverter { WaitForCancel = true };
        var rig = new Rig(converter);
        rig.Dialogs.NextAnswer = true;

        var run = rig.Launcher.ConvertAsync(converter, Files);
        await converter.Started.Task;
        rig.StatusBar.LastTask!.CancelAction!();

        Assert.Null(await run);
        Assert.Single(rig.Dialogs.Messages); // no summary of a cancelled run
        Assert.Contains("canceled", rig.State.MainDisplayMessage, StringComparison.Ordinal);
        Assert.Equal($"complete:{PackageConversionLauncher.TaskId}", rig.StatusBar.Log.Last());
    }

    [Fact]
    public async Task A_selection_that_holds_no_package_says_so()
    {
        var converter = new RecordingConverter { Report = new PackageConversionReport(0, []) };
        var rig = new Rig(converter);
        rig.Dialogs.NextAnswer = true;

        await rig.Launcher.ConvertAsync(converter, ["empty-folder"]);

        Assert.Equal("No package was found in the selection.", rig.Dialogs.Messages[1].Message);
    }

    private static PackageConversionReport FailedReport(int count) => new(count,
        Enumerable.Range(0, count)
            .Select(i => new PackageConversionItem($"broken{i}_p.psarc", null, null, PackageConversionStatus.Failed, "unreadable package: bad magic"))
            .ToList());

    private sealed class Rig
    {
        public Rig(RecordingConverter? converter)
        {
            Converter = converter;
            Launcher = new PackageConversionLauncher(Dialogs, Storage, new FakeConfigurationService(), StatusBar, State,
                new DialogBasics(FakeLocalization.English(), new FakeAppLogger()),
                converter is null ? [] : [converter]);
        }

        public RecordingConverter? Converter { get; }
        public ProjectState State { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public RecordingStatusBarService StatusBar { get; } = new();
        public MessageDialogs Dialogs { get; } = new();
        public PackageConversionLauncher Launcher { get; }
    }

    private sealed class MessageDialogs : IDialogs
    {
        public bool NextAnswer { get; set; }

        public List<(string Title, string Message, bool Confirmation)> Messages { get; } = [];

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed { add { } remove { } }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult> => throw new InvalidOperationException("no hosted dialog should open");

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false)
        {
            Messages.Add((title, message, isConfirmation));
            return Task.FromResult(NextAnswer);
        }

        public bool CloseTop() => false;
    }

    private sealed class RecordingConverter : IPackageSongConverter
    {
        public List<(IReadOnlyList<string> Inputs, string? OutputDirectory)> Calls { get; } = [];

        public PackageConversionReport? Report { get; init; }

        public bool WaitForCancel { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> FilePatterns { get; } = ["*.psarc"];

        public async Task<PackageConversionReport> ConvertAsync(
            IReadOnlyList<string> inputs, string? outputDirectory, PackageConversionOptions? options = null,
            IProgress<PackageConversionItem>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((inputs, outputDirectory));
            Started.TrySetResult();
            if (WaitForCancel) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Report != null) return Report;

            var folder = outputDirectory ?? "beside";
            var songs = inputs.Select(i => new PackageConversionItem(
                i, "key", Path.Combine(folder, Path.GetFileNameWithoutExtension(i) + ".dcsong"), PackageConversionStatus.Converted, null)).ToList();
            foreach (var song in songs) progress?.Report(song);
            return new PackageConversionReport(inputs.Count, songs);
        }
    }
}
