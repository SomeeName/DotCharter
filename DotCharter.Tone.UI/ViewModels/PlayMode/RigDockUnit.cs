// RigDockUnit root: RigEditorFace and its pure fold, the dock's wrapper over one PedalNodeViewModel (constructor, unit actions, editor family, captures) and its row types.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     Which EDITOR the module dock opens for the selected unit.
///
///     <para>
///         The mockup's <c>moduleEditor()</c> switches on the unit's <b>id</b> —
///         a per-module table, and exactly the second catalogue §7 exists to
///         forbid. This folds on <see cref="PedalSkin" /> instead, which the rack
///         already derives from the module metadata
///         (<see cref="DspModuleVisuals.SkinFor" />): a face is chosen by what a
///         unit IS, and a module added to the native catalogue tomorrow lands on
///         the right editor with nothing here to update.
///     </para>
/// </summary>
public enum RigEditorFace
{
    /// <summary>An amp head — built-in voicings or a neural capture. Gets the
    /// faceplate: name plate, model rail, the whole knob row, and (for a capture
    /// player) the capture column.</summary>
    Amp,

    /// <summary>A speaker cabinet — its model rail plus the impulse-response
    /// file it is loaded with.</summary>
    Cabinet,

    /// <summary>Everything else: the unit's own params, its model pickers and any
    /// file it carries.</summary>
    Generic
}

/// <summary>The pure fold. Its own type so the dock's shape is testable without
/// a chain, a service or a platform.</summary>
public static class RigEditorFaces
{
    public static RigEditorFace For(PedalSkin skin) => skin switch
    {
        PedalSkin.AmpHead or PedalSkin.NeuralAmp => RigEditorFace.Amp,
        PedalSkin.Cabinet => RigEditorFace.Cabinet,
        _ => RigEditorFace.Generic
    };
}

/// <summary>
///     The unit the module dock is editing, wrapped for the dock's own layout.
///
///     <para>
///         <b>It owns no parameter.</b> Every knob, model list and file row it
///         exposes is a projection of the same
///         <see cref="PedalNodeViewModel" /> the board card renders — the dock is
///         a second VIEW of one unit, never a second model of it. What it adds is
///         which FACE to draw and, for a capture player, the local capture
///         library as rows you can click.
///     </para>
/// </summary>
public sealed partial class RigDockUnit : ObservableObject
{
    private readonly INamCaptureLibrary? _library;
    private readonly ILocalizationService _loc;

