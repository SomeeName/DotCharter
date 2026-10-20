// The two per-parameter view-models: PedalParamViewModel (knobs and model selectors) and PedalStringParamViewModel (the file picker, the capture identity and the lyric editor).

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// What a NAMED (value-labelled) param actually IS to a player, where "a picker"
/// is the wrong answer.
/// </summary>
public enum RigParamRole
{
    /// <summary>A named choice: a model, a waveform, a mode. A picker, which is
    /// what every labelled param has always rendered as.</summary>
    Choice,

    /// <summary>A TRANSPORT: a footswitch whose state the ENGINE can move on its
    /// own (the looper auto-closes its take at <c>maxLoopS</c>). Renders as one
    /// button per state, and the current state is POLLED rather than trusted
    /// from the last write.</summary>
    Transport,

    /// <summary>A one-shot TRIGGER that reads back to its resting value: the UI
    /// writes "go", the engine performs and answers 0. A two-way picker mirror
    /// fights itself here — it snaps back and may re-fire — so this renders as a
    /// write-only momentary button that shows no state at all.</summary>
    Momentary
}

/// <summary>
///     The ONE table that says which labelled params are not pickers.
///
///     <para>
///         <b>This is a stopgap and is meant to read like one.</b> The honest
///         long-term fix is a CAPABILITY BIT in the native catalogue — a
///         <c>dca_dsp_module_param_flags</c> alongside the v34
///         <c>dca_dsp_module_type_flags</c>, so a module declares "this param is
///         a transport" the same way it declares "this module takes a key" and
///         the UI needs no table at all. Until that exists, the information
///         genuinely is not on the ABI: <c>looper.state</c> and <c>looper.undo</c>
///         are indistinguishable from <c>tremolo.shape</c> through every export
///         the engine has.
///     </para>
///     <para>
///         Keyed by (module, param), never by param id alone — the same param
///         name means different things on different pedals, which is the lesson
///         <c>PedalboardSkinReachabilityTests.ContinuousByDesign</c> already
///         records for <c>octave</c>. Keep it SHORT: every entry is a place the
///         rig stopped being catalogue-driven.
///     </para>
/// </summary>
public static class RigParamRoles
{
    private static readonly HashSet<(string Module, string Param)> TransportParams =
    [
        // UtilityGear.cpp:189 — Stop / Record / Play / Overdub, and the engine
        // moves it itself when the take reaches maxLoopS.
        ("looper", "state")
    ];

    private static readonly HashSet<(string Module, string Param)> MomentaryParams =
    [
        // UtilityGear.cpp:198 — labels { "-", "Undo" }; performs and reads back 0.
        ("looper", "undo")
    ];

    /// <summary>The role of a param on a module. Anything not in the two sets
    /// above — which is every param of every other module — is a
    /// <see cref="RigParamRole.Choice"/>, so a module added to the native
    /// catalogue tomorrow still needs nothing here.</summary>
    public static RigParamRole RoleOf(string? moduleTypeId, string? paramId)
    {
        if (moduleTypeId is null || paramId is null) return RigParamRole.Choice;
        var key = (moduleTypeId, paramId);
        if (TransportParams.Contains(key)) return RigParamRole.Transport;
        if (MomentaryParams.Contains(key)) return RigParamRole.Momentary;
        return RigParamRole.Choice;
    }

    /// <summary>Every (module, param) pair this table names — the test seam, so a
    /// lint can prove each one exists in the live native catalogue instead of
    /// rotting into a reference to a param that was renamed.</summary>
    public static IReadOnlyList<(string Module, string Param, RigParamRole Role)> All =>
        TransportParams.Select(k => (k.Module, k.Param, RigParamRole.Transport))
            .Concat(MomentaryParams.Select(k => (k.Module, k.Param, RigParamRole.Momentary)))
            .ToList();
}

/// <summary>
/// One generic parameter slider, materialized from <see cref="DspParamInfo"/>
/// metadata. Live-writes the value to the chain on every change.
/// </summary>
public sealed partial class PedalParamViewModel : ObservableObject
{
    private readonly IDspChainService _dsp;
    private readonly int _nodeId;

