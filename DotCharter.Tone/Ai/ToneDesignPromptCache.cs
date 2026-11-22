namespace DotCharter.Tone.Ai;

/// <summary>Bounds catalogue-sized prompts by the eight most recently used capture libraries.
/// Empty-library prompts have a constant size and remain cached across library edits.</summary>
internal sealed class ToneDesignPromptCache
{
    internal const int MaxCachedCaptureLibraries = 8;

    private readonly Dictionary<(bool Anchored, bool CaptureSearch, string Fingerprint), string> _prompts = [];
    private readonly LinkedList<string> _recent = [];
    private readonly Lock _gate = new();

    internal int Count
    {
        get
        {
            lock (_gate) return _prompts.Count;
        }
    }

    internal string GetOrAdd(NamCaptureCatalog captures, bool anchored, bool captureSearch, Func<string> build)
    {
        var key = (anchored, captureSearch, captures.Fingerprint);
        lock (_gate)
        {
            if (_prompts.TryGetValue(key, out var cached))
            {
                Touch(captures);
                return cached;
            }
        }

        var built = build();
        lock (_gate)
        {
            if (!_prompts.TryAdd(key, built)) built = _prompts[key];
            Touch(captures);
            while (_recent.Count > MaxCachedCaptureLibraries)
            {
                var evicted = _recent.First!.Value;
                _recent.RemoveFirst();
                _prompts.Remove((false, false, evicted));
                _prompts.Remove((false, true, evicted));
                _prompts.Remove((true, false, evicted));
                _prompts.Remove((true, true, evicted));
            }

            return built;
        }
    }

    private void Touch(NamCaptureCatalog captures)
    {
        if (captures.IsEmpty) return;
        _recent.Remove(captures.Fingerprint);
        _recent.AddLast(captures.Fingerprint);
    }
}
