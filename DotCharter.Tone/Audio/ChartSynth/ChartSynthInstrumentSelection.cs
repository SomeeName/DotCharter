namespace DotCharter.Tone.Audio.ChartSynth;

/// <summary>
/// The built-in voices' instrument selection as the app keeps it: the
///     defaults (the owner's HSS Strat on its bridge humbucker; a Jazz bass on both
///     pickups), the config round trip (<see cref="RigConfig" />, enum NAMES so a
///     renumbering can never silently swap a guitar), and the normalisation that
///     keeps a pickup on its model's selector. Pure, so the default gate pins it.
/// </summary>
public static class ChartSynthInstrumentSelection
{
    /// <summary>The guitar a fresh install plays: a Strat HSS, bridge humbucker.</summary>
    public static readonly ChartSynthInstrument DefaultGuitar = new(ChartSynthModel.StratHss, ChartSynthPickup.Bridge, false);

    /// <summary>The bass a fresh install plays: a Jazz bass, both pickups (the real Growlybass setting).</summary>
    public static readonly ChartSynthInstrument DefaultBass = new(ChartSynthModel.JazzBass, ChartSynthPickup.NeckBridge, false);

    /// <summary>The default of a family.</summary>
    /// <param name="bass">True for the bass.</param>
    /// <returns><see cref="DefaultBass" /> or <see cref="DefaultGuitar" />.</returns>
    public static ChartSynthInstrument Default(bool bass) => bass ? DefaultBass : DefaultGuitar;

    /// <summary>
    ///     <paramref name="instrument" /> made valid for its family: an unknown model
    ///     or one of the other family falls back to the family default, a pickup the
    ///     model does not have to the model's default, and coil split is kept only
    ///     where it means something.
    /// </summary>
    /// <param name="instrument">The requested selection.</param>
    /// <param name="bass">The family it must belong to.</param>
    /// <param name="info">What the model offers, or null when it is not known (then only the family is checked).</param>
    /// <returns>A selection the engine accepts.</returns>
    public static ChartSynthInstrument Normalize(ChartSynthInstrument instrument, bool bass, ChartSynthModelInfo? info)
    {
        if (!Enum.IsDefined(instrument.Model) || instrument.IsBass != bass) return Default(bass);
        if (info == null) return Enum.IsDefined(instrument.Pickup) ? instrument : Default(bass);
        var pickup = info.Pickups.Contains(instrument.Pickup) ? instrument.Pickup : info.DefaultPickup;
        return new ChartSynthInstrument(instrument.Model, pickup, instrument.CoilSplit && info.HasCoilSplit);
    }

    /// <summary>The saved selection of a family; the default when nothing (or nonsense) is saved.</summary>
    /// <param name="config">The config.</param>
    /// <param name="bass">True for the bass.</param>
    /// <returns>The saved selection, family-checked.</returns>
    public static ChartSynthInstrument FromConfig(RigConfig config, bool bass)
    {
        ArgumentNullException.ThrowIfNull(config);
        var model = bass ? config.ChartSynthBassModel : config.ChartSynthGuitarModel;
        var pickup = bass ? config.ChartSynthBassPickup : config.ChartSynthGuitarPickup;
        var split = bass ? config.ChartSynthBassCoilSplit : config.ChartSynthGuitarCoilSplit;
        return TryParse(model, pickup, split, bass, out var instrument) ? instrument : Default(bass);
    }

    /// <summary>
    ///     A selection saved by NAME (the config, a profile's instrument file, a
    ///     song tone's override): false for an unknown name or a model of the
    ///     other family, so the caller decides what "nothing usable" means.
    /// </summary>
    /// <param name="model">A <see cref="ChartSynthModel" /> name (case-sensitive).</param>
    /// <param name="pickup">A <see cref="ChartSynthPickup" /> name.</param>
    /// <param name="coilSplit">The coil split.</param>
    /// <param name="bass">The family it must belong to.</param>
    /// <param name="instrument">The selection, family-checked.</param>
    /// <returns>True when both names were valid for the family.</returns>
    public static bool TryParse(string? model, string? pickup, bool coilSplit, bool bass, out ChartSynthInstrument instrument)
    {
        instrument = Default(bass);
        if (!Enum.TryParse<ChartSynthModel>(model, ignoreCase: false, out var m) || !Enum.IsDefined(m)
            || !Enum.TryParse<ChartSynthPickup>(pickup, ignoreCase: false, out var p) || !Enum.IsDefined(p)
            || ((int)m >= 16) != bass
            || int.TryParse(model, out _) || int.TryParse(pickup, out _))
        {
            return false;
        }
        instrument = new ChartSynthInstrument(m, p, coilSplit);
        return true;
    }

    /// <summary>
    ///     The legacy app-wide selection, from before the instrument followed the
    /// player profile: true while either family's model is still in the
    ///     config. <c>ChartSynthInstrumentCoordinator</c> moves it into the
    ///     active profile once and then <see cref="ClearConfig" />s it.
    /// </summary>
    /// <param name="config">The config.</param>
    /// <returns>True when there is something to move.</returns>
    public static bool HasLegacyConfig(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !string.IsNullOrEmpty(config.ChartSynthGuitarModel) || !string.IsNullOrEmpty(config.ChartSynthBassModel);
    }

    /// <summary>Empties the app-wide fields — what makes the move one-shot.</summary>
    /// <param name="config">The config to write.</param>
    public static void ClearConfig(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.ChartSynthGuitarModel = config.ChartSynthGuitarPickup = string.Empty;
        config.ChartSynthBassModel = config.ChartSynthBassPickup = string.Empty;
        config.ChartSynthGuitarCoilSplit = config.ChartSynthBassCoilSplit = false;
    }

    /// <summary>Writes <paramref name="instrument" /> into its family's config fields.</summary>
    /// <param name="config">The config to write.</param>
    /// <param name="instrument">The selection.</param>
    public static void WriteTo(RigConfig config, ChartSynthInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (instrument.IsBass)
        {
            config.ChartSynthBassModel = instrument.Model.ToString();
            config.ChartSynthBassPickup = instrument.Pickup.ToString();
            config.ChartSynthBassCoilSplit = instrument.CoilSplit;
        }
        else
        {
            config.ChartSynthGuitarModel = instrument.Model.ToString();
            config.ChartSynthGuitarPickup = instrument.Pickup.ToString();
            config.ChartSynthGuitarCoilSplit = instrument.CoilSplit;
        }
    }
}