    public PedalParamViewModel(IDspChainService dsp, int nodeId, DspParamInfo info, float value)
    {
        _dsp = dsp;
        _nodeId = nodeId;
        Id = info.Id;
        _displayName = info.DisplayName is { Length: > 0 } name ? name : info.Id;
        Minimum = info.Min;
        Maximum = info.Max;
        Default = info.Default;
        IsUnitRange = info.Min == 0f && info.Max == 1f;
        ValueLabels = info.ValueLabels;
        Step = info.Step;
        // Decided ONCE, from the catalogue metadata, by the pure seam — never
        // per frame and never from a per-module table. See KnobMath.TaperFor.
        Taper = KnobMath.TaperFor(info.Id, info.Min, info.Max, info.Step);
        // Snap the SEED too: a persisted preset value that fell between two steps
        // (or an older tone written before the param was declared discrete) is
        // lrounded by the native core anyway — showing it un-snapped would put a
        // number on the face that the DSP is not running.
        _value = Snap(value);
    }

    public string Id { get; }
    [ObservableProperty] private bool _isControlActive = true;
    [ObservableProperty] private string? _inactiveHint;
    /// <summary>The knob's label. Fixed by the catalogue for every module but
    /// <c>rsamp</c>, whose generic knobs take the names of whichever Rocksmith
    /// amp its model picker names (<c>PedalNodeViewModel.RelabelRsAmpKnobs</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    [NotifyPropertyChangedFor(nameof(Unit))]
    private string _displayName;

    /// <summary>The caption a hardware face prints: <see cref="DisplayName"/> without
    /// its unit suffix ("Rate", not "Rate (Hz)") — the unit goes beside the value
    /// plate instead (<see cref="Unit"/>). <c>DspControlText.SplitUnit</c> is the
    /// one place that decides what counts as a unit.</summary>
    public string Caption => DspControlText.SplitUnit(DisplayName).Caption;

    /// <summary>The unit the value is in ("Hz", "ms", "dB"), or null.</summary>
    public string? Unit => DspControlText.SplitUnit(DisplayName).Unit;
    public float Minimum { get; }
    public float Maximum { get; }
    public float Default { get; }

    /// <summary>Quantization step for an ORDINAL discrete param (ABI v27), 0 for a
    /// continuous one. See <see cref="DspParamInfo.Step"/>: the knob snaps to it,
    /// so a count never reads "6.5 stages".</summary>
    public float Step { get; }

    /// <summary>True when this param is an ordinal count (a SNAPPING knob) rather
    /// than a continuous one. Discrete-but-NAMED params are pickers instead — see
    /// <see cref="HasValueLabels"/>.</summary>
    public bool IsStepped => Step > 0f;

    /// <summary>True for normalized 0..1 params — the knob readout then shows
    /// hardware-dial 0–10 units (display only, the value stays 0..1).</summary>
    public bool IsUnitRange { get; }

    /// <summary>
    ///     How the dial's TRAVEL maps onto this param's range —
    ///     <see cref="KnobTaper.Logarithmic"/> for a frequency or a
    ///     multi-decade sweep, linear for everything else. <b>Presentation
    ///     only</b>: <see cref="Value"/>, <see cref="DisplayText"/>, the typed
    ///     entry, the step grid and what is pushed to the DSP are all in the
    ///     param's own plain units under either taper — only where the pointer
    ///     has to be to produce a value moves.
    ///     <para>
    ///         It exists because a LINEAR 20–20,000 Hz dial puts 1 kHz at 4.9 %
    ///         of travel, which puts the whole guitar range inside the first
    ///         twentieth of the sweep. <c>svf.cutoffHz</c> and <c>comb.freqHz</c>
    ///         (JuceSharp 0.9) are the modules that made it unavoidable.
    ///     </para>
    /// </summary>
    public KnobTaper Taper { get; }

    /// <summary>Named labels for a discrete "model picker" param (ABI v24: amp
    /// voicing, cab/od/dist model) — null for a continuous param (→ a knob).
    /// When present, the pedal renders a labelled selector instead of a knob.</summary>
    public IReadOnlyList<string>? ValueLabels { get; }

    /// <summary>True when this param is a model picker (has value labels).</summary>
    public bool HasValueLabels => ValueLabels is { Count: > 0 };

    /// <summary>The current value as a 0-based label index (value == Min + index;
    /// clamped to the label range).</summary>
    public int SelectedIndex
    {
        get
        {
            if (ValueLabels is not { Count: > 0 } labels) return 0;
            var i = (int)Math.Round(Value - Minimum);
            return Math.Clamp(i, 0, labels.Count - 1);
        }
        set
        {
            if (ValueLabels is not { Count: > 0 } labels) return;
            var i = Math.Clamp(value, 0, labels.Count - 1);
            Value = Minimum + i; // writes native via OnValueChanged
        }
    }

