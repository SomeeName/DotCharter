using DotCharter.Services.Ai.Generate;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     The closed-catalog gate. Rocksmith's gear is a fixed list shipped with the
///     game, so a generated tone naming anything else is not a tone that sounds
///     slightly wrong — it is a tone the game refuses.
/// </summary>
public sealed class RsToneCatalogValidationTests
{
    private static readonly RsGearCatalog Catalog = RsGearCatalog.Shared;

    private static RsTone Valid(string key = "gen_1")
    {
        var (amp, cab) = GenerateFakes.RealGuitarGear();
        return new RsTone(
            key, "Generated", RsToneAuthoring.DefaultVolumeDb,
            [RsToneAuthoring.CleanDescriptorUIName],
            new RsAmp(amp, Catalog.GetCategory(amp), new Dictionary<string, float>(StringComparer.Ordinal)),
            new RsCab(cab, Catalog.GetCategory(cab), new Dictionary<string, float>(StringComparer.Ordinal)),
            [], [], []);
    }

    [Fact]
    public void ADefaultAuthoredTone_Passes()
    {
        // The guard against the guard: the app's own "new tone" must validate, or
        // every case below passes for the wrong reason.
        Assert.Null(RsToneCatalogValidation.FirstFault(
            RsToneAuthoring.CreateDefaultTone("tone_a", "Tone A", Catalog), Catalog));
        Assert.Null(RsToneCatalogValidation.FirstFault(Valid(), Catalog));
    }

