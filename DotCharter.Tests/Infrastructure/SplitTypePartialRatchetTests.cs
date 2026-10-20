using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every product type split across three or more partial files, with the exact
///     set of files that may declare it and a ceiling on the CLASS total — the
///     summed size of those files, CR bytes not counted. A partial makes the FILE
///     small, not the CLASS (root <c>AGENTS.md</c>, "Split types"), and a new
///     partial is the easy way past <see cref="SourceFileSizeBudgetTests" />.
///     <para>
///         <b>The table may only tighten.</b> A file set may only SHRINK: a new
///         partial fails (extract a collaborator instead), and a partial that went
///         away fails until its entry is deleted. A ceiling may only go DOWN: when a
///         type shrinks by more than <see cref="MaxSlackBytes" />, lower its ceiling
///         in the same change; never raise one to turn the gate green. A type that
///         reaches three partials and is not listed fails too.
///     </para>
///     <para>
///         Each listed file opens with a one-line <c>//</c> header saying what it
///         holds (after the BOM, before any <c>using</c>), so <c>head -1</c> over
///         the set finds the sibling to open. <c>docs/agents/MAP.md</c>'s class-total
///         column is read off this table.
///     </para>
/// </summary>
public sealed partial class SplitTypePartialRatchetTests
{
    private const int MaxSlackBytes = 8 * 1024;

    private const int SplitThreshold = 3;

    private sealed record SplitType(string Type, string Folder, int CeilingBytes, string Reason, string[] Files)
    {
        public string Project => Folder.Split('/')[0];

        public IEnumerable<string> Paths => Files.Select(file => $"{Folder}/{file}");
    }