    /// <summary>The current model's label ("Plexi 100", "4x12 Scooped", …); empty
    /// for a non-labelled param.</summary>
    public string CurrentLabel =>
        ValueLabels is { Count: > 0 } labels ? labels[SelectedIndex] : string.Empty;

    private float _value;

    /// <summary>The live value. Hand-written (not <c>[ObservableProperty]</c>)
    /// because every write must pass through <see cref="Snap"/> FIRST: the value
    /// the user sees, the value cached here and the value pushed to native are
    /// then the same number. A stepped param whose knob stopped at 6.4 would
    /// otherwise display 6.4, cache 6.4 and run 6 — the native adapter lrounds it.</summary>
    public float Value
    {
        get => _value;
        set
        {
            var snapped = Snap(value);
            if (EqualityComparer<float>.Default.Equals(_value, snapped)) return;
            // native FIRST: a listener that reads the chain on "Value" (the rig's
            // undo history) must see the value it was told about
            _dsp.SetParam(_nodeId, Id, snapped);
            SetProperty(ref _value, snapped);
            OnPropertyChanged(nameof(DisplayText));
            if (HasValueLabels)
            {
                OnPropertyChanged(nameof(SelectedIndex));
                OnPropertyChanged(nameof(CurrentLabel));
            }
        }
    }

    /// <summary>Mirrors a value the SERVICE already holds — the incremental
    /// board sync updating a surviving card after a preset apply wrote the
    /// param through the chain directly. Same <see cref="Snap"/> as the live
    /// setter and the same notifications, but no native push: pushing back a
    /// value that just came FROM native would be a wasted P/Invoke per param
    /// per card on every tone switch.</summary>
    internal void MirrorValue(float value)
    {
        if (!SetProperty(ref _value, Snap(value), nameof(Value))) return;
        OnPropertyChanged(nameof(DisplayText));
        if (HasValueLabels)
        {
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(CurrentLabel));
        }
    }

    /// <summary>
    ///     The readout a dial prints for the current value: the 0–10 hardware
    ///     scale for a normalized param, the raw number otherwise. It goes
    ///     through the SAME <see cref="KnobMath.DisplayValue" /> +
    ///     <c>DspControlText.FormatValue</c> pair the rack's
    ///     <c>RotaryKnob</c> draws with, so the two skins of one rig cannot
    ///     print different numbers for one param.
    /// </summary>
    /// <summary>The value as the design prints it: the raw number ("0.5"), not
    /// a 0–10 mapping. The knob's own typed entry still accepts either.</summary>
    public string DisplayText => DspControlText.FormatValue(Value);

    /// <summary>Quantizes onto the param's discrete grid (no-op when continuous).
    /// Same math as the knob control's, so the two can never disagree.</summary>
    private float Snap(float value)
        => IsStepped
            ? (float)KnobMath.Snap(value, Minimum, Maximum, Step)
            : value;
}

/// <summary>
/// One string-valued parameter on a pedal card (ABI v22). Two shapes, keyed off
/// the catalogue <see cref="DspStringParamInfo.FileFilter"/>:
/// <list type="bullet">
/// <item>a NON-empty filter → a compact <b>file-picker</b> row (namamp
/// <c>modelPath</c>, cab/acousticsim <c>irPath</c>): the button shows the current
/// file name and opens the OS picker filtered; a clear button reverts via "".</item>
/// <item>an empty filter → the miku <c>phrases</c> <b>lyric editor</b>: a popup
/// multiline editor with a live syllable-count hint (mirrors the native parse
/// rules) that pushes the text on Save.</item>
/// </list>
/// Live-writes through <see cref="IDspChainService.SetNodeString"/>; the managed
/// value only advances on native success (mirrors the setter's revert-on-fail).
/// </summary>
public sealed partial class PedalStringParamViewModel : ObservableObject
{
    private readonly IDspChainService _dsp;
    private readonly int _nodeId;
    private readonly DotCharter.Tone.Infrastructure.Storage.IFileStorageService? _files;
    private readonly Action? _onCommitted;
    private readonly ILocalizationService _loc;

    /// <summary>Resolves a capture path to its local-library manifest (title,
    /// author, format, size tier) for the amp faceplate. Optional and always
    /// allowed to return null: a capture picked straight off disk has no sidecar,
    /// and unit tests wire none at all.</summary>
    private readonly Func<string, NamCaptureManifest?>? _captureLookup;

