using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotCharter.Localization;

namespace DotCharter.Tests.Localization;

/// <summary>
///     A language-switch subscription must not outlive its owner on the localization service, which
///     lives as long as the app: a pedalboard rebuild replaces every parameter view model, each of
///     which subscribed, and a long session without a language switch must not pile up their handlers.
/// </summary>
public sealed class WeakLanguageSubscriptionTests
{
    private const int Rebuilds = 50;
    private const int ParamsPerRebuild = 40;

    [Fact]
    public void ManyRebuildsWithoutALanguageSwitch_LeaveABoundedNumberOfHandlers()
    {
        var localization = new CountingLocalization();
        var live = new Owner();
        WeakLanguageSubscription.Subscribe(localization, live, static o => o.Calls++);

        for (var rebuild = 0; rebuild < Rebuilds; rebuild++)
        {
            SubscribeDiscardedOwners(localization, ParamsPerRebuild);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Every rebuild's owners are gone; at most one rebuild's worth (plus the live owner) may wait to be pruned.
        Assert.True(localization.HandlerCount <= 2,
            $"{localization.HandlerCount} handlers on the localization after {Rebuilds} rebuilds");
        Assert.True(WeakLanguageSubscription.CountFor(localization) <= 2 * ParamsPerRebuild + 1,
            $"{WeakLanguageSubscription.CountFor(localization)} subscriptions held after {Rebuilds} rebuilds");

        localization.Switch();
        Assert.Equal(1, live.Calls);
        Assert.Equal(1, WeakLanguageSubscription.CountFor(localization));
        GC.KeepAlive(live);
    }

    [Fact]
    public void ALiveOwnerIsCalledOnEverySwitch_InTheOrderItSubscribed()
    {
        var localization = new CountingLocalization();
        var calls = new List<int>();
        var owners = Enumerable.Range(0, 3).Select(i => new Owner { Id = i, Log = calls }).ToList();
        foreach (var owner in owners) WeakLanguageSubscription.Subscribe(localization, owner, static o => o.Log!.Add(o.Id));

        localization.Switch();
        localization.Switch();

        Assert.Equal([0, 1, 2, 0, 1, 2], calls);
        GC.KeepAlive(owners);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SubscribeDiscardedOwners(ILocalizationService localization, int count)
    {
        for (var i = 0; i < count; i++)
            WeakLanguageSubscription.Subscribe(localization, new Owner(), static o => o.Calls++);
    }

    private sealed class Owner
    {
        public int Id { get; init; }
        public List<int>? Log { get; init; }
        public int Calls { get; set; }
    }

    /// <summary>Counts the handlers attached to <see cref="PropertyChanged" />.</summary>
    private sealed class CountingLocalization : ILocalizationService
    {
        private PropertyChangedEventHandler? _handlers;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public int HandlerCount => _handlers?.GetInvocationList().Length ?? 0;

        public IReadOnlyList<LanguageOption> LanguageOptions { get; } = [];
        public string ConfiguredCulture => LocalizationService.SystemLanguage;
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public bool IsRightToLeft => false;
        public string this[string key] => key;

        public string Format(string key, params object[] args) => key;

        public string Plural(string oneKey, string manyKey, int count, params object[] extra) => count == 1 ? oneKey : manyKey;

        public string PluralWith(string oneKey, string manyKey, int count, params object[] args) => count == 1 ? oneKey : manyKey;

        public void SetConfiguredCulture(string? configValue) => Switch();

        public void LoadCommunityLanguages(string folder)
        {
        }

        public void Switch() => _handlers?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