    public RigDockUnit(PedalNodeViewModel node, INamCaptureLibrary? library = null, RigMacroBankViewModel? controls = null,
        RigMidiInputViewModel? midiControls = null, RigNodeMetersViewModel? meters = null,
        PedalboardViewModel? rig = null, RigRoutingConsoleViewModel? routing = null)
    {
        Node = node;
        _loc = node.Localization;
        // OPENING the editor is a poll beat. This dock is rebuilt per selection,
        // and the two things it shows that the ENGINE owns — the node's latency
        // and a transport the engine moved itself — are stale by definition on a
        // card that was built when the pedal was added. Without this, stepping
        // to a unit shows whatever was true at AddNode time until the next board
        // sync happens to run.
        node.RefreshPolledState();
        BuildUnitActions(rig, node);
        _micRig = rig;
        Routing = routing;
        controls?.Edit(node);
        Assignments = controls?.Editor;
        Controls = controls;
        MidiControls = midiControls;
        Meters = meters;
        _library = library;
        Face = RigEditorFaces.For(node.Skin);
        Family = EditorFamilyFor(node.ModuleTypeId);
        // Presentation groups retain the live catalogue parameter instances.
        PrimaryKnobs = node.KnobParams.Where(p =>
            (!IsAmpFace || p.Id is not ("resonance" or "inputDb" or "outputDb")) &&
            (!IsCabFace || p.Id is not ("blend" or "gainADb" or "gainBDb" or "delayAMs" or "delayBMs"))).ToArray();
        SecondaryKnobs = node.KnobParams.Except(PrimaryKnobs).ToArray();
        if (IsCabFace)
        {
            CabinetSlots = new[] { "A", "B" }.Select(letter => new RigCabinetSlot(
                "IR " + letter,
                node.StringParams.FirstOrDefault(p => p.Id == (letter == "A" ? "irPath" : "irPathB")),
                SecondaryKnobs.Where(p => p.Id == "gain" + letter + "Db" || p.Id == "delay" + letter + "Ms").ToArray(),
                node.ModelSelectors.FirstOrDefault(p => p.ParamId == "invert" + letter), _loc)).Where(slot => slot.File != null).ToArray();
        }

        // The amp face's model rail: the voicing picker when the module has one,
        // else its first labelled param. Both come off the node — the dock never
        // builds a picker of its own.
        ModelRail = node.AmpModelSelector ?? node.ModelSelectors.FirstOrDefault();

        // The file this unit loads: a .nam/.aidax capture on a neural amp, an
        // impulse response on a cabinet. Cabinets expose both slots separately.
        FileParam = node.StringParams.FirstOrDefault(p => p.IsFilePicker);

        // A string param with no file filter is FREE TEXT (the vocal module's
        // phrase list), and it is the one param class the board card cannot
        // carry: the card's strip is a path plus a Choose button, and Choose
        // does nothing at all on a param that has no file to choose. It is an
        // editor field, so it lives in the editor. Seeded here because the draft
        // is what the field binds and it has to open on the stored value.
        LyricsParam = node.StringParams.FirstOrDefault(p => p.IsLyrics);
        LyricsParam?.BeginLyricsEdit();

        Captures = [];
        if (FileParam is { } file)
            LoadCaptures(file);

        // The enclosure's LED and footswitch label follow the unit's bypass.
        // Weakly held: the dock unit is rebuilt per selection and must not pin
        // the node (the same shape RigCabinetSlot uses).
        var weak = new WeakReference<RigDockUnit>(this);
        System.ComponentModel.PropertyChangedEventHandler? onNodeChanged = null;
        onNodeChanged = (_, e) =>
        {
            if (!weak.TryGetTarget(out var unit)) { node.PropertyChanged -= onNodeChanged; return; }
            if (e.PropertyName == nameof(PedalNodeViewModel.Bypassed)) unit.NotifyBypassChanged();
            // The panel APPEARS when the latency does: a node whose reading
            // arrives after construction (the engine attached, or a param moved
            // the module's geometry) must not leave the card with a row that is
            // rendered and a container that is still collapsed.
            else if (e.PropertyName == nameof(PedalNodeViewModel.HasLatency))
                unit.OnPropertyChanged(nameof(HasUnitPanel));
        };
        node.PropertyChanged += onNodeChanged;

        // …and the same for a file the engine refuses, which is by definition
        // something that happens long after this unit was built.
        if (FileParam is { } fileParam)
        {
            System.ComponentModel.PropertyChangedEventHandler? onFileChanged = null;
            onFileChanged = (_, e) =>
            {
                if (!weak.TryGetTarget(out var unit)) { fileParam.PropertyChanged -= onFileChanged; return; }
                if (e.PropertyName == nameof(PedalStringParamViewModel.HasLoadError))
                    unit.OnPropertyChanged(nameof(HasUnitPanel));
            };
            fileParam.PropertyChanged += onFileChanged;
        }
    }

    /// <summary>The one live unit. Knobs bind straight through it.</summary>
    public PedalNodeViewModel Node { get; }

    // ── the unit's own edit actions (duplicate / replace / remove) ────────────

