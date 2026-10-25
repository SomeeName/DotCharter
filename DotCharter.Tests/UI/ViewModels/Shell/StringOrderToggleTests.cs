using System.Reflection;
using DotCharter.Services.Session;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     #229: View ▸ Tab string order writes the editor's String order (state and
///     config, saved), follows a change made in Settings, and the 3D highway reads
///     Play Mode's own order only while a run has the window.
/// </summary>
public sealed class StringOrderToggleTests
{
    public class SessionProxy : DispatchProxy
    {
        public AppConfig Config { get; } = new();
        public int Saves { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_" + nameof(IProjectSessionService.AppConfig)) return Config;
            if (targetMethod?.Name == nameof(IProjectSessionService.SaveAppConfig)) Saves++;
            return null;
        }
    }

    private static (StringOrderToggle Toggle, ProjectState State, SessionProxy Session) NewRig()
    {
        var state = new ProjectState();
        var session = DispatchProxy.Create<IProjectSessionService, SessionProxy>();
        return (new StringOrderToggle(state, session), state, (SessionProxy)(object)session);
    }

    [Fact]
    public void Toggling_WritesTheEditorsStringOrder_AndSavesIt()
    {
        var (toggle, state, session) = NewRig();

        toggle.IsTab = true;

        Assert.True(toggle.IsTab);
        Assert.True(state.InvertStrings);
        Assert.True(session.Config.InvertStrings);
        Assert.False(session.Config.PlayModeInvertStrings);
        Assert.False(state.PlayModeInvertStrings);
        Assert.Equal(1, session.Saves);

        toggle.IsTab = true;
        Assert.Equal(1, session.Saves);

        toggle.IsTab = false;
        Assert.False(session.Config.InvertStrings);
        Assert.Equal(2, session.Saves);
    }

    [Fact]
    public void TheCheck_FollowsAChangeMadeInSettings()
    {
        var (toggle, state, _) = NewRig();
        var raised = new List<string?>();
        toggle.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        state.InvertStrings = true;

        Assert.Contains(nameof(StringOrderToggle.IsTab), raised);
        Assert.True(toggle.IsTab);
    }

    [Fact]
    public void TheHighway_ReadsPlayModesOrder_OnlyDuringARun()
    {
        var state = new ProjectState();
        state.ApplyStringOrders(new AppConfig { InvertStrings = true, PlayModeInvertStrings = false });

        Assert.True(state.UserSettings.HighwayInvertStrings);

        state.GameplayPresentation = true;
        Assert.False(state.UserSettings.HighwayInvertStrings);

        state.GameplayPresentation = false;
        Assert.True(state.UserSettings.HighwayInvertStrings);
    }
}