    /// <summary>What this param's module runs when the param is EMPTY — the
    /// cab's currently selected built-in IR name ("4x12 Greenback"), read live
    /// off the node's badge param. Null for a param whose empty state really is
    /// "nothing loaded" (a namamp with no capture passes the signal dry). The
    /// closure reads lazily on purpose: the owning node assigns its badge param
    /// AFTER the string params are built.</summary>
    private readonly Func<string?>? _builtinLabel;

    /// <summary>Native cap on parsed miku syllables (dcdsp_miku.h kMaxSyllables).</summary>
    private const int MaxSyllables = 64;
    private static readonly char[] SyllableSeparators = [' ', ',', '-', '|', '\n', '\r', '\t', ';'];

    public PedalStringParamViewModel(IDspChainService dsp, int nodeId, DspStringParamInfo info,
        string value, DotCharter.Tone.Infrastructure.Storage.IFileStorageService? files,
        ILocalizationService localization,
        Action? onCommitted = null,
        Func<string, NamCaptureManifest?>? captureLookup = null,
        Func<string, IReadOnlyList<NamCaptureChoice>>? libraryChoices = null,
        Func<string?>? builtinLabel = null)
    {
        _dsp = dsp;
        _nodeId = nodeId;
        _files = files;
        _loc = localization;
        _onCommitted = onCommitted;
        _captureLookup = captureLookup;
        _builtinLabel = builtinLabel;
        Id = info.Id;
        DisplayName = info.DisplayName is { Length: > 0 } name ? name : info.Id;
        FileFilter = info.FileFilter ?? string.Empty;
        _value = value ?? string.Empty;
        _lyricsDraft = _value;
        _identity = ResolveIdentity(_value);
        // Materialized here and re-materialized by RefreshLibraryChoices on
        // every board sync (the parent invalidates its library index there), so
        // a SURVIVING card — the incremental Sync keeps cards whose node lives —
        // still learns about a capture downloaded since it was built.
        _libraryChoicesSource = libraryChoices;
        LibraryChoices = IsFilePicker && libraryChoices != null
            ? libraryChoices(FileFilter) ?? []
            : [];
        _selectedLibraryChoice = FindLibraryChoice(_value);
    }

    private readonly Func<string, IReadOnlyList<NamCaptureChoice>>? _libraryChoicesSource;

    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>";"-separated wildcard list (e.g. "*.nam;*.aidax"); empty = free text.</summary>
    public string FileFilter { get; }

    /// <summary>A file path param → render the picker row.</summary>
    public bool IsFilePicker => FileFilter.Length > 0;

    /// <summary>A free-text param (miku phrases) → render the lyric editor.</summary>
    public bool IsLyrics => FileFilter.Length == 0;

    /// <summary>The stored value (absolute file path, or the free text).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(FileLabel))]
    private string _value;

    public bool HasValue => Value.Length > 0;

    /// <summary>The button caption for a file picker: the file name, or a
    /// "choose…" placeholder when unset.</summary>
    public string FileLabel => Value.Length > 0
        ? System.IO.Path.GetFileName(Value)
        : _loc["Dsp_NoFileChosen"];

    // ---- capture identity (the neural amp head's faceplate model plate) ------

    private NamCaptureIdentity _identity;

    /// <summary>Re-resolves the identity for a path: the local library's manifest
    /// when the capture came from there, else whatever the file NAME can prove.
    /// Throw-free — a broken library must not take the faceplate down with it.</summary>
    private NamCaptureIdentity ResolveIdentity(string path)
    {
        if (!IsFilePicker || path.Length == 0) return NamCaptureIdentity.None;

        NamCaptureManifest? manifest = null;
        try { manifest = _captureLookup?.Invoke(path); }
        catch { /* an unreadable library degrades to the file-name identity */ }

        return NamCaptureNaming.Resolve(
            path, manifest?.Title, manifest?.Author, manifest?.Format, manifest?.SizeTier);
    }

    /// <summary>The capture's MODEL NAME as it should read on an amp faceplate
    /// ("Deluxe Reverb Clean"), not a file name. Empty when nothing is loaded.</summary>
    public string CaptureTitle => _identity.Title;

    /// <summary>"by &lt;author&gt;" — who made the capture. Empty unless the local
    /// library knows (a manifest sidecar is the only place the author exists).</summary>
    public string CaptureByline => _identity.HasAuthor
        ? _loc.Format("Nam_CaptureBy", _identity.Author)
        : string.Empty;

    public bool HasCaptureByline => _identity.HasAuthor;