    // Ceilings were measured 2026-10-11 (#660): the class total plus ~2 KiB, rounded up to a KiB.
    private static readonly SplitType[] Splits =
    [
        new("JuceAudioPlaybackService", "DotCharter.Tone/Audio", 298 * 1024,
            "one engine handle under one _gate: every partial reads the root's shared fields, and the rebuild replays all of them; " +
                "the health monitor, the chart synth's request and the re-amp takes left as collaborators (#838)",
            ["JuceAudioPlaybackService.cs", "JuceAudioPlaybackService.ChartSynth.cs", "JuceAudioPlaybackService.DeviceWatchdog.cs",
                "JuceAudioPlaybackService.Devices.cs", "JuceAudioPlaybackService.EngineConfig.cs",
                "JuceAudioPlaybackService.EngineLifecycle.cs", "JuceAudioPlaybackService.Eq.cs",
                "JuceAudioPlaybackService.Media.cs", "JuceAudioPlaybackService.Midi.cs",
                "JuceAudioPlaybackService.Mixer.cs", "JuceAudioPlaybackService.PlayerRouting.cs",
                "JuceAudioPlaybackService.Playhead.cs", "JuceAudioPlaybackService.Reamp.cs", "JuceAudioPlaybackService.RigMidi.cs",
                "JuceAudioPlaybackService.Sandbox.cs", "JuceAudioPlaybackService.Stems.cs",
                "JuceAudioPlaybackService.Transport.cs", "JuceAudioPlaybackService.VstChain.cs"]),
        new("PlayModeViewModel", "DotCharter.UI/ViewModels/PlayMode", 148 * 1024,
            "the screens share one CurrentScreen and the root's injected fields; a screen leaves as a collaborator (ToneRig, Settings, SongSelect, RunResults, ListenBack)",
            ["PlayModeViewModel.cs", "PlayModeViewModel.Flow.cs", "PlayModeViewModel.FrontDoor.cs", "PlayModeViewModel.Gameplay.cs",
                "PlayModeViewModel.GetReady.cs", "PlayModeViewModel.HomeNavigation.cs", "PlayModeViewModel.Keys.cs",
                "PlayModeViewModel.Launch.cs", "PlayModeViewModel.Overlays.cs", "PlayModeViewModel.Pause.cs",
                "PlayModeViewModel.Results.cs", "PlayModeViewModel.RunControl.cs", "PlayModeViewModel.Screens.cs",
                "PlayModeViewModel.Tones.cs"]),
        new("ChartMaintenanceService", "DotCharter.Services/Editing/Maintenance", 98 * 1024,
            "the chart verbs behind one IChartMaintenanceService, all recording through the root's history and snapshot-undo actions; " +
                "the beat-grid math (TempoMapBeats), Add silence (ProjectSilencePadder), the note/chord conversions (ChordConversionCommands, " +
                "PowerChordFrets) and Generate FHP (FhpGenerationCommands) left as collaborators (#838)",
            ["ChartMaintenanceService.cs", "ChartMaintenanceService.ChordEditing.cs", "ChartMaintenanceService.Generation.cs",
                "ChartMaintenanceService.PowerTools.cs", "ChartMaintenanceService.Rocksmith.cs",
                "ChartMaintenanceService.Structure.cs", "ChartMaintenanceService.TempoMap.cs"]),
        new("App", "DotCharter.UI", 139 * 1024,
            "the composition root, one slice per area: ConfigureServices is the ONE object graph AppServiceGraphTests validates",
            ["App.axaml.cs", "App.AiServices.cs", "App.AudioServices.cs", "App.EditingServices.cs", "App.GameplayServices.cs",
                "App.MatchServices.cs", "App.OnlineServices.cs", "App.Shutdown.cs", "App.StateServices.cs",
                "App.ValidationServices.cs", "App.ViewModelServices.cs"]),
        new("RsToneConverter", "DotCharter.Tone/Audio/Dsp/RsToneConversion", 115 * 1024,
            "one static converter split by gear family; every partial feeds the root's slot routing, knob math and ParamMap",
            ["RsToneConverter.cs", "RsToneConverter.Amp.cs", "RsToneConverter.GameCurveAcoustic.cs",
                "RsToneConverter.GameCurveChorus.cs", "RsToneConverter.GameCurveGear.cs", "RsToneConverter.GearModels.cs",
                "RsToneConverter.RsAmp.cs"]),
        new("InspectorViewModel", "DotCharter.UI/ViewModels/Editor", 110 * 1024,
            "what is left is entangled with the selection state and the root's two mutation funnels; tabs leave as collaborators under Inspector/",
            ["InspectorViewModel.cs", "InspectorViewModel.Bends.cs", "InspectorViewModel.BulkEdits.cs",
                "InspectorViewModel.Chords.cs", "InspectorViewModel.Notes.cs", "InspectorViewModel.SelectionIssues.cs",
                "InspectorViewModel.SelectionState.cs", "InspectorViewModel.StringMask.cs"]),
        new("LivePlayViewModel", "DotCharter.UI/ViewModels/Editor", 108 * 1024,
            "the worker tick reads dozens of its siblings' members under one gate; the clusters that shared little already left (MAP.md)",
            ["LivePlayViewModel.cs", "LivePlayViewModel.Judging.cs", "LivePlayViewModel.Loops.cs", "LivePlayViewModel.Meters.cs",
                "LivePlayViewModel.Persistence.cs", "LivePlayViewModel.Session.cs", "LivePlayViewModel.Tick.cs",
                "LivePlayViewModel.Tuning.cs", "LivePlayViewModel.UiState.cs"]),
        new("PedalboardViewModel", "DotCharter.Tone.UI/ViewModels/PlayMode", 104 * 1024,
            "why each partial stays is the shared-member table in docs/agents/ui/pedalboard.md; collaborators are declared in the root",
            ["PedalboardViewModel.cs", "PedalboardViewModel.ChainEdits.cs", "PedalboardViewModel.History.cs",
                "PedalboardViewModel.MidiControls.cs", "PedalboardViewModel.NeuralAmp.cs", "PedalboardViewModel.Presets.cs",
                "PedalboardViewModel.Routing.cs", "PedalboardViewModel.Scenes.cs", "PedalboardViewModel.Seats.cs",
                "PedalboardViewModel.Spillover.cs"]),
        new("TransportViewModel", "DotCharter.UI/ViewModels/Editor", 77 * 1024,
            "the editor transport's surfaces over one audio service; the root holds every backing field. The chart synth's " +
                "instrument pickers left as Inspector/ChartSynthInstrumentViewModel (#838)",
            ["TransportViewModel.cs", "TransportViewModel.ChartSynth.cs", "TransportViewModel.Mixer.cs",
                "TransportViewModel.Popover.cs", "TransportViewModel.Vst.cs"]),
        new("AmpFxRigViewModel", "DotCharter.Tone.UI/ViewModels/PlayMode", 89 * 1024,
            "the rig screen's own state over the one PedalboardViewModel; the partials read the root's selection and chain",
            ["AmpFxRigViewModel.cs", "AmpFxRigViewModel.BoardSegments.cs", "AmpFxRigViewModel.Details.cs",
                "AmpFxRigViewModel.GearLibrary.cs"]),
        new("RsTonePlaybackCoordinator", "DotCharter.Services/Audio/Coordination", 79 * 1024,
            "ONE rig owner at a time: levelling and the tone edit act on the driver state the root holds",
            ["RsTonePlaybackCoordinator.cs", "RsTonePlaybackCoordinator.Levelling.cs", "RsTonePlaybackCoordinator.ToneEdit.cs"]),
        new("MainWindow", "DotCharter.UI/Views", 78 * 1024,
            "one window's code-behind over one namescope; four former partials are already collaborators under Views/Shell/",
            ["MainWindow.axaml.cs", "MainWindow.Docking.cs", "MainWindow.Input.cs", "MainWindow.WindowChrome.cs"]),
        new("FrameSnapshot", "DotCharter.Rendering/Data", 75 * 1024,
            "one per-frame projection on the render hot path; every partial fills the same struct arrays",
            ["FrameSnapshot.cs", "FrameSnapshot.Build.cs", "FrameSnapshot.Events.cs", "FrameSnapshot.Links.cs",
                "FrameSnapshot.Notes.cs"]),
        new("MainWindowViewModel", "DotCharter.UI/ViewModels/Shell", 73 * 1024,
            ".Layout's panel flags ride the shell's ONE PropertyChanged stream; menus leave as per-surface view models",
            ["MainWindowViewModel.cs", "MainWindowViewModel.Destinations.cs", "MainWindowViewModel.Editing.cs",
                "MainWindowViewModel.Layout.cs"]),
        new("ToneDesignViewModel", "DotCharter.UI/ViewModels/PlayMode", 62 * 1024,
            "this app's two blocks over the shared ToneDesignCoreViewModel; the section flow already lives in ToneSectionsViewModel",
            ["ToneDesignViewModel.cs", "ToneDesignViewModel.Match.cs", "ToneDesignViewModel.Sections.cs"]),
        new("RigDockUnit", "DotCharter.Tone.UI/ViewModels/PlayMode", 60 * 1024,
            "one dock unit's projections of the same PedalNodeViewModel, one partial per editor face",
            ["RigDockUnit.cs", "RigDockUnit.Cabinet.cs", "RigDockUnit.CaptureSearch.cs", "RigDockUnit.Details.cs",
                "RigDockUnit.Enclosure.cs", "RigDockUnit.Faces.cs", "RigDockUnit.Families.cs", "RigDockUnit.Mic.cs"]),
        new("RigSampler", "DotCharter.Services/Ai/Match", 57 * 1024,
            "one sampler in three parts (vocabulary, knob priors, algorithm), each header numbering the part",
            ["RigSampler.cs", "RigSampler.Knobs.cs", "RigSampler.Vocabulary.cs"]),
        new("JuceDspChainService", "DotCharter.Tone/Audio/Dsp", 33 * 1024,
            "metering and spillover share the chain's engine lifetime gate with the root",
            ["JuceDspChainService.cs", "JuceDspChainService.Metering.cs", "JuceDspChainService.Spillover.cs"]),
    ];

