using DotCharter.UI.Layout;

namespace DotCharter.Tests.UI;

/// <summary>The AI chat column's width rules (#559): what a first show and a saved width give, and what is persisted.</summary>
public sealed class AiChatColumnPolicyTests
{
    [Theory]
    [InlineData(null, AiChatColumnPolicy.DefaultWidthPx)]
    [InlineData(520.0, 520.0)]
    [InlineData(100.0, AiChatColumnPolicy.DefaultWidthPx)]
    [InlineData(double.NaN, AiChatColumnPolicy.DefaultWidthPx)]
    [InlineData(double.PositiveInfinity, AiChatColumnPolicy.DefaultWidthPx)]
    public void ASavedWidth_IsUsedOnlyWhenTheChatFitsInIt(double? saved, double expected) =>
        Assert.Equal(expected, AiChatColumnPolicy.Restored(saved));

    [Fact]
    public void TheWidthPersisted_IsTheColumnsWhileItShows_TheRememberedOneWhileHidden_AndNothingDuringARun()
    {
        Assert.Equal(512, AiChatColumnPolicy.ToPersist(512, visible: true, inRun: false, rememberedPx: 400));
        Assert.Equal(460, AiChatColumnPolicy.ToPersist(0, visible: false, inRun: false, rememberedPx: 460));
        Assert.Null(AiChatColumnPolicy.ToPersist(512, visible: true, inRun: true, rememberedPx: 400));
    }
}