    /// <summary>The stamped format/size chip beside the model name ("NAM · LSTM").
    /// Proper nouns, deliberately not localized. Empty when unknown.</summary>
    public string CaptureFormatLabel => _identity.FormatLabel;

    public bool HasCaptureFormat => _identity.FormatLabel.Length > 0;

    /// <summary>The faceplate plate text: the loaded file's name; for an empty
    /// value, what the module ACTUALLY runs — the cab's built-in IR name via
    /// <see cref="_builtinLabel"/> ("Built-in · 4x12 Greenback"), because a cab
    /// with no user IR is fully configured, and "No capture loaded" on it read
    /// as a fault for several releases; else the localized "nothing loaded"
    /// line (true on a namamp — it passes the signal dry). Always readable,
    /// never blank.</summary>
    public string CapturePlateText
    {
        get
        {
            if (_identity.HasTitle) return _identity.Title;
            if (!HasValue && BuiltinLabelOrNull() is { Length: > 0 } builtin)
                return _loc.Format("Rig_BuiltInIr", builtin);
            return _loc["Nam_NoCapture"];
        }
    }

    /// <summary>Throw-free read of <see cref="_builtinLabel"/> — the plate must
    /// never go down with a broken label source.</summary>
    private string? BuiltinLabelOrNull()
    {
        try { return _builtinLabel?.Invoke(); }
        catch { return null; }
    }

    /// <summary>Re-reads <see cref="CapturePlateText"/> — the owning node calls
    /// it when the badge param (the cab's <c>model</c>) changes, so switching
    /// the built-in IR updates the plate live.</summary>
    internal void RefreshPlate() => OnPropertyChanged(nameof(CapturePlateText));

    // ---- library picker (the "From library…" dropdown on the file strip) ----

    /// <summary>The local capture library filtered to THIS param's accepted
    /// extensions (a namamp offers .nam/.aidax captures, a cab only the .wav
    /// IRs). Empty when no library is wired or nothing fits — the dropdown
    /// hides via <see cref="HasLibraryChoices"/>.</summary>
    public IReadOnlyList<NamCaptureChoice> LibraryChoices { get; private set; }

    public bool HasLibraryChoices => LibraryChoices.Count > 0;

    /// <summary>Re-materializes <see cref="LibraryChoices"/> from the parent's
    /// (freshly invalidated) library index and re-finds the selected row. The
    /// incremental board sync calls it on every SURVIVING card — the old full
    /// teardown got this for free by rebuilding the card.</summary>
    internal void RefreshLibraryChoices()
    {
        if (!IsFilePicker || _libraryChoicesSource is null) return;
        LibraryChoices = _libraryChoicesSource(FileFilter) ?? [];
        OnPropertyChanged(nameof(LibraryChoices));
        OnPropertyChanged(nameof(HasLibraryChoices));
        _syncingLibraryChoice = true;
        try { SelectedLibraryChoice = FindLibraryChoice(Value); }
        finally { _syncingLibraryChoice = false; }
    }

    /// <summary>Guards the <c>OnSelectedLibraryChoiceChanged</c> partial while
    /// the selection is being mirrored FROM a committed value — the mirror must
    /// not re-load the file it is reporting.</summary>
    private bool _syncingLibraryChoice;

    /// <summary>The library row currently loaded on this param (null when the
    /// value is empty or came from outside the library). Selecting one loads
    /// its file through the SAME <see cref="Commit"/> path the picker uses.</summary>
    [ObservableProperty]
    private NamCaptureChoice? _selectedLibraryChoice;

    partial void OnSelectedLibraryChoiceChanged(NamCaptureChoice? value)
    {
        if (_syncingLibraryChoice || value is null) return;
        LoadFile(value.FilePath);
        // A rejected native push keeps the previous value (Commit only advances
        // on success) — follow it back so the dropdown never reports a file the
        // engine refused.
        if (!string.Equals(Value, value.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            _syncingLibraryChoice = true;
            try { SelectedLibraryChoice = FindLibraryChoice(Value); }
            finally { _syncingLibraryChoice = false; }
        }
    }

    private NamCaptureChoice? FindLibraryChoice(string path)
        => path.Length == 0
            ? null
            : LibraryChoices.FirstOrDefault(c =>
                string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase));

    // ---- file picker --------------------------------------------------------

    [RelayCommand]
    private async Task PickFileAsync()
    {
        if (_files == null || !IsFilePicker) return;
        var filters = BuildFilters(FileFilter, DisplayName);
        var picked = await _files.OpenFilePickerAsync(
            _loc.Format("Dsp_ChooseFileTitle", DisplayName), filters);
        if (!string.IsNullOrEmpty(picked)) Commit(picked);
    }

