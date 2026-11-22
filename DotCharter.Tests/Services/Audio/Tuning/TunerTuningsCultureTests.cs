using System.Globalization;
using System.Reflection;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Tests.Services.Audio.Tuning;

public sealed class TunerTuningsCultureTests
{
    [Fact]
    public void New_identifiers_use_invariant_negative_signs()
    {
        var previous = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("sv-SE").Clone();
        culture.NumberFormat.NegativeSign = "\u2212";
        try
        {
            CultureInfo.CurrentCulture = culture;
            var createId = typeof(TunerTunings).GetMethod("IdOf", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal("guitar:-2,0,0,0,0,0", (string)createId.Invoke(null, [false, new[] { -2, 0, 0, 0, 0, 0 }])!);
            Assert.Equal("bass:-2,0,0,0", (string)createId.Invoke(null, [true, new[] { -2, 0, 0, 0 }])!);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("guitar:\u22122,0,0,0,0,0", "guitar:-2,0,0,0,0,0")]
    [InlineData("bass:\u22121,\u22121,\u22121,\u22121", "bass:-1,-1,-1,-1")]
    [InlineData("guitar:\u061c-2,0,0,0,0,0", "guitar:-2,0,0,0,0,0")]
    [InlineData("guitar:\u200e-2,0,0,0,0,0", "guitar:-2,0,0,0,0,0")]
    public void Legacy_identifiers_restore_the_tuning_under_a_different_culture(string legacy, string invariant)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var tuning = TunerTunings.Find(legacy);
            Assert.NotNull(tuning);
            Assert.Equal(invariant, tuning.Id);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
