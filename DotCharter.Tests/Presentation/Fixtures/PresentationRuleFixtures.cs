// Deliberately bad (and deliberately fine) shapes for PresentationTypeRulesTests'
// probe facts. Nothing here is used by product code.

namespace DotCharter.Tests.Presentation.Fixtures.Alpha
{
    internal sealed class EightDependencies(int a, int b, int c, int d, int e, int f, int g, int h)
    {
        public int Sum => a + b + c + d + e + f + g + h;
    }

    internal sealed class SevenDependencies(int a, int b, int c, int d, int e, int f, int g)
    {
        public int Sum => a + b + c + d + e + f + g;
    }

    /// <summary>Holds another area's screen VM (bad), its own row VM and a Shared state VM (both fine).</summary>
    internal sealed class SongListViewModel(
        IReadOnlyList<Beta.ResultsViewModel> results,
        Beta.Shared.SessionViewModel session)
    {
        private readonly Beta.ResultsViewModel? _results = null;
        private readonly List<SongRowViewModel> _rows = [];

        public int Count => results.Count + _rows.Count + (_results == null ? 0 : 1) + (session == null ? 0 : 1);
    }

    internal sealed class SongRowViewModel;
}

namespace DotCharter.Tests.Presentation.Fixtures.Beta
{
    internal sealed class ResultsViewModel;
}

namespace DotCharter.Tests.Presentation.Fixtures.Beta.Shared
{
    internal sealed class SessionViewModel;
}
