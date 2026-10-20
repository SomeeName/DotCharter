using DotCharter.Services.Editing.MultiTrack;

namespace DotCharter.Tests.Services.MultiTrack;

public class GhostArrangementsServiceTests
{
    [Fact]
    public void Toggle_AddsAndRemovesArrangementFromVisibleSet()
    {
        var service = new GhostArrangementsService();
        var arr = new Arrangement { Name = "Lead" };

        Assert.False(service.IsVisible(arr));

        service.Toggle(arr);
        Assert.True(service.IsVisible(arr));
        Assert.Contains(arr, service.VisibleGhosts);

        service.Toggle(arr);
        Assert.False(service.IsVisible(arr));
        Assert.DoesNotContain(arr, service.VisibleGhosts);
    }

    [Fact]
    public void SetVisible_IsIdempotent()
    {
        var service = new GhostArrangementsService();
        var arr = new Arrangement { Name = "Bass" };

        service.SetVisible(arr, true);
        service.SetVisible(arr, true);

        Assert.Single(service.VisibleGhosts);
    }

    [Fact]
    public void Clear_RemovesAllGhosts()
    {
        var service = new GhostArrangementsService();
        service.SetVisible(new Arrangement { Name = "A" }, true);
        service.SetVisible(new Arrangement { Name = "B" }, true);

        service.Clear();

        Assert.Empty(service.VisibleGhosts);
    }

    [Fact]
    public void Changed_FiresOnToggle()
    {
        var service = new GhostArrangementsService();
        var arr = new Arrangement { Name = "Rhythm" };
        var fires = 0;
        service.Changed += () => fires++;

        service.Toggle(arr);
        service.Toggle(arr);

        Assert.Equal(2, fires);
    }
}