    /// <summary>
    ///     The rig's <c>DuplicateCommand</c>, carried on the unit so the editor's
    ///     header binds one property instead of casting back through the host's
    ///     DataContext — the same reason <c>RigGearItem</c> carries its add
    ///     command. Its parameter is <see cref="Node" />. Null when the dock was
    ///     built without a rig (the pure face tests), and a routing junction
    ///     reports it disabled rather than absent.
    /// </summary>
    public System.Windows.Input.ICommand? DuplicateCommand { get; private set; }

    /// <inheritdoc cref="DuplicateCommand" />
    public System.Windows.Input.ICommand? RemoveCommand { get; private set; }

    /// <summary>
    ///     "Replace with…": one option per catalogue module of the SAME identity
    ///     as this unit (a drive swaps for another drive), each carrying the rig's
    ///     <c>ReplaceCommand</c> and its own <see cref="RigModuleReplacement" />
    ///     parameter — a command takes one parameter, and a picker cannot build a
    ///     pair in markup. Empty for a junction (its pair is the region's frame),
    ///     for a dock with no rig, and when the identity has no other module.
    /// </summary>
    public IReadOnlyList<RigReplaceOption> ReplaceOptions { get; private set; } = [];

    public bool HasReplaceOptions => ReplaceOptions.Count > 0;

    /// <summary>True while this unit may be duplicated or replaced at all — false
    /// on a splitter/merger, so the header's actions ship visibly disabled rather
    /// than silently inert.</summary>
    public bool CanEditComposition => !Node.IsJunction;

    private void BuildUnitActions(PedalboardViewModel? rig, PedalNodeViewModel node)
    {
        if (rig is null) return;

        DuplicateCommand = rig.DuplicateCommand;
        RemoveCommand = rig.RemoveCommand;
        if (node.IsJunction) return;

        var identity = node.FireCategory;
        ReplaceOptions = rig.AvailableModules
            .Where(m => !string.Equals(m.Id, node.ModuleTypeId, StringComparison.Ordinal) &&
                        FireRigCategory.ForModule(m.Id) == identity)
            .Select(m => new RigReplaceOption(
                m.DisplayName is { Length: > 0 } name ? name : m.Id,
                new RigModuleReplacement(node, m),
                rig.ReplaceCommand))
            .ToList();
    }
    public RigControlAssignmentEditorViewModel? Assignments { get; }
    public RigMacroBankViewModel? Controls { get; }
    public RigMidiInputViewModel? MidiControls { get; }
    public RigNodeMetersViewModel? Meters { get; }

    /// <summary>The split console for a splitter / merger: the routing mode
    /// and the live lanes' mixers. Null for every other unit, and for a
    /// junction opened by a host that has no board (a test over the unit alone).</summary>
    public RigRoutingConsoleViewModel? Routing { get; }

    public bool HasRouting => Routing is not null;

    public RigEditorFace Face { get; }

    /// <summary>Which editor VIEW opens for this unit (<see cref="EditorFamilyFor(string)" />).
    /// The view for it is built by <c>RigEditorTemplateSelector</c>.</summary>
    public RigEditorFamily Family { get; }

    /// <summary>
    ///     The editor a module opens. A unit with a generated faceplate is edited ON
    ///     THE BOARD, where its face stands with live knobs, so the dock opens the
    ///     unit inspector (the <see cref="RigEditorFamily.Pedal" /> view: every
    ///     parameter's exact value, its file and captures) whatever its art family —
    ///     a second face in a different style below the board read as a different
    ///     unit. The cabinet and routing consoles stay: the cabinet's parameters
    ///     are not on its face, and the splitter is a board junction, not a face.
    /// </summary>
    public static RigEditorFamily EditorFamilyFor(string moduleTypeId) =>
        EditorFamilyFor(moduleTypeId, RigFaceplates.For(moduleTypeId) is not null);