    [RelayCommand]
    private void Clear() => Commit(string.Empty); // "" reverts to the built-in default

    /// <summary>
    ///     Loads a file this param already knows the path of — the rig's CAPTURE
    ///     column clicking a row out of the local library, where there is nothing
    ///     to pick because the file is already chosen.
    ///     <para>
    ///         It goes through the SAME <c>Commit</c> the picker uses, which is
    ///         the point: the native push, the identity re-resolve and the
    ///         <c>StringCommitted</c> notification (which arms the neural amp's
    ///         bounded status poll) are one path, so a capture loaded from the
    ///         library cannot end up in a different state from one picked off
    ///         disk.
    ///     </para>
    /// </summary>
    internal void LoadFile(string path)
    {
        if (!IsFilePicker || string.IsNullOrEmpty(path)) return;
        Commit(path);
    }

    /// <summary>
    ///     Pushes a value this param is being GIVEN — the board's duplicate and
    ///     undo-remove gestures copying a unit's string params onto a freshly
    ///     added node.
    ///     <para>
    ///         It is <see cref="Commit" />, not <see cref="MirrorValue" />: the
    ///         new node has nothing loaded natively yet, so the value has to be
    ///         PUSHED (a mirror would leave the card claiming a capture the engine
    ///         never got). Unlike <see cref="LoadFile" /> it also covers the free
    ///         TEXT param — a duplicated vocal module keeps its phrases — and, like
    ///         every other path here, the managed value only advances if native
    ///         accepted it.
    ///     </para>
    /// </summary>
    internal void CopyValueFrom(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Commit(value);
    }

    private static IReadOnlyList<DotCharter.Tone.Infrastructure.Storage.FilePickerFilter> BuildFilters(
        string fileFilter, string displayName)
    {
        var patterns = fileFilter
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return patterns.Count == 0
            ? []
            : [new DotCharter.Tone.Infrastructure.Storage.FilePickerFilter(displayName, patterns)];
    }

    // ---- lyric editor (miku phrases) ---------------------------------------

    /// <summary>The draft text in the open lyric popup (committed on Save).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyllableHint))]
    private string _lyricsDraft;

    /// <summary>Localized "N / 64 syllables" hint, mirroring the native
    /// tokenizer (whitespace/comma/hyphen/pipe/semicolon split, 64 cap).</summary>
    public string SyllableHint =>
        _loc.Format("Dsp_SyllableCount", CountSyllables(LyricsDraft), MaxSyllables);

    /// <summary>Resets the draft to the stored value (call when the popup opens).</summary>
    public void BeginLyricsEdit() => LyricsDraft = Value;

    [RelayCommand]
    private void SaveLyrics() => Commit(LyricsDraft ?? string.Empty);

    internal static int CountSyllables(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        var inToken = false;
        foreach (var c in text)
        {
            var sep = Array.IndexOf(SyllableSeparators, c) >= 0;
            if (sep) { inToken = false; continue; }
            if (!inToken) { count++; if (count >= MaxSyllables) return MaxSyllables; }
            inToken = true;
        }
        return count;
    }

    // ---- commit -------------------------------------------------------------

    private void Commit(string value)
    {
        var v = value ?? string.Empty;
        // Native FIRST; only advance the managed value on success (a failed IR
        // load keeps the previous value natively — the mirror must match).
        if (_dsp.SetNodeString(_nodeId, Id, v))
        {
            LoadError = string.Empty;
            PublishValue(v);
            // Tell the owning node the push landed — a namamp modelPath loads
            // ASYNCHRONOUSLY natively, so the status badge needs a delayed re-read.
            _onCommitted?.Invoke();
            return;
        }

        // THE REFUSAL IS THE POINT. A synchronous IR load that fails returns
        // false and the engine KEEPS the previous IR (IDspChainService
        // .SetNodeString says so) — so before this, picking a 32-bit WAV, a
        // 4-channel WAV or a file the decoder cannot open did exactly nothing
        // visible: the button kept the old name, no plate moved, and the player
        // had every reason to believe the new IR was loaded and simply sounded
        // identical. convreverb's `irFile` (JuceSharp 0.9) makes that reachable
        // from a second module, but the hole was always there on cab/acousticsim.
        // Nothing else on this path can say so: the setter is bool, not a reason.
        LoadError = _loc.Format("Rig_FileRejected",
            v.Length > 0 ? System.IO.Path.GetFileName(v) : DisplayName);
    }