    [Fact]
    public void EachSplitTypeIsDeclaredInExactlyItsListedPartials()
    {
        var root = TestPaths.RepoRoot();
        var failures = new List<string>();

        foreach (var split in Splits)
        {
            var declaring = DeclaringFiles(root, split.Project, split.Type);
            var allowed = split.Paths.ToArray();
            var added = declaring.Except(allowed, StringComparer.Ordinal).ToArray();
            var gone = allowed.Except(declaring, StringComparer.Ordinal).ToArray();

            failures.AddRange(added.Select(file => $"{split.Type}: new partial {file} — extract a collaborator instead"));
            failures.AddRange(gone.Select(file => $"{split.Type}: {file} no longer declares it — delete its entry"));
        }

        Assert.True(failures.Count == 0,
            "A split type's partial set may only shrink:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void NoSplitTypeOutgrowsItsClassTotalCeiling()
    {
        var root = TestPaths.RepoRoot();
        var failures = Splits
            .Select(split => (split, total: ClassTotal(root, split)))
            .Where(x => x.total > x.split.CeilingBytes)
            .Select(x => $"{x.split.Type}: {x.total:N0} bytes, ceiling {x.split.CeilingBytes:N0}")
            .ToArray();

        Assert.True(failures.Length == 0,
            "These classes outgrew their ceiling. Move a cluster into a real collaborator, "
            + "not a new partial and not a higher ceiling:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryClassTotalCeilingIsTight()
    {
        var root = TestPaths.RepoRoot();
        var failures = Splits
            .Select(split => (split, total: ClassTotal(root, split)))
            .Where(x => x.split.CeilingBytes - x.total > MaxSlackBytes)
            .Select(x => $"{x.split.Type}: {x.total:N0} bytes against a ceiling of {x.split.CeilingBytes:N0}, lower the ceiling")
            .ToArray();

        Assert.True(failures.Length == 0,
            "A class-total ceiling may only go down. When a type shrinks, lower its ceiling "
            + "(and MAP.md's class-total column):\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryListedPartialOpensWithAOneLineHeader()
    {
        var root = TestPaths.RepoRoot();
        var failures = Splits
            .SelectMany(split => split.Paths)
            .Where(path => File.Exists(Path.Combine(root, path)))
            .Where(path => !OpensWithHeader(Path.Combine(root, path)))
            .ToArray();

        Assert.True(failures.Length == 0,
            "These partials do not open with a one-line `//` header saying what they hold "
            + "(first line after the BOM, before any `using`):\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryTypeSplitAcrossThreeOrMoreFilesIsListed()
    {
        var root = TestPaths.RepoRoot();
        var listed = Splits.Select(split => (split.Project, split.Type)).ToHashSet();
        var unlisted = Directory.EnumerateDirectories(root, "DotCharter.*")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(project => project != "DotCharter.Tests")
            .SelectMany(project => PartialDeclarations(root, project)
                .GroupBy(d => d.Type, StringComparer.Ordinal)
                .Where(g => g.Count() >= SplitThreshold)
                .Select(g => (Project: project, Type: g.Key, Count: g.Count())))
            .Where(x => !listed.Contains((x.Project, x.Type)))
            .Select(x => $"{x.Project}: {x.Type} ({x.Count} files)")
            .ToArray();

        Assert.True(unlisted.Length == 0,
            $"These types are split across {SplitThreshold}+ partial files and are not in the table — "
            + "list them with a reason and a ceiling, or fold them into collaborators:\n  " + string.Join("\n  ", unlisted));
    }

    [Fact]
    public void TheTableIsWellFormed()
    {
        Assert.Equal(Splits.Length, Splits.Select(s => (s.Project, s.Type)).Distinct().Count());
        Assert.All(Splits, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason), s.Type));
        Assert.All(Splits, s => Assert.True(s.Files.Length >= SplitThreshold, s.Type));
        Assert.All(Splits, s => Assert.Equal(s.Files.Length, s.Files.Distinct(StringComparer.Ordinal).Count()));
        Assert.All(Splits, s => Assert.All(s.Files, f => Assert.StartsWith(s.Type + ".", f, StringComparison.Ordinal)));
    }

    private static string[] DeclaringFiles(string root, string project, string type) =>
    [
        .. PartialDeclarations(root, project)
            .Where(d => d.Type == type)
            .Select(d => d.Path)
            .Order(StringComparer.Ordinal),
    ];

    private static readonly Dictionary<string, (string Type, string Path)[]> DeclarationCache = [];

    private static (string Type, string Path)[] PartialDeclarations(string root, string project)
    {
        lock (DeclarationCache)
        {
            if (DeclarationCache.TryGetValue(project, out var cached)) return cached;

            var found = Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                .Where(relative => !relative.Contains("/bin/", StringComparison.Ordinal)
                    && !relative.Contains("/obj/", StringComparison.Ordinal))
                .SelectMany(relative => PartialDeclaration()
                    .Matches(SourceText.StripComments(File.ReadAllText(Path.Combine(root, relative))))
                    .Select(m => m.Groups["type"].Value)
                    .Distinct(StringComparer.Ordinal)
                    .Select(type => (Type: type, Path: relative)))
                .ToArray();
            DeclarationCache[project] = found;
            return found;
        }
    }

    private static int ClassTotal(string root, SplitType split) =>
        split.Paths
            .Select(path => Path.Combine(root, path))
            .Where(File.Exists)
            .Sum(NormalizedSize);

    private static int NormalizedSize(string file)
    {
        var bytes = File.ReadAllBytes(file);
        return bytes.Length - bytes.Count(b => b == (byte)'\r');
    }

    private static bool OpensWithHeader(string file)
    {
        using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
        var first = reader.ReadLine() ?? "";
        return first.StartsWith("// ", StringComparison.Ordinal) && first.Length > "// ".Length;
    }

    [GeneratedRegex(@"\bpartial\s+(?:class|record|struct)\s+(?<type>\w+)")]
    private static partial Regex PartialDeclaration();
}
