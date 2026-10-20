// TransportViewModel: what the Mix / Tempo / Tools popovers add - the input peak meter, the pitch note, the plugin rescan and drag-to-reorder.
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     What the transport bar's Mix / Tempo / Tools popovers show that nothing
///     else on this view model already carried — `Play Menus.dc.html`'s INPUT
///     LEVEL meter, its PITCH note, and the Tools panel's plugin rescan and
///     drag-to-reorder.
/// </summary>
/// <remarks>
///     <para>
///         Every member here is wired to real engine state or a real verb. The
///         first pass at these panels declared four of them "not built" and drew
///         the rest; a drawn control that does nothing is the
///         registered-but-dead shape the root guide exists to prevent, so the
///         rule for this file is that a property either reads the engine or does
///         not exist.
///     </para>
/// </remarks>
public partial class TransportViewModel
{
    /// <summary>The window the peak is taken over. Same size as
    /// <c>LivePlayViewModel</c>'s probe, and for the same reason: it is the
    /// windowed (multi-consumer) tap, so reading it cannot starve live note
    /// detection.</summary>
    private readonly float[] _inputLevelProbe = new float[2048];

    /// <summary>
    ///     Live capture peak in dBFS. <see cref="double.NegativeInfinity" />
    ///     until something is heard — the meter's own floor decides how that
    ///     draws, exactly as on the amp rig's header.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputPeakLabel))]
    [NotifyPropertyChangedFor(nameof(IsInputClipping))]
    private double _inputLevelDb = double.NegativeInfinity;

    /// <summary>
    ///     The mockup's `−14.2 dB PEAK`, from the real number. Silence prints an
    ///     em dash rather than `-∞ dB`, and the minus sign is U+2212 because the
    ///     readout is mono and a hyphen sits a pixel high in IBM Plex.
    /// </summary>
    public string InputPeakLabel =>
        double.IsNegativeInfinity(InputLevelDb) || InputLevelDb < -90
            ? Loc("Toolbar_Mix_NoInput")
            : string.Format(CultureInfo.InvariantCulture, "{0}{1:0.0} dB PEAK",
                InputLevelDb < 0 ? "−" : "+", Math.Abs(InputLevelDb));

    /// <summary>Digital full scale is 0 dBFS, so at or above it the converter
    /// could not represent the sample. A real threshold on a real peak.</summary>
    public bool IsInputClipping => InputLevelDb >= 0.0;

    /// <summary>
    ///     One tick of the Mix popover's meter. Driven by the VIEW's clock,
    ///     which runs only while that popover is open — never by a timer inside
    ///     a view model that outlives the surface (the amp rig's rule, and the
    ///     reason the app does not accumulate meter clocks).
    /// </summary>
    public void PollInputMeter()
    {
        var read = _audioService.ReadInputCaptureWindow(_inputLevelProbe);
        var peak = 0f;
        for (var i = 0; i < read; i++)
        {
            var a = Math.Abs(_inputLevelProbe[i]);
            if (a > peak) peak = a;
        }

        InputLevelDb = peak <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(peak);
    }

    /// <summary>
    ///     Tools → "Rescan plugin folders". Re-runs the same scan the constructor
    ///     fires once, so a plugin installed after launch appears without a
    ///     restart.
    /// </summary>
    /// <remarks>
    ///     It deliberately does NOT re-run <c>RestoreLastVstSelections</c>: that
    ///     re-adds the persisted chain, so a rescan would duplicate every slot
    ///     the user already has. The lists are rebuilt around the "None" sentinel
    ///     and the current selections are re-pointed at the refreshed instances
    ///     by PATH, because <c>VstPluginInfo</c> is compared by reference in the
    ///     ComboBox and a fresh scan mints new ones.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RescanVstPluginsAsync()
    {
        IsRescanningVstPlugins = true;
        try
        {
            var plugins = await Task.Run(() =>
            {
                try
                {
                    return _audioService.ScanVstPlugins();
                }
                catch (Exception ex)
                {
                    _logger?.Warning(AppLogCategory.Audio,
                        "Rescanning the VST3 folders failed; the plugin list is unchanged.", ex);
                    return null;
                }
            }).ConfigureAwait(true);

            if (plugins == null) return;

            var selectedInstrumentPath = SelectedInstrumentVst?.PluginPath;

            AvailableVstPlugins.Clear();
            InstrumentVstPlugins.Clear();
            var none = new VstPluginInfo { Name = "None", PluginPath = "" };
            AvailableVstPlugins.Add(none);
            InstrumentVstPlugins.Add(none);

            foreach (var p in plugins)
            {
                if (string.IsNullOrEmpty(p.PluginPath)) continue;
                if (!string.Equals(Path.GetExtension(p.PluginPath), ".vst3", StringComparison.OrdinalIgnoreCase))
                    continue;

                AvailableVstPlugins.Add(p);
                InstrumentVstPlugins.Add(p);
            }

            SelectedInstrumentVst =
                InstrumentVstPlugins.FirstOrDefault(p =>
                    string.Equals(p.PluginPath, selectedInstrumentPath, StringComparison.OrdinalIgnoreCase))
                ?? InstrumentVstPlugins[0];

            VstPluginScans++;
        }
        finally
        {
            IsRescanningVstPlugins = false;
        }
    }

    /// <summary>True while <see cref="RescanVstPluginsCommand" /> is walking the
    /// VST3 folders — the footer action disables itself rather than queueing a
    /// second walk of the same directories.</summary>
    [ObservableProperty] private bool _isRescanningVstPlugins;

    /// <summary>How many times the plugin folders have been scanned this
    /// session, the constructor's one included. Test seam: it is the only
    /// observable proof that "Rescan plugin folders" did something.</summary>
    internal int VstPluginScans { get; private set; } = 1;

    /// <summary>
    ///     Move one VST3 slot to an arbitrary position — what a DRAG produces,
    ///     where the two arrow buttons could only produce ±1.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         It is byte-for-byte what <c>MoveGuitarSlotUp</c> does, generalized:
    ///         reorder the collection, persist, push the whole chain at the
    ///         engine. <c>SetGuitarVstChain</c> takes the WHOLE list and diffs it
    ///         host-side, so an arbitrary permutation costs exactly what a
    ///         neighbour swap costs and no plugin is re-instantiated.
    ///     </para>
    ///     <para>
    ///         Returns false — and touches nothing — for an out-of-range or
    ///         no-op move, so a drag that ends where it started does not write
    ///         the config file or re-push the chain.
    ///     </para>
    /// </remarks>
    public bool MoveGuitarSlot(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= GuitarVstChain.Count) return false;
        if (toIndex < 0 || toIndex >= GuitarVstChain.Count) return false;
        if (fromIndex == toIndex) return false;

        GuitarVstChain.Move(fromIndex, toIndex);
        RenumberGuitarChain();
        PersistGuitarChain();
        PushGuitarChainToEngine();
        return true;
    }
}