    /// <summary>
    ///     Why the last push was REFUSED, or empty when the value on screen is
    ///     the value the engine is running. Cleared by the next accepted push.
    ///     <para>
    ///         A face that loads a file MUST render this. "Nothing happened" is
    ///         indistinguishable from "it loaded and sounds the same", and a
    ///         silent refusal on an IR is exactly the shape of bug the repo's
    ///         "a capability the app cannot honour must never look live" rule
    ///         exists for.
    ///     </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string _loadError = string.Empty;

    /// <summary>True while the engine refused the last file and kept the old
    /// one — drives the error line beside the picker.</summary>
    public bool HasLoadError => LoadError.Length > 0;

    /// <summary>Mirrors a value the SERVICE already holds — the incremental
    /// board sync updating a surviving card after a preset apply pushed the
    /// string through the chain directly. No native call and no
    /// <c>StringCommitted</c> (nothing was pushed from HERE); the neural amp's
    /// status poll is armed by whichever path did the pushing.</summary>
    internal void MirrorValue(string value)
    {
        var v = value ?? string.Empty;
        if (!string.Equals(Value, v, StringComparison.Ordinal)) PublishValue(v);
    }

    /// <summary>Advances the managed value + everything derived from it (the
    /// shared tail of <see cref="Commit"/> and <see cref="MirrorValue"/>).</summary>
    private void PublishValue(string v)
    {
        Value = v;
        LyricsDraft = v;
        // The faceplate identity is derived from the path (+ its library
        // manifest), so it has to be re-resolved on the same beat the value
        // changes — the plate is what tells the user WHICH capture landed.
        _identity = ResolveIdentity(v);
        OnPropertyChanged(nameof(CaptureTitle));
        OnPropertyChanged(nameof(CaptureByline));
        OnPropertyChanged(nameof(HasCaptureByline));
        OnPropertyChanged(nameof(CaptureFormatLabel));
        OnPropertyChanged(nameof(HasCaptureFormat));
        OnPropertyChanged(nameof(CapturePlateText));
        // Mirror the dropdown onto whatever landed (null when the file came
        // from outside the library, or on a clear) WITHOUT re-loading it.
        _syncingLibraryChoice = true;
        try { SelectedLibraryChoice = FindLibraryChoice(v); }
        finally { _syncingLibraryChoice = false; }
    }
}

/// <summary>
///     One state of a <see cref="RigParamRole.Transport"/> param, as a button
///     the face can bind directly. It carries its OWN command and index for the
///     same reason <c>RigReplaceOption</c> does: a row inside an
///     <c>ItemsControl</c> cannot bind an ancestor's DataContext, and a command
///     takes one parameter.
/// </summary>
public sealed partial class PedalTransportStateViewModel : ObservableObject
{
    private readonly PedalTransportParamViewModel _owner;

    internal PedalTransportStateViewModel(PedalTransportParamViewModel owner, int index, string label)
    {
        _owner = owner;
        Index = index;
        Label = label;
    }

    /// <summary>The state's index; the native value is <c>Min + Index</c>.</summary>
    public int Index { get; }

    /// <summary>The state's name, straight off the ABI labels ("Record",
    /// "Overdub"). A proper noun of the engine's, deliberately not localized —
    /// the same rule every other value label follows.</summary>
    public string Label { get; }

    /// <summary>True while the ENGINE reports this state. Refreshed by the
    /// owner's poll, never latched from the click.</summary>
    public bool IsActive => _owner.SelectedIndex == Index;

    internal void NotifyActiveChanged() => OnPropertyChanged(nameof(IsActive));

    [RelayCommand]
    private void Select() => _owner.Select(Index);
}

/// <summary>
///     A <see cref="RigParamRole.Transport"/> param: a footswitch with named
///     states that the ENGINE can move on its own.
///     <para>
///         <b>The state is POLLED, never latched.</b> A looper closes its own
///         take when <c>maxLoopS</c> is reached, so a control that showed the
///         last value it WROTE would sit on "Record" over a pedal that had
///         already gone to "Play". <see cref="Refresh"/> re-reads
///         <see cref="IDspChainService.GetParam"/> — the same bounded,
///         timer-free shape the neural amp's status badge uses, driven off the
///         board's own sync rather than a clock of its own.
///     </para>
/// </summary>
public sealed partial class PedalTransportParamViewModel : ObservableObject
{
    private readonly IDspChainService _dsp;
    private readonly int _nodeId;
    private int _selectedIndex;

