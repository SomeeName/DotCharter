using DotCharter.Presentation.Navigation;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     <see cref="Navigator" /> and <see cref="BackStack" />: the pure half of
///     target-architecture.md § 3.1, against a recording presenter. The UI half
///     (<c>ScreenHost</c>) is <c>Perf/ScreenHostUiPerfSmokeTests</c>.
/// </summary>
public sealed class NavigatorTests
{
    private interface IA;

    private interface IB;

    private interface IC;

    private sealed class Screen(string name) : IA, IB, IC, IScreenLifecycle
    {
        public string Name { get; } = name;
        public int Inits { get; set; }
        public List<string> Log { get; } = [];
        public void OnShown() => Log.Add("shown");
        public void OnHidden() => Log.Add("hidden");
    }

    private sealed class RecordingPresenter : IScreenPresenter
    {
        public Dictionary<Type, Screen> Screens { get; } = [];
        public List<Type> Presented { get; } = [];

        public object Present(Type contract)
        {
            Presented.Add(contract);
            if (!Screens.TryGetValue(contract, out var screen))
                Screens[contract] = screen = new Screen(contract.Name);
            return screen;
        }
    }

    [Fact]
    public void Show_pushes_presents_and_runs_init_once_before_OnShown()
    {
        var presenter = new RecordingPresenter();
        var nav = new Navigator(presenter);

        nav.Show<IA>(a => ((Screen)a).Inits++);
        var a = presenter.Screens[typeof(IA)];

        Assert.Equal(typeof(IA), nav.Current);
        Assert.False(nav.CanGoBack);
        Assert.Equal(1, a.Inits);
        Assert.Equal(["shown"], a.Log);

        nav.Show<IB>();
        Assert.Equal(typeof(IB), nav.Current);
        Assert.True(nav.CanGoBack);
        Assert.Equal(["shown", "hidden"], a.Log);
    }

    [Fact]
    public void Back_returns_to_the_same_screen_without_running_its_init_again()
    {
        var presenter = new RecordingPresenter();
        var nav = new Navigator(presenter);
        nav.Show<IA>(a => ((Screen)a).Inits++);
        nav.Show<IB>();

        Assert.True(nav.Back());

        var a = presenter.Screens[typeof(IA)];
        Assert.Equal(typeof(IA), nav.Current);
        Assert.Equal(1, a.Inits);
        Assert.Equal(["shown", "hidden", "shown"], a.Log);
        Assert.Equal(["shown", "hidden"], presenter.Screens[typeof(IB)].Log);
        Assert.False(nav.Back());
        Assert.Equal(typeof(IA), nav.Current);
    }

    [Fact]
    public void Showing_a_screen_already_on_the_stack_unwinds_to_it()
    {
        var nav = new Navigator(new RecordingPresenter());
        nav.Show<IA>();
        nav.Show<IB>();
        nav.Show<IC>();

        nav.Show<IA>();

        Assert.Equal([typeof(IA)], nav.Stack);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void The_stack_is_bounded_and_drops_the_oldest()
    {
        var stack = new BackStack(capacity: 2);
        stack.Push(typeof(IA));
        stack.Push(typeof(IB));
        stack.Push(typeof(IC));

        Assert.Equal([typeof(IB), typeof(IC)], stack.Items);
        Assert.Equal(typeof(IC), stack.Pop());
        Assert.Equal(typeof(IB), stack.Top);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackStack(0));
    }

    [Fact]
    public void An_empty_stack_pops_null()
    {
        var stack = new BackStack();
        Assert.Null(stack.Pop());
        Assert.Null(stack.Top);
    }

    [Fact]
    public void The_presenter_slot_forwards_to_the_one_host_it_serves()
    {
        var slot = new ScreenPresenterSlot();
        Assert.Throws<InvalidOperationException>(() => slot.Present(typeof(IA)));

        var host = new RecordingPresenter();
        slot.Attach(host);
        slot.Attach(host);
        Assert.True(slot.IsAttached);
        Assert.IsType<Screen>(slot.Present(typeof(IA)));
        Assert.Equal([typeof(IA)], host.Presented);
        Assert.Throws<InvalidOperationException>(() => slot.Attach(new RecordingPresenter()));
    }
}
