using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The rig's "From library…" pickers: the pure extension-filter parse, the
///     <see cref="PedalboardViewModel.ListLibraryChoices"/> projection over the
///     local capture library, the card strip's
///     <see cref="PedalStringParamViewModel.SelectedLibraryChoice"/> load path,
///     and the module dock's per-face capture column. No Avalonia instance.
/// </summary>
public sealed class RigLibraryChoiceTests
{
    // ── the pure wildcard-filter parse ───────────────────────────────────────

    [Theory]
    [InlineData("*.nam;*.aidax", new[] { ".nam", ".aidax" })]
    [InlineData("*.wav", new[] { ".wav" })]
    [InlineData(" *.NAM ; *.nam ", new[] { ".nam" })] // trimmed, case-folded, deduped
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    [InlineData("phrases", new string[0])] // a pattern with no extension accepts nothing
    public void ExtensionsFrom_parses_the_wildcard_list(string? filter, string[] expected)
        => Assert.Equal(expected, RigDockUnit.ExtensionsFrom(filter));

    // ── ListLibraryChoices: filter, title fallback, order ────────────────────

    [Fact]
    public void ListLibraryChoices_filters_by_extension_and_orders_by_title()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi"));
        lib.Entries.Add(Entry(@"X:\nam\1-alpha.aidax", title: "Alpha Lead", format: "aidax"));
        lib.Entries.Add(Entry(@"X:\nam\3-cab-ir.wav", title: "412 Greenback", format: "ir"));
        lib.Entries.Add(Entry(@"X:\nam\4-untitled.nam", title: "")); // → file-name fallback

        var vm = NewVm(lib, out _);

        // Neural-amp filter: the two .nam + the .aidax, never the IR; titled
        // from the manifest with the bare file name standing in when it is
        // blank; ordered by title ("4-untitled" sorts before the letters).
        Assert.Equal(
            new[] { "4-untitled", "Alpha Lead", "Zulu Plexi" },
            vm.ListLibraryChoices("*.nam;*.aidax").Select(c => c.Title).ToArray());

        // Cab filter: only the .wav IR.
        var irs = vm.ListLibraryChoices("*.wav");
        var ir = Assert.Single(irs);
        Assert.Equal("412 Greenback", ir.Title);
        Assert.Equal(@"X:\nam\3-cab-ir.wav", ir.FilePath);