    internal PedalTransportParamViewModel(IDspChainService dsp, int nodeId, DspParamInfo info)
    {
        _dsp = dsp;
        _nodeId = nodeId;
        Id = info.Id;
        DisplayName = info.DisplayName is { Length: > 0 } name ? name : info.Id;
        Minimum = info.Min;
        var labels = info.ValueLabels ?? [];
        States = labels.Select((label, i) => new PedalTransportStateViewModel(this, i, label)).ToList();
        Refresh();
    }

    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>The param's minimum — the native value of state 0.</summary>
    public float Minimum { get; }

    /// <summary>One button per named state, in ABI order.</summary>
    public IReadOnlyList<PedalTransportStateViewModel> States { get; }

    /// <summary>The state the ENGINE last reported (clamped into the label
    /// range). Read-only to the view: a state change goes through
    /// <see cref="Select"/>, which writes and then re-reads.</summary>
    public int SelectedIndex => _selectedIndex;

    /// <summary>The current state's name, for the accessible name and any
    /// compact readout. Empty when the param declares no labels at all.</summary>
    public string CurrentLabel =>
        _selectedIndex >= 0 && _selectedIndex < States.Count ? States[_selectedIndex].Label : string.Empty;

    /// <summary>Writes a state and immediately re-reads what the engine actually
    /// runs. The write is NOT assumed: a native rejection, a clamp, or an engine
    /// that has already moved on all land in the same place, and the buttons show
    /// that rather than the click.</summary>
    internal void Select(int index)
    {
        _dsp.SetParam(_nodeId, Id, Minimum + index);
        Refresh();
        // Re-announce UNCONDITIONALLY, even when nothing moved. The buttons bind
        // IsChecked one-way, so a click toggles the target locally; if the write
        // was refused, or the user pressed the state that was already active,
        // there is no source CHANGE to push the binding back and the segment
        // would sit visually wrong over a value that never moved.
        foreach (var state in States) state.NotifyActiveChanged();
    }

    /// <summary>Re-reads the state from the chain. Cheap (one cached read on the
    /// fake, one P/Invoke live) and idempotent, so the board's sync may call it
    /// freely.</summary>
    public void Refresh()
    {
        var value = _dsp.GetParam(_nodeId, Id);
        var next = value.HasValue && States.Count > 0
            ? Math.Clamp((int)Math.Round(value.Value - Minimum), 0, States.Count - 1)
            : 0;
        if (next == _selectedIndex) return;
        _selectedIndex = next;
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(CurrentLabel));
        foreach (var state in States) state.NotifyActiveChanged();
    }
}

/// <summary>
///     A <see cref="RigParamRole.Momentary"/> param: a one-shot TRIGGER.
///     <para>
///         It is WRITE-ONLY by construction and exposes no current value at all,
///         because there is nothing honest to show — the engine performs the
///         action and answers its resting value, so a control that mirrored the
///         value would flash to "Undo" and snap straight back to "-", and a
///         two-way binding would see that snap-back as a user edit and re-fire.
///         Pressing it writes the trigger value once; the face shows a button and
///         never a state.
///     </para>
/// </summary>
public sealed partial class PedalMomentaryParamViewModel : ObservableObject
{
    private readonly IDspChainService _dsp;
    private readonly int _nodeId;
    private readonly float _triggerValue;

    internal PedalMomentaryParamViewModel(IDspChainService dsp, int nodeId, DspParamInfo info)
    {
        _dsp = dsp;
        _nodeId = nodeId;
        Id = info.Id;
        DisplayName = info.DisplayName is { Length: > 0 } name ? name : info.Id;
        var labels = info.ValueLabels ?? [];
        // The ACTION is the last named value ("-", "Undo" ⇒ 1) and the resting
        // value is the first. Derived from the labels rather than hard-coded to
        // 1 so a future three-state trigger still fires its real action.
        ActionLabel = labels.Count > 0 ? labels[^1] : DisplayName;
        _triggerValue = labels.Count > 0 ? info.Min + (labels.Count - 1) : info.Max;
    }

    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>The button's caption: the ABI's name for the ACTION ("Undo"),
    /// not for the resting state. An engine proper noun, not localized.</summary>
    public string ActionLabel { get; }

    /// <summary>Fires the trigger once.</summary>
    [RelayCommand]
    private void Trigger() => _dsp.SetParam(_nodeId, Id, _triggerValue);
}
