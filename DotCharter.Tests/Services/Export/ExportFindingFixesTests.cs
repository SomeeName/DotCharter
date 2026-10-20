using System.Reflection;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Every export finding carries a DECISION (#184): the verb that fixes it, or the reason
///     there is none. Reflected over the validator's <c>Key*</c> constants, so a new finding
///     cannot ship without one.
/// </summary>
public sealed class ExportFindingFixesTests
{
    private static List<string> ValidatorKeys() =>
    [
        .. typeof(ChartExportValidator)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) } && f.Name.StartsWith("Key", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
    ];

    [Fact]
    public void EveryValidatorKey_IsMappedToAFix_OrListedWithAReason()
    {
        var keys = ValidatorKeys();
        Assert.True(keys.Count >= 18, $"only {keys.Count} Key* constants found — the reflection no longer sees them");

        var undecided = keys.Where(k => !ExportFindingFixes.Fixes.ContainsKey(k) && !ExportFindingFixes.NoAutomaticFix.ContainsKey(k)).ToList();
        Assert.True(undecided.Count == 0,
            "export findings with no fix decision — map them in ExportFindingFixes.Fixes or give a reason in NoAutomaticFix: " +
            string.Join(", ", undecided));
    }

    [Fact]
    public void NoKey_IsBothFixedAndUnfixable_AndEveryReasonSaysSomething()
    {
        Assert.Empty(ExportFindingFixes.Fixes.Keys.Intersect(ExportFindingFixes.NoAutomaticFix.Keys));
        Assert.All(ExportFindingFixes.NoAutomaticFix, kv => Assert.True(kv.Value.Length > 20, $"{kv.Key}: a reason, please"));
        Assert.DoesNotContain(ExportFindingFixes.Fixes, kv => kv.Value == ExportFixKind.None);
    }

    [Fact]
    public void BothTables_NameOnlyRealValidatorKeys()
    {
        var keys = ValidatorKeys().ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(ExportFindingFixes.Fixes.Keys, k => !keys.Contains(k));
        Assert.DoesNotContain(ExportFindingFixes.NoAutomaticFix.Keys, k => !keys.Contains(k));
    }

    [Fact]
    public void AFinding_ReadsItsFixFromTheTable()
    {
        Assert.Equal(ExportFixKind.FillFhp,
            new ExportValidationFinding("Lead", ExportFindingSeverity.Error, ChartExportValidator.KeyMissingFhpCoverage).Fix);
        Assert.Equal(ExportFixKind.None,
            new ExportValidationFinding("Lead", ExportFindingSeverity.Error, ChartExportValidator.KeyFretOutOfRange).Fix);
    }
}
