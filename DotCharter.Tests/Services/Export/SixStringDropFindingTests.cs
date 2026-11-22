using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Export;

/// <summary>#858: the six-string projection's loss is a finding in the export dialog, once per arrangement, with counts.</summary>
public sealed class SixStringDropFindingTests
{
    [Fact]
    public void A_seven_string_arrangement_gets_one_finding_with_what_the_projection_drops()
    {
        var project = RsXmlSixStringProjectionTests.Chart(7);

        var findings = new ChartExportValidator { SixStringProjection = true }.Validate(project);

        var drop = Assert.Single(findings, f => f.MessageKey == ChartExportValidator.KeySixStringDrop);
        Assert.Equal(ExportFindingSeverity.Warning, drop.Severity);
        Assert.Equal("Lead", drop.ArrangementName);
        Assert.StartsWith("7|2|", drop.Detail);
    }

    [Fact]
    public void A_six_string_arrangement_or_the_option_off_gets_no_finding()
    {
        Assert.DoesNotContain(new ChartExportValidator { SixStringProjection = true }.Validate(RsXmlSixStringProjectionTests.Chart(6)),
            f => f.MessageKey == ChartExportValidator.KeySixStringDrop);
        Assert.DoesNotContain(new ChartExportValidator().Validate(RsXmlSixStringProjectionTests.Chart(7)),
            f => f.MessageKey == ChartExportValidator.KeySixStringDrop);
    }
}