    [Fact]
    public void GearTheGameDoesNotHave_IsRejected_AndTheFaultNamesIt()
    {
        var tone = Valid() with
        {
            Amp = new RsAmp("Amp_Marshall_JCM800", "Amp", new Dictionary<string, float>(StringComparer.Ordinal))
        };

        var fault = RsToneCatalogValidation.FirstFault(tone, Catalog);

        Assert.NotNull(fault);
        Assert.Equal(RsToneFaultCodes.UnknownGear, fault!.Code);
        Assert.Equal("Amp_Marshall_JCM800", fault.GearKey);
        Assert.Contains("Amp_Marshall_JCM800", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKnobOutsideItsRange_IsRejected_AndTheFaultNamesTheValueAndTheRange()
    {
        var (amp, _) = GenerateFakes.RealGuitarGear();
        var knob = GenerateFakes.FirstKnob(amp);
        var over = knob.MaxValue + 42f;
        var tone = Valid() with
        {
            Amp = new RsAmp(amp, Catalog.GetCategory(amp),
                new Dictionary<string, float>(StringComparer.Ordinal) { [knob.Key] = over })
        };

        var fault = RsToneCatalogValidation.FirstFault(tone, Catalog);

        Assert.NotNull(fault);
        Assert.Equal(RsToneFaultCodes.KnobOutOfRange, fault!.Code);
        Assert.Equal(knob.Key, fault.KnobKey);
        Assert.Contains(over.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItRejects_RatherThanClampingLikeTheEditorDoes()
    {
        // RsToneAuthoring.ClampKnobValue is right for a human dragging a knob past
        // its stop; it is wrong for a generated value, where an out-of-range number
        // means the model misread the unit. This pins the difference.
        var (amp, _) = GenerateFakes.RealGuitarGear();
        var knob = GenerateFakes.FirstKnob(amp);

        var clamped = RsToneAuthoring.ClampKnobValue(knob, knob.MaxValue + 42f);
        Assert.Equal(knob.MaxValue, clamped, 3);

        var tone = Valid() with
        {
            Amp = new RsAmp(amp, Catalog.GetCategory(amp),
                new Dictionary<string, float>(StringComparer.Ordinal) { [knob.Key] = knob.MaxValue + 42f })
        };
        Assert.False(RsToneCatalogValidation.IsValid(tone, Catalog));
    }

    [Fact]
    public void AKnobTheGearDoesNotHave_IsRejected()
    {
        var (amp, _) = GenerateFakes.RealGuitarGear();
        var tone = Valid() with
        {
            Amp = new RsAmp(amp, Catalog.GetCategory(amp),
                new Dictionary<string, float>(StringComparer.Ordinal) { [amp + "_Sparkle"] = 50f })
        };

        var fault = RsToneCatalogValidation.FirstFault(tone, Catalog);
        Assert.Equal(RsToneFaultCodes.UnknownKnob, fault!.Code);
    }

    [Fact]
    public void ANonFiniteKnob_IsRejected_RatherThanPassingAsNotOutOfRange()
    {
        var (amp, _) = GenerateFakes.RealGuitarGear();
        var knob = GenerateFakes.FirstKnob(amp);
        var tone = Valid() with
        {
            Amp = new RsAmp(amp, Catalog.GetCategory(amp),
                new Dictionary<string, float>(StringComparer.Ordinal) { [knob.Key] = float.NaN })
        };

        Assert.Equal(RsToneFaultCodes.KnobNotFinite, RsToneCatalogValidation.FirstFault(tone, Catalog)!.Code);
    }

    [Fact]
    public void AnAmpInTheCabinetSlot_IsRejected()
    {
        var (amp, _) = GenerateFakes.RealGuitarGear();
        var tone = Valid() with
        {
            Cabinet = new RsCab(amp, "Amp", new Dictionary<string, float>(StringComparer.Ordinal))
        };

        Assert.Equal(RsToneFaultCodes.WrongGearType, RsToneCatalogValidation.FirstFault(tone, Catalog)!.Code);
    }

    [Fact]
    public void AFifthPedalOnARail_IsRejected()
    {
        var pedal = RsToneAuthoring.GearOptions(Catalog, RsGearType.Pedal).First();
        var rail = Enumerable.Range(1, 5)
            .Select(i => new RsPedal(RsGearRole.PrePedal, i, pedal.Key, RsGearType.Pedal, pedal.Category,
                new Dictionary<string, float>(StringComparer.Ordinal)))
            .ToList();

        var fault = RsToneCatalogValidation.FirstFault(Valid() with { PrePedals = rail }, Catalog);
        Assert.Equal(RsToneFaultCodes.TooManySlots, fault!.Code);
    }

    [Fact]
    public void AVolumeOutsideTheGamesRange_IsRejected()
    {
        Assert.Equal(RsToneFaultCodes.VolumeOutOfRange,
            RsToneCatalogValidation.FirstFault(Valid() with { VolumeDb = 6f }, Catalog)!.Code);
    }

    [Fact]
    public void AnInventedDescriptor_IsRejected()
    {
        var tone = Valid() with { ToneDescriptors = ["$[99999]SHOEGAZE"] };
        Assert.Equal(RsToneFaultCodes.UnknownDescriptor,
            RsToneCatalogValidation.FirstFault(tone, Catalog)!.Code);
    }

    [Fact]
    public void AToneKeyTheManifestCannotCarry_IsRejected()
    {
        Assert.Equal(RsToneFaultCodes.BadKey,
            RsToneCatalogValidation.FirstFault(Valid("2 bad key!"), Catalog)!.Code);
    }

    [Fact]
    public void AnOfficialDigitFirstKey_FailsTheAuthoringRule_ButNotTheGameLoadCheck()
    {
        Assert.Equal(RsToneFaultCodes.BadKey, RsToneCatalogValidation.FirstFault(Valid("311allm_clean"), Catalog)!.Code);
        Assert.Null(RsToneCatalogValidation.FirstLoadFault(Valid("311allm_clean"), Catalog));
        var unknownAmp = Valid("311allm_clean") with { Amp = Valid().Amp with { GearKey = "Amp_NotInTheGame" } };
        Assert.Equal(RsToneFaultCodes.UnknownGear, RsToneCatalogValidation.FirstLoadFault(unknownAmp, Catalog)!.Code);
    }

    [Fact]
    public void RequireThrows_AndTheExceptionCarriesTheFault()
    {
        var tone = Valid() with
        {
            Amp = new RsAmp("Amp_Nope", "Amp", new Dictionary<string, float>(StringComparer.Ordinal))
        };

        var ex = Assert.Throws<RsToneCatalogException>(
            () => RsToneCatalogValidation.Require(tone, Catalog, "the generated tone 'x'"));

        Assert.Equal(RsToneFaultCodes.UnknownGear, ex.Fault.Code);
        Assert.Contains("Amp_Nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyCatalogue_RejectsEverything_RatherThanPassingIt() =>
        // RsGearCatalog.Empty is the degraded fallback for an unreadable embedded
        // resource. A validator that passed everything against it would turn a broken
        // install into a silently unvalidated writer.
        Assert.NotNull(RsToneCatalogValidation.FirstFault(Valid(), RsGearCatalog.Empty));
}