    /// <summary><see cref="EditorFamilyFor(string)" /> with whether the module has a
    /// faceplate given, so the rule is testable without the asset loader.</summary>
    public static RigEditorFamily EditorFamilyFor(string moduleTypeId, bool hasFaceplate)
    {
        var family = RigArtwork.FamilyFor(moduleTypeId);
        if (family is RigEditorFamily.Cabinet or RigEditorFamily.Routing or RigEditorFamily.MicPlacement) return family;
        return hasFaceplate ? RigEditorFamily.Pedal : family;
    }
    public IReadOnlyList<PedalParamViewModel> PrimaryKnobs { get; }
    public IReadOnlyList<PedalParamViewModel> SecondaryKnobs { get; }
    public bool HasSecondaryKnobs => SecondaryKnobs.Count > 0;
    public bool ShowSecondaryKnobs => HasSecondaryKnobs && !IsCabFace;
    public bool ShowFileHeader => HasFileParam && !IsCabFace;
    public IReadOnlyList<RigCabinetSlot> CabinetSlots { get; } = [];
    public PedalStringParamViewModel? SecondaryIr => CabinetSlots.FirstOrDefault(slot => slot.File?.Id == "irPathB")?.File;
    public PedalParamViewModel? Blend => Node.Params.FirstOrDefault(p => p.Id == "blend");
    public bool HasBlend => IsCabFace && Blend != null;
    public int PrimaryColumnCount => Math.Clamp(PrimaryKnobs.Count, 1, IsAmpFace ? 6 : 4);

    public bool IsAmpFace => Face == RigEditorFace.Amp;
    public bool IsCabFace => Face == RigEditorFace.Cabinet;
    public bool IsGenericFace => Face == RigEditorFace.Generic;

    public string DisplayName => Node.DisplayName;

    /// <summary>The identity eyebrow beside the name in the dock bar
    /// ("AMPS", "CABINETS", …) — the same fold the card's head prints.</summary>
    public string CategoryLabel => Node.FireCategoryLabel;

    public FireUnitCategory FireCategory => Node.FireCategory;
    public string FireTokenKey => Node.FireTokenKey;

    /// <summary>The faceplate's engraved name. Upper-cased HERE rather than in
    /// the markup because the source is a bound value, not a resx key, so
    /// <c>{localization:Loc …, Upper=True}</c> cannot reach it.</summary>
    public string PlateTitle => Node.IsNeuralAmp ? "NAM" : Node.DisplayName.ToUpperInvariant();

    /// <summary>The model picker the face's rail draws, or null when the module
    /// has no labelled param at all (most pedals).</summary>
    public PedalModelSelectorViewModel? ModelRail { get; }

    public bool HasModelRail => ModelRail != null;

    /// <summary>The remaining labelled params — drawn as compact pickers under
    /// the knob row so a module with two model lists loses neither.</summary>
    public IReadOnlyList<PedalModelSelectorViewModel> ExtraSelectors =>
        Node.ModelSelectors.Where(s => !ReferenceEquals(s, ModelRail) &&
            (!IsCabFace || s.ParamId is not ("invertA" or "invertB"))).ToList();

    public bool HasExtraSelectors => ExtraSelectors.Count > 0;

    // ── the unit panel: what a knob and a picker cannot say ──────────────────

    /// <summary>The unit's TRANSPORT params (the looper's Stop/Record/Play/
    /// Overdub) — the node's own, projected so the shared rack binds one
    /// property. Empty for every other module.</summary>
    public IReadOnlyList<PedalTransportParamViewModel> TransportParams => Node.TransportParams;

    /// <summary>The unit's one-shot TRIGGER params (the looper's undo).</summary>
    public IReadOnlyList<PedalMomentaryParamViewModel> MomentaryParams => Node.MomentaryParams;

    /// <summary>
    ///     Whether the shared rack's UNIT panel has anything to show. A single
    ///     flag rather than four <c>IsVisible</c>s on one Border because a panel
    ///     whose every row is collapsed still draws its own chrome — an empty
    ///     titled card under the MIDI rack, on ~95 of the 100 modules.
    /// </summary>
    public bool HasUnitPanel =>
        Node.HasTransportParams || Node.HasMomentaryParams || Node.SupportsSidechain ||
        Node.HasLatency || FileParam is { HasLoadError: true };