        // A free-text param (no wildcard) is offered nothing.
        Assert.Empty(vm.ListLibraryChoices(""));
    }

    [Fact]
    public void WithoutALibrary_NoChoicesAreOffered_AndTheDropdownHides()
    {
        var vm = NewVm(library: null, out _);
        var amp = Add(vm, "namamp");
        var sp = amp.StringParams.Single();
        Assert.Empty(sp.LibraryChoices);
        Assert.False(sp.HasLibraryChoices);
    }

    // ── the card strip's picker: load, mirror, revert ────────────────────────

    [Fact]
    public void Selecting_a_choice_pushes_the_file_through_SetNodeString_and_keeps_the_selection()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi"));
        var vm = NewVm(lib, out var dsp);
        var amp = Add(vm, "namamp");
        var sp = amp.StringParams.Single();

        Assert.True(sp.HasLibraryChoices);
        Assert.Null(sp.SelectedLibraryChoice); // nothing loaded yet

        var choice = sp.LibraryChoices.Single();
        sp.SelectedLibraryChoice = choice;

        Assert.Equal((amp.NodeId, "modelPath", choice.FilePath), dsp.StringSets.Last());
        Assert.Equal(choice.FilePath, sp.Value);
        Assert.Same(choice, sp.SelectedLibraryChoice);
        // Exactly ONE native push: Commit's mirror must not re-load the file.
        Assert.Equal(1, dsp.StringSets.Count(s => s.ParamId == "modelPath"));

        // Clearing reverts the value AND the dropdown, without a re-load.
        sp.ClearCommand.Execute(null);
        Assert.Equal(string.Empty, sp.Value);
        Assert.Null(sp.SelectedLibraryChoice);
    }

    [Fact]
    public void ANativeReject_RevertsTheSelection_SoTheDropdownNeverLies()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi"));
        var vm = NewVm(lib, out var dsp);
        var amp = Add(vm, "namamp");
        var sp = amp.StringParams.Single();

        dsp.FailSetString = true;
        sp.SelectedLibraryChoice = sp.LibraryChoices.Single();

        Assert.Equal(string.Empty, sp.Value); // Commit's revert-on-fail kept the old value
        Assert.Null(sp.SelectedLibraryChoice); // and the dropdown followed it back
    }

    [Fact]
    public void ASeededValue_PreselectsItsLibraryRow()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi"));
        var vm = NewVm(lib, out var dsp);
        var amp = Add(vm, "namamp");
        // The value lands natively, then the board is rebuilt (the Sync path a
        // reopened board takes) — the fresh card must show the row as selected.
        Assert.True(dsp.SetNodeString(amp.NodeId, "modelPath", @"x:\NAM\2-ZULU.nam")); // case-insensitive match
        vm.Sync();

        var sp = vm.Nodes.Single().StringParams.Single();
        Assert.NotNull(sp.SelectedLibraryChoice);
        Assert.Equal(@"X:\nam\2-zulu.nam", sp.SelectedLibraryChoice!.FilePath);
    }

    /// <summary>The incremental Sync (0.59) keeps a card whose node survives —
    /// so the surviving card must re-materialize its library list on Sync, or
    /// a capture downloaded after the card was built would never show in its
    /// "From library…" dropdown. The old full teardown got this for free by
    /// rebuilding every card.</summary>
    [Fact]
    public void ASurvivingCard_LearnsAboutANewlyDownloadedCapture_OnSync()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi"));
        var vm = NewVm(lib, out _);
        var amp = Add(vm, "namamp");
        var sp = amp.StringParams.Single();
        Assert.Single(sp.LibraryChoices);

        lib.Entries.Add(Entry(@"X:\nam\1-alpha.aidax", title: "Alpha Lead", format: "aidax"));
        vm.Sync();

        Assert.Same(amp, vm.Nodes.Single());     // the card SURVIVED the sync…
        Assert.Equal(2, sp.LibraryChoices.Count); // …and still sees the new capture
    }

    // ── the module dock's capture column, per face ───────────────────────────

    [Fact]
    public void TheDock_ListsOnlyFilesTheUnitAccepts_OnEveryFace()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi"));
        lib.Entries.Add(Entry(@"X:\nam\3-cab-ir.wav", title: "412 Greenback", format: "ir"));
        var vm = NewVm(lib, out _);

        // The cab face lists the library's IRs (and only those)…
        var cab = new RigDockUnit(Add(vm, "cab"), lib);
        Assert.True(cab.IsCabFace);
        var cabRow = Assert.Single(cab.Captures);
        Assert.EndsWith(".wav", cabRow.FilePath, StringComparison.Ordinal);
        // …while the NAM status text and the library-empty note stay amp-only.
        Assert.False(cab.HasCaptureColumn);
        Assert.False(cab.ShowsEmptyCaptureNote);

        // The amp face never offers a .wav — a native reject waiting to happen.
        var amp = new RigDockUnit(Add(vm, "namamp"), lib);
        var ampRow = Assert.Single(amp.Captures);
        Assert.EndsWith(".nam", ampRow.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ACabWithNoFittingFiles_ShowsNeitherListNorEmptyNote()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi")); // captures only, no IRs
        var vm = NewVm(lib, out _);

        var cab = new RigDockUnit(Add(vm, "cab"), lib);
        Assert.Empty(cab.Captures);
        Assert.False(cab.HasCaptures);
        // "Nothing in your library yet" is a sentence about CAPTURES — on a cab
        // it would deny the twelve built-in IRs the unit is already running.
        Assert.False(cab.ShowsEmptyCaptureNote);
    }

    // ── the cab plate: built-in IR name, never "No capture loaded" ───────────

    [Fact]
    public void ACabsEmptyIrPath_NamesTheBuiltInIr_NotNoCapture()
    {
        var vm = NewVm(library: null, out _);
        var sp = Add(vm, "cab").StringParams.Single();

        // The cab is fully configured with no user IR: its `model` param picks
        // one of the twelve built-ins. The plate says WHICH, not "no capture".
        Assert.Equal(
            FakeLocalization.English().Format("Rig_BuiltInIr", "1x12 Combo"),
            sp.CapturePlateText);
    }

    [Fact]
    public void ChangingTheCabModel_RetitlesThePlateLive()
    {
        var vm = NewVm(library: null, out _);
        var cab = Add(vm, "cab");
        var sp = cab.StringParams.Single();

        var plateChanges = 0;
        sp.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PedalStringParamViewModel.CapturePlateText)) plateChanges++;
        };

        cab.Params.First(p => p.Id == "model").Value = 2f; // 4x12 Scooped

        Assert.True(plateChanges > 0, "the plate never heard about the model change");
        Assert.Equal(
            FakeLocalization.English().Format("Rig_BuiltInIr", "4x12 Scooped"),
            sp.CapturePlateText);
    }

    [Fact]
    public void ALoadedUserIr_TakesThePlate_AndClearFallsBackToTheBuiltIn()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\3-cab-ir.wav", title: "412 Greenback", format: "ir"));
        var vm = NewVm(lib, out _);
        var sp = Add(vm, "cab").StringParams.Single();

        sp.SelectedLibraryChoice = sp.LibraryChoices.Single();
        Assert.Equal("412 Greenback", sp.CapturePlateText);

        sp.ClearCommand.Execute(null); // "" = back to the built-in IR
        Assert.Equal(
            FakeLocalization.English().Format("Rig_BuiltInIr", "1x12 Combo"),
            sp.CapturePlateText);
    }

    [Fact]
    public void ANamampsEmptyPlate_StillSaysNoCaptureLoaded()
    {
        // On a neural amp the empty state is REAL: no capture means the signal
        // passes dry, so "No capture loaded" is the honest plate.
        var vm = NewVm(library: null, out _);
        var sp = Add(vm, "namamp").StringParams.Single();

        Assert.Equal(
            FakeLocalization.English()["Nam_NoCapture"],
            sp.CapturePlateText);
    }

    // ── Clear: the way BACK from a loaded file ───────────────────────────────

    /// <summary>
    ///     Clearing is a real native PUSH of "", not a managed reset: "" is what
    ///     tells the module to fall back to its built-in default, and a clear
    ///     that only cleared the label would leave the engine running the old
    ///     capture behind a plate that said otherwise. Asserted at the SEAM the
    ///     picker uses — same <c>SetNodeString</c>, same param id, same node.
    /// </summary>
    [Fact]
    public void Clearing_PushesTheEmptyValue_ThroughTheSameSetNodeStringThePickerUses()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi"));
        var vm = NewVm(lib, out var dsp);
        var amp = Add(vm, "namamp");
        var sp = amp.StringParams.Single();

        sp.SelectedLibraryChoice = sp.LibraryChoices.Single();
        Assert.True(sp.HasValue); // …which is the state the ✕ is visible in

        sp.ClearCommand.Execute(null);

        Assert.Equal((amp.NodeId, "modelPath", string.Empty), dsp.StringSets.Last());
        Assert.Equal(string.Empty, sp.Value);
        Assert.False(sp.HasValue); // …and the ✕ goes away with it
    }

    /// <summary>
    ///     And it inherits <c>Commit</c>'s revert-on-fail: an engine that refuses
    ///     the clear keeps running the capture, so the plate must go on naming
    ///     it. The whole point of routing Clear through Commit rather than
    ///     writing <c>Value</c>.
    /// </summary>
    [Fact]
    public void ANativeRejectOfAClear_LeavesTheFileLoaded_RatherThanLying()
    {
        var lib = new FakeLibrary();
        lib.Entries.Add(Entry(@"X:\nam\2-zulu.nam", title: "Zulu Plexi"));
        var vm = NewVm(lib, out var dsp);
        var sp = Add(vm, "namamp").StringParams.Single();

        var choice = sp.LibraryChoices.Single();
        sp.SelectedLibraryChoice = choice;

        dsp.FailSetString = true;
        sp.ClearCommand.Execute(null);

        Assert.Equal(choice.FilePath, sp.Value);
        Assert.True(sp.HasValue);
        Assert.Same(choice, sp.SelectedLibraryChoice);
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static PedalboardViewModel NewVm(FakeLibrary? library, out FakeDspChainService dsp)
    {
        dsp = new FakeDspChainService(Catalog());
        return TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English(), namLibrary: library);
    }

    private static PedalNodeViewModel Add(PedalboardViewModel vm, string moduleTypeId)
    {
        var node = vm.AddGear(vm.AvailableModules.First(m => m.Id == moduleTypeId));
        Assert.NotNull(node);
        return node!;
    }

    private static IReadOnlyList<DspModuleType> Catalog() =>
    [
        new DspModuleType("namamp", "NAM Amp", [])
            { StringParams = [new DspStringParamInfo("modelPath", "NAM Model", "*.nam;*.aidax")] },
        new DspModuleType("cab", "Cabinet",
        [
            new DspParamInfo("model", "Model", 0f, 3f, 0f)
                { ValueLabels = ["1x12 Combo", "2x12 Combo", "4x12 Scooped", "Vintage 4x12"] },
        ])
            { StringParams = [new DspStringParamInfo("irPath", "Impulse Response", "*.wav")] },
    ];

    private static NamCaptureEntry Entry(string path, string title, string format = "nam") =>
        new(new NamCaptureManifest(
                ToneId: 1, ModelId: 1, Title: title, Author: "capturist", GearType: "amp",
                Format: format, SizeTier: "standard", License: "CC-BY", LicenseUrl: "",
                AttributionUrl: "", FileName: Path.GetFileName(path), Sha256: "00",
                FileBytes: 5, DownloadedUtc: DateTimeOffset.UnixEpoch),
            path);

    private sealed class FakeLibrary : INamCaptureLibrary
    {
        public List<NamCaptureEntry> Entries { get; } = [];

        public string RootDirectory => @"X:\nam";

        public event EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<NamCaptureEntry> GetAll() => Entries.ToList();

        public bool IsModelDownloaded(long modelId) => Entries.Any(e => e.Manifest.ModelId == modelId);

        public bool IsToneDownloaded(long toneId) => Entries.Any(e => e.Manifest.ToneId == toneId);

        public Task<OnlineResult<NamCaptureEntry>> DownloadAsync(
            Tone3000Tone tone, Tone3000Model model,
            IProgress<double>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException("these tests never download");

        public bool Delete(long modelId) => false;

        public int MigrateFileExtensions() => 0;

        public bool ImportBundled(string sourceFilePath, BundledNamCapture meta) => false;
    }
}
