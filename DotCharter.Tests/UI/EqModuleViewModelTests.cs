using DotCharter.Tone.Audio.Equalizer;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI;

/// <summary>
///     The EQ module's view model: that every gesture reaches the ENGINE, and
///     that nothing on screen can disagree with what was pushed.
/// </summary>
/// <remarks>
///     Default gate, no platform: the view model takes only
///     <c>IAudioPlaybackService</c>, which is the whole reason the plot control
///     owns no maths and no state.
/// </remarks>
public sealed class EqModuleViewModelTests
{
    private static (EqModuleViewModel Vm, FakeAudioPlaybackService Audio) Build(bool engineHasEq = true)
    {
        var audio = new FakeAudioPlaybackService { SupportsParametricEq = engineHasEq };
        return (new EqModuleViewModel(audio, FakeLocalization.English()), audio);
    }

    // ── every gesture reaches the engine ─────────────────────────────────

    [Fact]
    public void Constructing_PushesOnce_SoTheEngineKnowsTheRouteAndTrimBeforeAnyEdit()
    {
        var (_, audio) = Build();
        Assert.Equal(1, audio.EqPushCount);
        Assert.NotNull(audio.LastEqSection);
    }

    [Fact]
    public void ChangingTheBandType_PushesTheSection()
    {
        var (vm, audio) = Build();
        var before = audio.EqPushCount;

        vm.SelectedBandType = EqBandType.Notch;

        Assert.True(audio.EqPushCount > before, "changing a band's type never reached the engine");
        Assert.Equal(EqBandType.Notch, audio.LastEqSection!.Bands[vm.SelectedIndex].Type);
    }

    [Fact]
    public void MovingTheFrequencySlider_PushesTheSection()
    {
        var (vm, audio) = Build();
        var before = audio.EqPushCount;

        vm.SelectedFrequencyNormalized = 0.75;

        Assert.True(audio.EqPushCount > before);
        Assert.Equal(EqCurve.NormalizedToFrequency(0.75),
            audio.LastEqSection!.Bands[vm.SelectedIndex].FrequencyHz, 3);
    }

    [Fact]
    public void MovingTheTrim_PushesTheSection()
    {
        var (vm, audio) = Build();
        vm.TrimDb = -7.5;
        Assert.Equal(-7.5, audio.LastEqSection!.TrimDb, 6);
    }

    [Fact]
    public void ChangingTheRoute_PushesTheSection()
    {
        var (vm, audio) = Build();
        vm.SetRouteGuitarCommand.Execute(null);
        Assert.Equal(EqRoute.GuitarIn, audio.LastEqSection!.Route);
    }

    [Fact]
    public void TogglingTheSectionPower_PushesTheSection()
    {
        var (vm, audio) = Build();
        vm.IsSectionEnabled = !vm.IsSectionEnabled; // what the EqPower ToggleButton writes
        Assert.False(audio.LastEqSection!.IsEnabled);
        Assert.False(vm.IsSectionEnabled);
    }

    [Fact]
    public void SoloingABand_PushesTheSection()
    {
        var (vm, audio) = Build();
        vm.SelectedIndex = 2;
        vm.ToggleSoloCommand.Execute(null);

        Assert.Equal(2, audio.LastEqSection!.SoloedBand);
        Assert.True(vm.IsSelectedSoloed);
    }

