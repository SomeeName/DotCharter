using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Navigation;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.Localization;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     Contract conformance (target-architecture.md § 6), derived from the
///     <see cref="ScreenCatalog" />: every fake variant — and every product view
///     model the Presentation modules register for a catalog contract — reads
///     every contract property without throwing, exposes non-null commands,
///     strings and lists, and <b>raises <c>PropertyChanged</c> for every
///     contract property whose value a command changes</b>.
///     <para>
///         The notification half is observed, not declared: each command that
///         can run with no parameter is executed, the contract properties are
///         read before and after, and a value that moved with no notification is
///         a binding that would go stale on screen. A notification naming a
///         property the type does not have is reported too — that is a rename
///         the view never hears about.
///     </para>
/// </summary>
public sealed class ContractConformanceTests
{
    public static TheoryData<string, string> FakeVariants
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var entry in ScreenCatalog.Default.Entries)
            {
                foreach (var variant in FakeScreens.VariantsOf(entry.Contract))
                    data.Add(entry.Contract.FullName!, variant.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FakeVariants))]
    public void Every_fake_variant_conforms_to_its_contract(string contractName, string variantName)
    {
        var entry = ScreenCatalog.Default.Entries.Single(e => e.Contract.FullName == contractName);
        var variant = FakeScreens.VariantsOf(entry.Contract).Single(v => v.Name == variantName);

        var violations = Violations(entry.Contract, variant.Create());
        Assert.True(violations.Count == 0,
            $"{entry.Contract.Name} fake, variant {variantName}:\n  {string.Join("\n  ", violations)}");
    }

    [Fact]
    public void Every_registered_product_view_model_conforms_to_its_contract()
    {
        var services = new ServiceCollection();
        NavigationModule.Register(services);
        DialogsModule.Register(services);
        DotCharter.Presentation.Screens.Editor.EditorModule.Register(services);
        services.AddSingleton<DotCharter.Services.Plugins.IPackageFeatures>(DotCharter.Services.Plugins.PackageFeatures.All);
        AddDialogDependencies(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var checkedContracts = new List<string>();
        foreach (var entry in ScreenCatalog.Default.Entries)
        {
            if (scope.ServiceProvider.GetService(entry.Contract) is not { } viewModel) continue;
            checkedContracts.Add(entry.Contract.Name);
            var violations = Violations(entry.Contract, viewModel);
            Assert.True(violations.Count == 0,
                $"{viewModel.GetType().Name} as {entry.Contract.Name}:\n  {string.Join("\n  ", violations)}");
        }

        Assert.Contains("IMessagePromptScreen", checkedContracts);
        Assert.Contains("IShapePanelScreen", checkedContracts);
        var dialogs = ScreenCatalog.Default.Entries.Where(e => e.Kind == ScreenKind.Dialog).Select(e => e.Contract.Name)
            .Except(DesignOnlyDialogs);
        Assert.All(dialogs, name => Assert.Contains(name, checkedContracts));
        Assert.All(DesignOnlyDialogs, name => Assert.DoesNotContain(name, checkedContracts));
    }

    /// <summary>
    ///     Catalog dialogs that are gallery designs on their fakes, awaiting the owner's approval
    ///     before any view model exists. The list only shrinks: wiring one fails the check above
    ///     until its entry is deleted here.
    /// </summary>
    private static readonly string[] DesignOnlyDialogs = [];

    /// <summary>What the dialog view models the module registers reach through, over a real 16-beat chart editor.</summary>
    private static void AddDialogDependencies(IServiceCollection services)
    {
        var chart = new Dialogs.ChartEditHarness();
        services.AddSingleton<DotCharter.Services.Editing.IChartEditorSurface>(chart.Editor);
        services.AddSingleton<DotCharter.Services.Editing.ChordShapes.IChordUseMarks>(new DotCharter.UI.ViewModels.Editor.ChordUseMarks(chart.Editor));
        services.AddSingleton<DotCharter.Services.Audio.Coordination.IEditorTransport>(chart.Transport);
        services.AddSingleton<DotCharter.Services.Editing.Tempo.ITempoService>(chart.Tempo);
        services.AddSingleton<IHistoryService>(chart.History);
        services.AddSingleton(chart.State);
        services.AddSingleton<IConfigurationService>(new FakeConfigurationService());
        services.AddSingleton<ILocalizationService>(LocalizationService.Instance);
        services.AddSingleton<IAppLogger>(new FakeAppLogger());
        services.AddSingleton(new DotCharter.Services.Editing.Quality.ChartQualityReporter(
            new ArrangementValidationService(new FakeAppLogger())));
        // The report's checks land where they ran (#690), so a command's notifications are observed with it,
        // not raced against a pool thread; the off-thread path is ChartQualityReportOffThreadTests'.
        services.AddSingleton(Dialogs.ChartQualityReportViewModelTests.Checks());
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.Linting.IAudioChartLintService>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Tone.Audio.Abstractions.IPlaybackTransport>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy);
        services.AddSingleton<DotCharter.Services.Editing.Tempo.RecordingAlignment.IRecordingAlignmentService>(
            new DotCharter.Services.Editing.Tempo.RecordingAlignment.RecordingAlignmentService(new DotCharter.Services.Editing.Tempo.BeatResyncService(),
                chart.History, new FakeConfigurationService(), chart.Transport, chart.Editor, new FakeAppLogger()));
        services.AddSingleton<DotCharter.Services.Update.IAppUpdateService>(
            new Settings.StubUpdateService(DotCharter.Services.Update.UpdateCheckStatus.UpToDate));
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IGlobalEditingCoordinator>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Ai.Generate.IToneCreator>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Ai.Generate.IRsToneFeedbackLoop>().Proxy);
        services.AddSingleton<DotCharter.Services.Editing.ChordShapes.IChordShapePlacementService>(
            Services.Editing.ChordShapes.ChordShapePlacementServiceTests.Build(chart.State, chart.Editor.Selection, chart.History));
        services.AddSingleton<DotCharter.Presentation.Contracts.Shell.IShortcutCatalog>(new OneShortcut());
        services.AddSingleton<DotCharter.Tone.Infrastructure.Storage.IFileStorageService>(new FakeFileStorageService());
        services.AddSingleton<DotCharter.Tone.Infrastructure.Storage.IExportTargetWriter>(
            new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter());
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Import.Audio.IChartFromAudioStemLocator>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Audio.StemSeparation.ISongStemSeparation>().Proxy);
        services.AddSingleton<DotCharter.Services.Import.Audio.IChartFromAudioAnalyzer>(
            new DotCharter.Services.Import.Audio.ChartFromAudioAnalyzer());
        services.AddSingleton(new DotCharter.Services.Editing.DraftMerge.DraftMergeReviewHost(chart.History,
            new DotCharter.Services.Editing.FretAnchors.FretAnchorGenerationService()));
        services.AddSingleton<DotCharter.Services.Diagnostics.IMessagePrompt>(new Dialogs.ScriptedPrompt());
        services.AddSingleton<DotCharter.Services.State.Checkpoints.ICheckpointService>(
            new Dialogs.CheckpointsViewModelTests.FakeCheckpointService());
        services.AddSingleton<DotCharter.Services.Infrastructure.Documents.IProjectDocumentService>(
            new Dialogs.CheckpointsViewModelTests.FakeDocumentService());
        services.AddSingleton<DotCharter.Services.Session.IProjectSessionService>(
            new Dialogs.CheckpointsViewModelTests.FakeSession());
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Infrastructure.LibraryDoctor.ILibraryDoctorService>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.Linting.IBatchChartLintService>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Infrastructure.Shell.IBundledDocuments>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Tone.Infrastructure.Shell.IShellLauncher>().Proxy);
        services.AddSingleton<DotCharter.Services.Package.DcSong.IDcSongExporter>(new Dialogs.FakeDcSongExporter());
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Export.DlcBuilder.IDlcBuilderProjectWriter>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Tone.Infrastructure.Shell.ISystemClipboard>().Proxy);
        services.AddSingleton<DotCharter.Services.Import.GuitarPro.GpImportApplier>();
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Infrastructure.Recovery.ICrashRecoveryService>().Proxy);
        services.AddSingleton(new DotCharter.Services.Audio.Tuning.LiveTuner(
            new FakeAudioPlaybackService(), chart.State, new TestUiDispatcher(), new FakeAppLogger()));
        services.AddSingleton<DotCharter.Services.Editing.Events.IEventEditingService>(
            new DotCharter.Services.Editing.Events.EventEditingService(chart.State,
                new DotCharter.Services.Editing.Selection.SelectionService(), chart.History,
                new DotCharter.Services.Editing.EditingScope(chart.State), new FakeAppLogger()));
        services.AddSingleton<DotCharter.Tone.Diagnostics.IUiDispatcher>(new TestUiDispatcher());
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Session.INewProjectSources>().Proxy);
        services.AddSingleton<DotCharter.Services.Editing.Selection.ISelectionService>(
            new DotCharter.Services.Editing.Selection.SelectionService());
        services.AddSingleton(sp => new DotCharter.Services.Editing.Selection.Patterns.MatchPatternStore(
            sp.GetRequiredService<IConfigurationService>()));
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Infrastructure.Input.IKeybindDispatch>().Proxy);
        services.AddSingleton<DotCharter.Services.Audio.Calibration.ICalibrationAudio>(
            new DotCharter.Services.Audio.Calibration.CalibrationAudio(new FakeAudioPlaybackService()));
        services.AddSingleton<DotCharter.Services.Infrastructure.Graphics.IGraphicsDeviceSettings>(
            new Settings.GraphicsStubs.FakeDevice());
        var chain = new DotCharter.Tone.Audio.Dsp.NullDspChainService();
        services.AddSingleton<DotCharter.Tone.Audio.Dsp.IDspChainService>(chain);
        services.AddSingleton(new DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigApplier(chain));
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Import.Youtube.IYtDlpAudioDownloader>().Proxy);
        services.AddSingleton(Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.Tempo.IBeatResyncService>().Proxy);
    }

    private sealed class OneShortcut : DotCharter.Presentation.Contracts.Shell.IShortcutCatalog
    {
        public IReadOnlyList<DotCharter.Presentation.Contracts.Shell.ShortcutEntry> CurrentShortcuts() =>
            [new("File", "Save project", "Ctrl+S")];
    }

    // ── probes ───────────────────────────────────────────────────────────

    public interface IProbeScreen : INotifyPropertyChanged
    {
        string Label { get; }

        int Count { get; }

        ICommand BumpCommand { get; }

        ICommand? MissingCommand { get; }
    }

    private sealed class SilentProbe : ObservableObject, IProbeScreen
    {
        public SilentProbe() => BumpCommand = new RelayCommand(() =>
        {
            Count++;
            OnPropertyChanged("Cuont");
        });

        public string Label => "probe";

        public int Count { get; private set; }

        public ICommand BumpCommand { get; }

        public ICommand? MissingCommand => null;
    }

    private sealed class HonestProbe : ObservableObject, IProbeScreen
    {
        private int _count;

        public HonestProbe() => BumpCommand = new RelayCommand(() => Count++);

        public string Label => "probe";

        public int Count
        {
            get => _count;
            private set => SetProperty(ref _count, value);
        }

        public ICommand BumpCommand { get; }

        public ICommand? MissingCommand { get; } = new RelayCommand(() => { });
    }

    [Fact]
    public void Probe_a_silent_change_a_misspelt_notification_and_a_null_command_are_each_named()
    {
        var violations = Violations(typeof(IProbeScreen), new SilentProbe());

        Assert.Contains(violations, v => v.Contains("MissingCommand", StringComparison.Ordinal) && v.Contains("null", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Count", StringComparison.Ordinal) && v.Contains("BumpCommand", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Cuont", StringComparison.Ordinal));
        Assert.Equal(3, violations.Count);
    }

    [Fact]
    public void Probe_an_honest_screen_passes() => Assert.Empty(Violations(typeof(IProbeScreen), new HonestProbe()));

    // ── the rule ─────────────────────────────────────────────────────────

    internal static List<string> Violations(Type contract, object screen)
    {
        var violations = new List<string>();
        if (!contract.IsInstanceOfType(screen))
            return [$"{screen.GetType().Name} does not implement {contract.Name}"];

        var properties = Properties(contract);
        var before = Snapshot(properties, screen, violations);

        foreach (var property in properties.Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType)))
        {
            if (before.GetValueOrDefault(property.Name) is not ICommand command || !command.CanExecute(null)) continue;

            var raised = new List<string?>();
            PropertyChangedEventHandler record = (_, e) => raised.Add(e.PropertyName);
            ((INotifyPropertyChanged)screen).PropertyChanged += record;
            try
            {
                command.Execute(null);
                // An async verb changes state after Execute returns; its notifications
                // belong to THIS command, so the window stays open until it finishes.
                if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand { ExecutionTask: { } running }
                    && !running.Wait(TimeSpan.FromSeconds(60)))
                {
                    violations.Add($"{property.Name} did not finish within 60 s of being executed");
                }
            }
            catch (AggregateException ex) when (ex.InnerException is { } inner and not Xunit.Sdk.XunitException)
            {
                violations.Add($"{property.Name} threw {inner.GetType().Name} when executed with no parameter: {inner.Message}");
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                violations.Add($"{property.Name} threw {ex.GetType().Name} when executed with no parameter: {ex.Message}");
            }
            finally
            {
                ((INotifyPropertyChanged)screen).PropertyChanged -= record;
            }

            var after = Snapshot(properties, screen, []);
            var all = raised.Any(string.IsNullOrEmpty);
            foreach (var p in properties)
            {
                if (!all && !raised.Contains(p.Name) && !SameValue(before.GetValueOrDefault(p.Name), after.GetValueOrDefault(p.Name)))
                    violations.Add($"{p.Name} changed when {property.Name} ran, but no PropertyChanged named it");
            }

            foreach (var name in raised.Where(n => !string.IsNullOrEmpty(n)).Distinct())
            {
                if (screen.GetType().GetProperty(name!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is null
                    && properties.All(p => p.Name != name))
                {
                    violations.Add($"{property.Name} raised PropertyChanged(\"{name}\"), which is no property of {screen.GetType().Name}");
                }
            }

            before = after;
        }

        return violations;
    }

    private static List<PropertyInfo> Properties(Type contract) =>
        new[] { contract }.Concat(contract.GetInterfaces())
            .Where(t => t != typeof(INotifyPropertyChanged))
            .SelectMany(t => t.GetProperties())
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

    private static Dictionary<string, object?> Snapshot(List<PropertyInfo> properties, object screen, List<string> violations)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in properties)
        {
            object? value;
            try
            {
                value = p.GetValue(screen);
            }
            catch (TargetInvocationException ex)
            {
                violations.Add($"{p.Name} threw {ex.InnerException?.GetType().Name} when read");
                continue;
            }

            if (value is null && (typeof(ICommand).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string)
                                  || (typeof(IEnumerable).IsAssignableFrom(p.PropertyType) && !IsNullable(p))))
            {
                violations.Add($"{p.Name} is null");
            }

            if (value is IEnumerable items and not string && items.Cast<object?>().Any(i => i is null))
                violations.Add($"{p.Name} holds a null item");
            values[p.Name] = value is IEnumerable list and not string ? list.Cast<object?>().ToList() : value;
        }

        return values;
    }

    private static bool IsNullable(PropertyInfo p) =>
        new NullabilityInfoContext().Create(p).ReadState == NullabilityState.Nullable;

    private static bool SameValue(object? a, object? b) =>
        a is List<object?> x && b is List<object?> y ? x.SequenceEqual(y) : Equals(a, b);
}