    /// <summary>The capture / impulse-response file row, or null.</summary>
    public PedalStringParamViewModel? FileParam { get; }

    public bool HasFileParam => FileParam != null;

    /// <summary>The free-text param (the vocal module's phrases), or null — which
    /// is every other module.</summary>
    public PedalStringParamViewModel? LyricsParam { get; }

    public bool HasLyricsParam => LyricsParam != null;

    /// <summary>
    ///     "This unit has no controls" — which must count the lyric field, or a
    ///     module whose ONLY param is free text prints the note UNDER the field
    ///     it is denying the existence of.
    /// </summary>
    public bool ShowsNoControlsNote => !Node.HasParamControls && !HasLyricsParam;

    /// <summary>
    ///     What the right-hand column is called: CAPTURE on an amp that plays a
    ///     neural capture, IMPULSE RESPONSE on a cabinet that loads an IR.
    ///     <para>
    ///         The same column, two jobs, because it is the same THING — the file
    ///         this unit is loaded with. Naming it by the unit's face rather than
    ///         hard-coding either word is what stops the cab's IR row reading as
    ///         "capture".
    ///     </para>
    /// </summary>
    public string FileColumnTitleKey => IsCabFace ? "Rig_Ir" : "Rig_Capture";

    public string FileColumnTitle => _loc[FileColumnTitleKey];

    /// <summary>
    ///     The local capture library as clickable rows — the mockup's CAPTURE
    ///     column. Real files off <see cref="INamCaptureLibrary.GetAll" />,
    ///     filtered to the extensions THIS unit's file param accepts (a cab
    ///     offers only the library's <c>.wav</c> IRs, a neural amp only
    ///     <c>.nam</c>/<c>.aidax</c> captures); empty (and the amp column says
    ///     so) when nothing fitting has been downloaded yet.
    /// </summary>
    public ObservableCollection<RigCaptureRow> Captures { get; }

    public bool HasCaptures => Captures.Count > 0;

    /// <summary>True only for a neural amp: the capture LIST and the capture
    /// status are about a capture, and a cabinet's IR column has neither.</summary>
    public bool HasCaptureColumn => IsAmpFace && Node.IsNeuralAmp;

    /// <summary>
    ///     "Nothing in your library yet" belongs to the CAPTURE column only. A
    ///     cabinet has no capture library, so on a cab face the empty note would
    ///     be a sentence about a thing that column is not.
    /// </summary>
    public bool ShowsEmptyCaptureNote => HasCaptureColumn && !HasCaptures;

    private void LoadCaptures(PedalStringParamViewModel file)
    {
        if (_library is null) return;

        IReadOnlyList<NamCaptureEntry> entries;
        try { entries = _library.GetAll(); }
        catch { return; } // an unreadable library must not take the dock down

        var accepted = ExtensionsFrom(file.FileFilter);
        foreach (var entry in entries)
        {
            if (!accepted.Contains(Path.GetExtension(entry.FilePath), StringComparer.OrdinalIgnoreCase))
                continue;
            Captures.Add(new RigCaptureRow(entry, file));
        }

        OnPropertyChanged(nameof(HasCaptures));
        OnPropertyChanged(nameof(ShowsEmptyCaptureNote));
    }