    [Fact]
    public void AChipsDot_TogglesItsOwnBandWithoutSelectingIt()
    {
        var (vm, audio) = Build();
        vm.SelectedPresetKey = "FindTheGuitar";
        vm.SelectedIndex = 0;

        vm.Bands[4].ToggleEnabledCommand.Execute(null);

        Assert.False(audio.LastEqSection!.Bands[4].Enabled);
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void AnEditFromThePlot_PushesAndAdoptsThatBandAsTheSelection()
    {
        var (vm, audio) = Build();
        vm.SelectedIndex = 0;

        // What the plot does on a drag: write through the SECTION, then report.
        vm.Section.SetBand(4, vm.Section.Bands[4] with { FrequencyHz = 5000 });
        vm.OnBandEditedFromPlot(4);

        Assert.Equal(4, vm.SelectedIndex);
        Assert.Equal(5000, audio.LastEqSection!.Bands[4].FrequencyHz, 3);
    }

    // ── one source of truth ──────────────────────────────────────────────

    [Fact]
    public void TheBadge_ReadsTheENGINEsCount_NotAManagedOne()
    {
        var (vm, _) = Build();
        vm.SelectedPresetKey = "FindTheGuitar";

        // The fake mirrors the real engine's predicate over what was pushed.
        Assert.Equal("5/6", vm.ActiveBadge);
    }

    [Fact]
    public void TheBadge_SaysSOLOWhileABandIsSoloed() =>
        // With a solo standing the count is always 1, so printing "1/6" would be
        // true and useless.
        Assert.Equal(FakeLocalization.English().Format("Inspector_EqSoloBadge", 3), SoloedThird().ActiveBadge);

    private static EqModuleViewModel SoloedThird()
    {
        var (vm, _) = Build();
        vm.SelectedIndex = 2;
        vm.ToggleSoloCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void ThePushedSection_IsTheSameInstanceTheViewBinds()
    {
        // Not an identity nicety: the plot control reads Section directly to
        // draw the curve and hit-test the handles. If the push sent a COPY, the
        // drawn curve and the running DSP would be two objects that merely
        // started equal — the display-drifts-from-DSP fault this whole design is
        // arranged to make impossible.
        var (vm, audio) = Build();
        vm.TrimDb = 1;

        Assert.Same(vm.Section, audio.LastEqSection);
    }

    [Fact]
    public void TheSelectedChip_IsTheOneTheEditorIsEditing()
    {
        var (vm, _) = Build();
        vm.SelectedIndex = 4;

        Assert.True(vm.Bands[4].IsSelected);
        Assert.All(vm.Bands.Where(b => b.Index != 4), b => Assert.False(b.IsSelected));
    }

    // ── the honesty gate ─────────────────────────────────────────────────

    [Fact]
    public void WithNoParametricEqInTheEngine_TheModuleIsNotLive()
    {
        var (vm, audio) = Build(engineHasEq: false);

        Assert.False(vm.IsLive);
        Assert.Equal(0, audio.EqPushCount);
        Assert.False(string.IsNullOrWhiteSpace(vm.UnavailableReason));
    }

    [Fact]
    public void WithNoParametricEqInTheEngine_NothingIsEverPushed()
    {
        // The rule this repo learned from CoopRigApplier: a control the engine
        // cannot serve must not merely LOOK disabled, it must not act either.
        var (vm, audio) = Build(engineHasEq: false);

        vm.SelectedBandType = EqBandType.Notch;
        vm.TrimDb = 6;
        vm.SetRouteBothCommand.Execute(null);
        vm.ToggleSoloCommand.Execute(null);

        Assert.Equal(0, audio.EqPushCount);
        Assert.Null(audio.LastEqSection);
    }

    [Fact]
    public void WithNoParametricEqInTheEngine_TheAnalyserDrawsNothing()
    {
        var (vm, _) = Build(engineHasEq: false);
        vm.PollSpectrum();

        // NULL, never an invented shape — the plot skips the analyser entirely
        // rather than showing a hump no signal produced.
        Assert.Null(vm.Spectrum);
    }

    [Fact]
    public void TheAnalyser_TakesWhateverTheEngineWrote()
    {
        var (vm, _) = Build();
        vm.PollSpectrum();

        Assert.NotNull(vm.Spectrum);
        Assert.All(vm.Spectrum!, db => Assert.InRange(db, -140f, 6f));
    }

    // ── the adapting gain row ────────────────────────────────────────────

    [Theory]
    [InlineData(EqBandType.HighPass, false)]
    [InlineData(EqBandType.LowShelf, true)]
    [InlineData(EqBandType.Bell, true)]
    [InlineData(EqBandType.HighShelf, true)]
    [InlineData(EqBandType.LowPass, false)]
    [InlineData(EqBandType.Notch, false)]
    public void TheGainRow_AdaptsToTheBandsShape(EqBandType type, bool hasGain)
    {
        var (vm, _) = Build();
        vm.SelectedBandType = type;

        Assert.Equal(hasGain, vm.SelectedHasGain);
        if (!hasGain)
            Assert.False(string.IsNullOrWhiteSpace(vm.SelectedSlopeText));
    }

    [Fact]
    public void AGainlessShape_PrintsTheFiltersRealSlope() =>
        // 12 dB/oct is a FACT about a biquad, and EqCurveTests asserts the filter
        // really rolls off at it. This is the other half: that the number the
        // user reads is that same constant and not a decorative string.
        Assert.Contains(EqLimits.SlopeDbPerOctave.ToString(), HighPassed().SelectedSlopeText,
            StringComparison.Ordinal);

    private static EqModuleViewModel HighPassed()
    {
        var (vm, _) = Build();
        vm.SelectedBandType = EqBandType.HighPass;
        return vm;
    }

    [Fact]
    public void ANotch_SaysFullCutRatherThanASlope()
    {
        var (vm, _) = Build();
        vm.SelectedBandType = EqBandType.Notch;

        Assert.DoesNotContain(EqLimits.SlopeDbPerOctave.ToString(), vm.SelectedSlopeText,
            StringComparison.Ordinal);
    }

    // ── presets ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryPreset_IsOfferedByThePicker() =>
        Assert.Equal(EqPresets.Keys, Build().Vm.Presets);

    [Fact]
    public void LoadingAPreset_PushesEveryBand()
    {
        var (vm, audio) = Build();
        vm.SelectedPresetKey = "NotchHum";

        Assert.Equal(EqBandType.Notch, audio.LastEqSection!.Bands[1].Type);
        Assert.Equal(50, audio.LastEqSection.Bands[1].FrequencyHz, 3);
    }

    [Fact]
    public void EditingAfterAPreset_MakesTheHeaderReadCustom()
    {
        var (vm, _) = Build();
        vm.SelectedPresetKey = "KillTheKick";
        Assert.False(vm.IsCustom);

        vm.SelectedQNormalized = 0.9;

        Assert.True(vm.IsCustom);
        Assert.Null(vm.SelectedPresetKey);
    }

    [Fact]
    public void EveryPresetKey_HasALabel() =>
        Assert.All(EqPresets.Keys, key =>
        {
            var label = EqModuleViewModel.PresetLabel(key, FakeLocalization.English());
            Assert.False(string.IsNullOrWhiteSpace(label));
            // A missing resx entry comes back as the KEY, which reads as a
            // plausible label and would ship untranslated.
            Assert.NotEqual(EqPresets.ResourceKeyFor(key), label);
        });

    [Fact]
    public void EveryBandType_HasAShortLabelAndAName() =>
        Assert.All(Enum.GetValues<EqBandType>(), type =>
        {
            var (vm, _) = Build();
            vm.SelectedBandType = type;

            Assert.False(string.IsNullOrWhiteSpace(vm.SelectedTypeName));
            Assert.NotEqual($"Inspector_EqTypeName_{type}", vm.SelectedTypeName);

            var chip = vm.Bands[vm.SelectedIndex];
            Assert.False(string.IsNullOrWhiteSpace(chip.TypeLabel));
            Assert.NotEqual($"Inspector_EqTypeShort_{type}", chip.TypeLabel);
        });

    [Fact]
    public void EveryBand_NamesAnIdentityTokenThatExistsInTheLadder()
    {
        var (vm, _) = Build();
        for (var i = 0; i < vm.Bands.Count; i++)
            Assert.Equal($"FireEqBand{i + 1}", vm.Bands[i].ColorToken);
    }

    // ── the readouts ─────────────────────────────────────────────────────

    /// <summary>
    ///     The unit switches at 1 kHz and the precision switches again at 10 kHz
    ///     — the mockup's own rule, and the reason the readout is a method rather
    ///     than a <c>StringFormat</c>.
    /// </summary>
    /// <remarks>
    ///     Asserted STRUCTURALLY (the unit, and how many digits follow the
    ///     decimal separator) rather than against a literal string: the readout
    ///     is formatted in the USER's culture, so a literal "3.00 kHz" pins this
    ///     test to an en-US machine and fails on the owner's German one for a
    ///     reason that is not a defect.
    /// </remarks>
    [Theory]
    [InlineData(400, "Hz", 0)]
    [InlineData(999, "Hz", 0)]
    [InlineData(3000, "kHz", 2)]
    [InlineData(9999, "kHz", 2)]
    [InlineData(12000, "kHz", 1)]
    [InlineData(20000, "kHz", 1)]
    public void TheFrequencyReadout_ChangesUnitAtOneKilohertzAndPrecisionAtTen(
        double hz, string unit, int decimals)
    {
        var text = EqModuleViewModelFormatProbe.Format(hz);
        Assert.EndsWith(" " + unit, text, StringComparison.Ordinal);

        var separator = System.Globalization.CultureInfo.CurrentCulture
            .NumberFormat.NumberDecimalSeparator;
        var number = text[..^(unit.Length + 1)];
        var at = number.IndexOf(separator, StringComparison.Ordinal);
        Assert.Equal(decimals, at < 0 ? 0 : number.Length - at - separator.Length);
    }

    [Fact]
    public void TheGainReadout_AlwaysCarriesItsSign()
    {
        var (vm, _) = Build();
        vm.SelectedBandType = EqBandType.Bell;

        vm.SelectedGainDb = 6.5;
        Assert.StartsWith("+", vm.SelectedGainText, StringComparison.Ordinal);

        vm.SelectedGainDb = -6.5;
        Assert.StartsWith("-", vm.SelectedGainText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrimReadout_AlwaysCarriesItsSign()
    {
        var (vm, _) = Build();
        vm.TrimDb = 4;
        Assert.StartsWith("+", vm.TrimText, StringComparison.Ordinal);
    }

    // ── the engine arrives LATER than this view model does ───────────────
    //
    // The audio engine is built lazily — the first audio load or the first
    // transport call — while this view model is constructed with the transport,
    // before the window exists. So on a cold start `SupportsParametricEq` is
    // false here and true a second later.

    [Fact]
    public void WhenTheEngineArrivesAfterConstruction_IsLiveFlipsAndSaysSo()
    {
        var (vm, audio) = Build(engineHasEq: false);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.IsLive);

        audio.SupportsParametricEq = true;
        vm.PollSpectrum();

        Assert.True(vm.IsLive);
        // Without the notification every `IsEnabled="{Binding IsLive}"` in the
        // module keeps whatever it read first, and the whole surface stays dead
        // for the session.
        Assert.Contains(nameof(EqModuleViewModel.IsLive), raised);
    }

    [Fact]
    public void WhenTheEngineArrivesAfterConstruction_TheSectionIsPushedToIt()
    {
        var (vm, audio) = Build(engineHasEq: false);
        Assert.Equal(0, audio.EqPushCount);

        audio.SupportsParametricEq = true;
        vm.PollSpectrum();

        // This is the half that produced the owner's screenshot: without it the
        // first engine of the session runs an EQ it was never told about, so the
        // badge reports the engine's truthful 0/6 over a section the DSP has
        // never seen.
        Assert.Equal(1, audio.EqPushCount);
        Assert.Same(vm.Section, audio.LastEqSection);
    }

    [Fact]
    public void TheBadge_ReadsTheEngineWhenThereIsOneAndTheSectionWhenThereIsNot()
    {
        var (vm, audio) = Build(engineHasEq: false);
        vm.SelectedPresetKey = "FindTheGuitar";

        // Nothing to ask: the section's own count, which is a fact about what
        // the user has set up.
        Assert.Equal(-1, audio.EngineEqActiveCount);
        Assert.Equal($"{vm.Section.ActiveCount}/{EqLimits.BandCount}", vm.ActiveBadge);

        audio.SupportsParametricEq = true;
        vm.PollSpectrum();

        // Something to ask: the ENGINE's number, which is a statement about the
        // DSP.
        Assert.Equal(vm.Section.ActiveCount, audio.EngineEqActiveCount);
        Assert.Equal($"{audio.EngineEqActiveCount}/{EqLimits.BandCount}", vm.ActiveBadge);
    }

    // ── the sliders' double-click defaults ───────────────────────────────

    [Fact]
    public void TheFrequencyDefault_FollowsTheSelectedBandsShape()
    {
        var (vm, _) = Build();

        vm.SelectedBandType = EqBandType.HighPass;
        Assert.Equal(EqCurve.FrequencyToNormalized(EqPresets.DefaultFrequencyFor(EqBandType.HighPass)),
            vm.SelectedFrequencyDefaultNormalized, 9);

        vm.SelectedBandType = EqBandType.LowPass;
        Assert.Equal(EqCurve.FrequencyToNormalized(EqPresets.DefaultFrequencyFor(EqBandType.LowPass)),
            vm.SelectedFrequencyDefaultNormalized, 9);
    }

    [Fact]
    public void TheQDefault_IsTheSameNumberTheResetButtonWrites()
    {
        // Two literals is how a double-click and the RESET button one row above
        // it come to disagree about what "default" means.
        var (vm, _) = Build();
        vm.SelectedQNormalized = 0.9;
        Assert.NotEqual(EqLimits.DefaultQ, vm.Section.Bands[vm.SelectedIndex].Q, 6);

        vm.ResetSelectedCommand.Execute(null);

        Assert.Equal(EqLimits.DefaultQ, vm.Section.Bands[vm.SelectedIndex].Q, 9);
        Assert.Equal(EqModuleViewModel.QDefaultNormalized, vm.SelectedQNormalized, 9);
        Assert.Equal(EqModuleViewModel.GainDefaultDb, vm.SelectedGainDb, 9);
    }
}

/// <summary>Reaches the internal frequency formatter without widening it — the
/// unit switch at 1 kHz and the precision switch at 10 kHz are the mockup's own
/// rule and are worth pinning.</summary>
internal static class EqModuleViewModelFormatProbe
{
    public static string Format(double hz) => EqModuleViewModel.FormatFrequency(hz);
}
