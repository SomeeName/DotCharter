namespace DotCharter.Presentation.Fakes;

/// <summary>One named state of a fake screen; <c>Create</c> returns a new instance on every call.</summary>
public sealed record FakeVariant<TContract>(string Name, Func<TContract> Create)
    where TContract : class;

/// <summary>The variant names of target-architecture.md § 3; any other name is a screen's own extra.</summary>
public static class FakeVariantNames
{
    public const string Default = nameof(Default);
    public const string Empty = nameof(Empty);
    public const string LongText = nameof(LongText);
    public const string Full = nameof(Full);
    public const string Busy = nameof(Busy);
    public const string Error = nameof(Error);
}