    /// <summary>
    ///     The extensions a ";"-separated wildcard filter accepts
    ///     ("*.nam;*.aidax" → [".nam", ".aidax"]), lowercase, dot included.
    ///     Pure. The dock's capture column and the card strip's library picker
    ///     both filter the library through it, so the two surfaces can never
    ///     offer a param a file it would reject — the library holds NAM
    ///     captures AND cab IRs, and a <c>.nam</c> pushed into a cab's
    ///     <c>irPath</c> is a silent native reject.
    /// </summary>
    internal static IReadOnlyList<string> ExtensionsFrom(string? fileFilter)
    {
        if (string.IsNullOrWhiteSpace(fileFilter)) return [];

        var result = new List<string>();
        foreach (var pattern in fileFilter.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = Path.GetExtension(pattern).ToLowerInvariant(); // "*.nam" → ".nam"
            if (ext.Length > 1 && !result.Contains(ext)) result.Add(ext);
        }

        return result;
    }
}

/// <summary>
///     One entry in the module editor's "replace with…" picker: what the swap
///     would be called, the pair it needs (this unit + that module type) and the
///     rig's own <c>ReplaceCommand</c>. Carried per row for the same reason
///     <c>GearModelOption</c> carries its add command — a popup row cannot bind
///     an ancestor's DataContext, and a command takes one parameter.
/// </summary>
public sealed class RigReplaceOption
{
    public RigReplaceOption(string label, RigModuleReplacement request,
        System.Windows.Input.ICommand command)
    {
        Label = label;
        Request = request;
        Command = command;
    }

    public string Label { get; }

    /// <summary>The command's parameter — bound as <c>CommandParameter</c>.</summary>
    public RigModuleReplacement Request { get; }

    public System.Windows.Input.ICommand Command { get; }
}

/// <summary>
///     One capture in the amp face's CAPTURE column. Clicking it pushes the
///     file into the SAME string param the Browse… button writes, so a capture
///     loaded from here and one picked off disk take one code path.
/// </summary>
public sealed partial class RigCaptureRow : ObservableObject
{
    private readonly PedalStringParamViewModel _target;

    public RigCaptureRow(NamCaptureEntry entry, PedalStringParamViewModel target)
    {
        _target = target;
        FilePath = entry.FilePath;
        Title = entry.Manifest.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(entry.FilePath);
        // Proper nouns off the manifest ("NAM", "AIDA-X"), deliberately not
        // localized — the same call the faceplate's format chip already makes.
        FormatLabel = entry.Manifest.Format.ToUpperInvariant();
    }

    public string FilePath { get; }
    public string Title { get; }
    public string FormatLabel { get; }

    /// <summary>True when this capture is the one the unit is currently
    /// playing — the column's selection mark.</summary>
    public bool IsActive =>
        string.Equals(_target.Value, FilePath, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Load()
    {
        _target.LoadFile(FilePath);
        OnPropertyChanged(nameof(IsActive));
    }
}

public sealed class RigCabinetSlot : ObservableObject
{
    private readonly ILocalizationService _loc;

    public RigCabinetSlot(string title, PedalStringParamViewModel? file,
        IReadOnlyList<PedalParamViewModel> knobs, PedalModelSelectorViewModel? polarity, ILocalizationService localization)
    {
        Title = title; File = file; Knobs = knobs; Polarity = polarity;
        _loc = localization;
        if (file is null) return;
        var weak = new WeakReference<RigCabinetSlot>(this);
        System.ComponentModel.PropertyChangedEventHandler? changed = null;
        changed = (sender, _) =>
        {
            if (weak.TryGetTarget(out var slot))
            {
                slot.OnPropertyChanged(nameof(CanControl));
                slot.OnPropertyChanged(nameof(StatusText));
            }
            else
            {
                file.PropertyChanged -= changed;
            }
        };
        file.PropertyChanged += changed;
    }
    public string Title { get; }
    public PedalStringParamViewModel? File { get; }
    public IReadOnlyList<PedalParamViewModel> Knobs { get; }
    public PedalModelSelectorViewModel? Polarity { get; }
    public bool CanControl => File?.Id != "irPathB" || File.HasValue;
    public string? StatusText => File is { Id: "irPathB", HasValue: false }
        ? _loc["Rig_IrBEmpty"] : File?.CapturePlateText;
}
