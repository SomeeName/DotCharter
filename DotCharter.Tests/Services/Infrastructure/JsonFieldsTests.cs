using System.Text.Json;
using DotCharter.Tone.Infrastructure.Json;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class JsonFieldsTests
{
    private static readonly JsonElement Sample = Parse("""
        {
          "name": "Song",
          "empty": "",
          "count": 42,
          "big": 9999999999,
          "ratio": 0.5,
          "countText": "17",
          "negativeText": "-3",
          "notANumber": "seventeen",
          "flag": true,
          "off": false,
          "nothing": null,
          "list": [1, 2]
        }
        """);

    [Fact]
    public void String_ReadsAStringField_AndIsNullForEverythingElse()
    {
        Assert.Equal("Song", JsonFields.String(Sample, "name"));
        Assert.Equal("", JsonFields.String(Sample, "empty"));
        Assert.Null(JsonFields.String(Sample, "count"));
        Assert.Null(JsonFields.String(Sample, "nothing"));
        Assert.Null(JsonFields.String(Sample, "missing"));
        Assert.Equal("", JsonFields.StringOrEmpty(Sample, "missing"));
    }

    [Fact]
    public void Int32_ReadsOnlyARealNumberThatFits()
    {
        Assert.Equal(42, JsonFields.Int32(Sample, "count"));
        Assert.Null(JsonFields.Int32(Sample, "countText"));
        Assert.Null(JsonFields.Int32(Sample, "big"));
        Assert.Null(JsonFields.Int32(Sample, "ratio"));
        Assert.Equal(9999999999L, JsonFields.Int64(Sample, "big"));
        Assert.Equal(0.5, JsonFields.Double(Sample, "ratio"));
        Assert.Null(JsonFields.Double(Sample, "name"));
    }

    [Fact]
    public void Int32OrNumericText_AlsoReadsANumberWrittenAsText()
    {
        Assert.Equal(42, JsonFields.Int32OrNumericText(Sample, "count"));
        Assert.Equal(17, JsonFields.Int32OrNumericText(Sample, "countText"));
        Assert.Equal(-3, JsonFields.Int32OrNumericText(Sample, "negativeText"));
        Assert.Null(JsonFields.Int32OrNumericText(Sample, "notANumber"));
        Assert.Null(JsonFields.Int32OrNumericText(Sample, "missing"));
    }

    [Fact]
    public void Int64OrNumericText_ReadsALongNumberOrNumericText()
    {
        Assert.Equal(9999999999L, JsonFields.Int64OrNumericText(Sample, "big"));
        Assert.Equal(17L, JsonFields.Int64OrNumericText(Sample, "countText"));
        Assert.Equal(-3L, JsonFields.Int64OrNumericText(Sample, "negativeText"));
        Assert.Null(JsonFields.Int64OrNumericText(Sample, "ratio"));
        Assert.Null(JsonFields.Int64OrNumericText(Sample, "notANumber"));
        Assert.Null(JsonFields.Int64OrNumericText(Sample, "missing"));
    }

    [Fact]
    public void IsTrue_IsTrueOnlyForTheLiteralTrue()
    {
        Assert.True(JsonFields.IsTrue(Sample, "flag"));
        Assert.False(JsonFields.IsTrue(Sample, "off"));
        Assert.False(JsonFields.IsTrue(Sample, "name"));
        Assert.False(JsonFields.IsTrue(Sample, "missing"));
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"text\"")]
    [InlineData("12")]
    [InlineData("null")]
    public void AnElementThatIsNotAnObject_ReadsAsAbsent_InsteadOfThrowing(string json)
    {
        var element = Parse(json);

        Assert.Null(JsonFields.String(element, "name"));
        Assert.Null(JsonFields.Int32(element, "count"));
        Assert.Null(JsonFields.Int32OrNumericText(element, "count"));
        Assert.Null(JsonFields.Int64(element, "count"));
        Assert.Null(JsonFields.Int64OrNumericText(element, "count"));
        Assert.Null(JsonFields.Double(element, "count"));
        Assert.False(JsonFields.IsTrue(element, "flag"));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
